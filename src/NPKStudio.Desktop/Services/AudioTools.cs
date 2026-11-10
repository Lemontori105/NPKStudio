using System.Diagnostics;
using NPKStudio.Core.Audio;
using NPKStudio.Core.Diagnostics;

namespace NPKStudio.Desktop.Services;

/// <summary>外部命令执行结果。</summary>
/// <param name="ExitCode">退出码（-1 表示没能启动或超时被杀）。</param>
/// <param name="Error">标准错误输出（截断到 2000 字）。</param>
internal readonly record struct ToolRun(int ExitCode, string Error);

/// <summary>转换结果：成功时 <see cref="Ogg" /> 是可直接写进 NPK 的 Ogg Vorbis 字节。</summary>
/// <param name="Ogg">Ogg Vorbis 字节；失败时为 <c>null</c>。</param>
/// <param name="Reencoded">是否重新编码过（原文件已经是参数一致的 Ogg Vorbis 时原样使用）。</param>
/// <param name="Error">失败原因（工具的报错首行或异常信息）。</param>
/// <param name="MissingTool">缺少的命令名（ffmpeg / oggenc）；不缺时为 <c>null</c>。</param>
internal readonly record struct AudioConvertResult(byte[]? Ogg, bool Reencoded, string? Error, string? MissingTool = null);

/// <summary>
///     借本机命令行工具处理音效：<c>ffplay</c> 试听，<c>ffmpeg</c> + <c>oggenc</c> 转成 Ogg Vorbis。
///     <para>
///         工具不随程序分发。macOS 用 Homebrew 装 <c>ffmpeg</c>（自带 ffplay）和 <c>vorbis-tools</c>（oggenc）。
///         Homebrew 的 ffmpeg 不带 libvorbis 编码器（自带的 vorbis 编码器是实验性的），所以编码交给 oggenc。
///         系统自带的 afplay 不支持 Vorbis，不能用来试听。
///     </para>
///     <para>所有参数都走 <see cref="ProcessStartInfo.ArgumentList" />，不经过 shell，文件名里有空格 / 引号也安全。</para>
/// </summary>
internal sealed class AudioTools : IDisposable {
    private static readonly string[] ExtraSearchDirs = ["/opt/homebrew/bin", "/usr/local/bin"];
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(2);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NPKStudio-audio");
    private Process? _player;
    private string? _previewFile;

    /// <summary>试听进程结束（播完、被停止、出错都算）。在线程池线程上触发，订阅方自己切回 UI 线程。</summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>是否正在试听。</summary>
    public bool IsPlaying => _player is { HasExited: false };

    /// <summary>
    ///     找命令：先找 PATH，再找 Homebrew 的常见目录（从 Finder 双击启动的 app 拿不到终端里的 PATH）。
    /// </summary>
    /// <returns>可执行文件的完整路径；找不到返回 <c>null</c>。</returns>
    public static string? Find(string tool) {
        var name = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraSearchDirs);
        foreach (var dir in dirs) {
            try {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            } catch (ArgumentException) {
                // PATH 里有非法字符的段：跳过
            }
        }

        return null;
    }

    /// <summary>试听一段 Ogg（先停掉正在播的）。</summary>
    /// <param name="ogg">音效字节。</param>
    /// <param name="error">失败原因；找不到 ffplay 时为 <c>null</c>，看 <paramref name="missingTool" />。</param>
    /// <param name="missingTool">缺少的命令名；不缺时为 <c>null</c>。</param>
    /// <returns>是否开始播放。</returns>
    public bool Play(ReadOnlyMemory<byte> ogg, out string? error, out string? missingTool) {
        Stop();
        error = null;
        missingTool = null;

        var ffplay = Find("ffplay");
        if (ffplay is null) {
            missingTool = "ffplay";
            return false;
        }

        try {
            Directory.CreateDirectory(_tempDir);
            _previewFile = Path.Combine(_tempDir, $"preview-{Environment.ProcessId}.ogg");
            File.WriteAllBytes(_previewFile, ogg.ToArray());

            var info = new ProcessStartInfo(ffplay) {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", _previewFile }) {
                info.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += (_, _) => {
                if (ReferenceEquals(_player, process)) {
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                }
            };
            process.Start();
            _player = process;
            return true;
        } catch (Exception ex) {
            TraceLog.Instance.Warn("启动 ffplay 失败", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>停止试听（没在播放时什么也不做）。</summary>
    public void Stop() {
        var process = _player;
        _player = null;
        if (process is null) return;

        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch (Exception ex) {
            TraceLog.Instance.Warn("停止 ffplay 失败", ex);
        } finally {
            process.Dispose();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     把任意音频文件变成可以写进 NPK 的 Ogg Vorbis。
    ///     <para>
    ///         已经是 Ogg Vorbis、且采样率 / 声道与要求一致（或没有要求）时原样使用，不重新编码；
    ///         否则 ffmpeg 解码成 16 位 PCM wav（按要求重采样 / 改声道），再 oggenc -q 5 编码。
    ///     </para>
    /// </summary>
    /// <param name="inputPath">源文件（wav / mp3 / flac / m4a / ogg …，ffmpeg 能读的都行）。</param>
    /// <param name="sampleRate">目标采样率；&lt;= 0 表示保持源文件的。</param>
    /// <param name="channels">目标声道数；&lt;= 0 表示保持源文件的。</param>
    public async Task<AudioConvertResult> ToOggVorbisAsync(string inputPath, int sampleRate, int channels) {
        byte[] source;
        try {
            source = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        } catch (Exception ex) {
            return new AudioConvertResult(null, false, ex.Message);
        }

        var sourceInfo = OggAudioInfo.TryParse(source);
        if (sourceInfo is { Codec: OggCodec.Vorbis }
            && (sampleRate <= 0 || sourceInfo.SampleRate == sampleRate)
            && (channels <= 0 || sourceInfo.Channels == channels)) {
            return new AudioConvertResult(source, false, null);
        }

        var ffmpeg = Find("ffmpeg");
        var oggenc = Find("oggenc");
        if (ffmpeg is null || oggenc is null) {
            return new AudioConvertResult(null, false, null, ffmpeg is null ? "ffmpeg" : "oggenc");
        }

        Directory.CreateDirectory(_tempDir);
        var stem = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        var wav = stem + ".wav";
        var ogg = stem + ".ogg";
        try {
            var decodeArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, "-vn" };
            if (sampleRate > 0) decodeArgs.AddRange(["-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (channels > 0) decodeArgs.AddRange(["-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            decodeArgs.AddRange(["-c:a", "pcm_s16le", wav]);

            var decode = await RunAsync(ffmpeg, decodeArgs).ConfigureAwait(false);
            if (decode.ExitCode != 0 || !File.Exists(wav)) {
                return new AudioConvertResult(null, false, FirstLine(decode.Error, "ffmpeg"));
            }

            var encode = await RunAsync(oggenc, ["-Q", "-q", "5", "-o", ogg, wav]).ConfigureAwait(false);
            if (encode.ExitCode != 0 || !File.Exists(ogg)) {
                return new AudioConvertResult(null, false, FirstLine(encode.Error, "oggenc"));
            }

            var result = await File.ReadAllBytesAsync(ogg).ConfigureAwait(false);
            return OggAudioInfo.TryParse(result) is { Codec: OggCodec.Vorbis }
                ? new AudioConvertResult(result, true, null)
                : new AudioConvertResult(null, true, "oggenc");
        } finally {
            TryDelete(wav);
            TryDelete(ogg);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Stop();
        if (_previewFile is not null) TryDelete(_previewFile);
    }

    private static async Task<ToolRun> RunAsync(string exe, IEnumerable<string> args) {
        var info = new ProcessStartInfo(exe) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        try {
            process.Start();
        } catch (Exception ex) {
            return new ToolRun(-1, ex.Message);
        }

        // 两个管道都要读走，否则输出塞满缓冲区时子进程会卡住
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ConvertTimeout);
        try {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // 已经退出
            }

            return new ToolRun(-1, $"{Path.GetFileName(exe)} timeout");
        }

        await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new ToolRun(process.ExitCode, error.Length > 2000 ? error[..2000] : error);
    }

    private static string FirstLine(string text, string fallback) {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? fallback : line;
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch (IOException ex) {
            TraceLog.Instance.Warn($"清理临时文件失败：{path}", ex);
        } catch (UnauthorizedAccessException ex) {
            TraceLog.Instance.Warn($"清理临时文件失败：{path}", ex);
        }
    }
}
