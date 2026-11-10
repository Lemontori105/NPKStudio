using NPKStudio.Core.Formats;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;

namespace NPKStudio.Desktop.ViewModels.Channels;

/// <summary>
///     通道的<b>文档生命周期</b>：打开 / 加载 / 保存 / 打包 / 资源清单，以及"这份文档是谁"的标识
///     （记忆键、上次打开是否成功、帧解析失败原因）。
///     <para>
///         <b>为什么单独成类</b>：这些操作的前置条件是"有会话、有工作区、要写盘"，与"帧怎么编辑、
///         画布怎么显示"无关。留在 <see cref="FrameChannel" /> 里会让聚合根重新长到 900 行。
///     </para>
///     <para>
///         <b>与通道的通信是直接方法调用，不是事件 / 委托</b>：回写状态就写
///         <see cref="FrameChannel.Status" /> / <see cref="FrameChannel.IsBusy" />，
///         需要通知就调 <see cref="FrameChannel.Notify" />。sealed 类型上的非虚调用，
///         JIT 可去虚拟化并内联，零委托分配、零多播。
///     </para>
///     <para>
///         <b>不持有帧级对象</b>：帧列表、选中帧、撤销栈一律经 <see cref="FrameChannel" /> /
///         <see cref="ChannelFrameEditor" /> 间接操作，本类只做编排。
///     </para>
/// </summary>
internal sealed class ChannelDocument
{
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private readonly FrameChannel _owner;
    private readonly DocumentSession _session;
    private readonly FileCommandPresenter _files;

    /// <summary>实验性版本"写回前确认一次"的会话记忆（同一 IMG 只问一次，切 IMG 重置）。</summary>
    private readonly ExperimentalSaveConfirmation _saveConfirmation = new();

    /// <summary>最近一次帧解析失败的原因；成功后清空（帧区空状态据此区分"版本不支持"和"解析炸了"）。</summary>
    private string? _frameLoadError;

    /// <summary>构造。</summary>
    /// <param name="owner">聚合根（回写状态栏 / 忙碌标记与通知的唯一出口）。</param>
    /// <param name="session">文档会话（真正的解析与解码在这一层之下）。</param>
    /// <param name="workspace">文件工作区（保存 / 另存 / 打包都经它）。</param>
    /// <param name="paths">路径库（资源清单的标准来源）。</param>
    public ChannelDocument(FrameChannel owner, DocumentSession session, FileWorkspace workspace, PathLibrary paths) {
        _owner = owner;
        _session = session;
        _files = new FileCommandPresenter(session, workspace, paths);
    }

    /// <summary>最近一次帧解析失败的原因；<c>null</c> 表示没有失败。</summary>
    internal string? FrameLoadError => _frameLoadError;

    /// <summary>当前 IMG 的记忆键：文件夹模式用「文件夹名/条目路径」，NPK 模式用「包名/条目路径」。</summary>
    public string? CurrentImageKey {
        get {
            var entry = _owner.SelectedEntry;
            if (entry is null) return null;
            if (_session.Folder is not null) return $"{_session.Folder.Name}/{entry.Path}";
            if (_session.Archive is not null) return $"{Path.GetFileName(_session.Archive.FilePath)}/{entry.Path}";
            return null;
        }
    }

    /// <summary>最近一次打开是否成功（供自动化脚本判断退出码；GUI 不依赖它）。</summary>
    public bool LastOpenSucceeded => _session.LastOpenSucceeded;

    /// <summary>换 IMG 后忘记"这次已经确认过实验性写回"，下次保存要重新问。</summary>
    internal void ResetSaveConfirmation() => _saveConfirmation.Reset();

    // ── 打开 / 加载 ────────────────────────────────────────────────────────────

    /// <summary>打开文件或文件夹：目录走 <see cref="FolderEntrySource" />，其余按 NPK / IMG 处理。</summary>
    public async Task OpenAsync(string path) {
        _owner.Stop();
        _owner.MarkAudioSaved();   // 换文件：旧包里没保存的音效替换随旧包丢弃（和帧编辑一样，上游打开新文件时不提示保存）
        _owner.IsBusy = true;
        _owner.Status = Loc["status.parsingIndex"];
        try {
            var entries = await _session.OpenAsync(path);

            _owner.EntryFilter = string.Empty;
            _owner.LoadEntries(entries);

            _owner.FileName = _session.FileName;
            NotifyModeChanged();
            _owner.Status = _session.Folder is not null
                ? (_session.Folder.SkippedCount > 0
                    ? Loc.F("status.openedFolderSkipped", _owner.FileName, entries.Count, _session.Folder.SkippedCount)
                    : Loc.F("status.openedFolder", _owner.FileName, entries.Count))
                : Loc.F("status.opened", _owner.FileName, entries.Count);

            _owner.SelectedEntry = _owner.FilteredEntries.FirstOrDefault();
            if (_owner.PendingFrameLoad is not null) {
                await _owner.PendingFrameLoad;
            }
        } catch (Exception ex) {
            _session.MarkOpenFailed();
            _owner.Status = Loc.F("status.openFailed", ex.Message);
            ResetAll();
        } finally {
            _owner.IsBusy = false;
        }
    }

    /// <summary>打开一个"NPK 源目录"（与 <see cref="OpenAsync" /> 分流）。</summary>
    public Task OpenFolderAsync(string path) => OpenAsync(path);

    /// <summary>解析当前选中条目的 IMG，填充帧列表并选中首帧。</summary>
    internal async Task LoadFramesAsync(EntryItem? item) {
        if (item is null || (_session.Archive is null && _session.Folder is null)) {
            _owner.Frames.Clear();
            _owner.SelectedFrames.Clear();
            _session.ClearImage();
            _owner.View.ClearCurrentImage();
            _owner.View.ResetFrameCache();
            _owner.View.ResetCanvasBounds(null);
            _owner.Editor.Detach();
            _frameLoadError = null;
            NotifyVersionSupport();
            _owner.Editor.RefreshChangeMarkers();
            return;
        }

        _owner.IsBusy = true;
        _owner.Status = Loc.F("status.parsingImage", item.Name);

        // 先丢掉上一个 IMG 的位图缓存：既释放内存，也让在途的预解任务按代次立即停手。
        _owner.View.ResetFrameCache();
        try {
            var result = await _session.LoadFramesAsync(item);

            if (!ReferenceEquals(_owner.SelectedEntry, item)) return;

            _frameLoadError = null;
            _owner.Frames.Clear();
            _owner.SelectedFrames.Clear();
            _owner.ResetFrameHighlight();
            foreach (var frame in result.Frames) {
                _owner.Frames.Add(frame);
            }

            _owner.View.ResetCanvasBounds(result.Image);

            // 新 IMG 的历史栈：以"刚加载完"为基线，IsDirty 归零；变更标记基线也在这里拍
            _owner.Editor.Attach();
            _owner.Editor.RefreshChangeMarkers();

            if (result.Image is not null) {
                _owner.Status = Loc.F("status.frameLoaded", item.Name, result.Frames.Count, (int)result.Image.Version);

                // 后台预解：把首圈播放的解码开销提前摊掉（预算内全量，超预算自动停手）。
                _owner.View.PrefetchFrames(result.Image, result.Frames);
            }

            NotifyVersionSupport();

            // 放到最后：选中帧会触发 UpdateCurrentImage，可能写入"线减大面积透明"提示，不能被覆盖
            _owner.SelectedFrame = result.Selected;

            // 帧列表此刻已经入列（首屏可见），再等后台缩略图收尾：
            // 既不拖慢首屏，也不让调度回调跑到本次加载之外。
            try {
                await result.Thumbnails;
            } catch {
                // 缩略图失败不影响文档可用性（画布与导出各自会如实报错）
            }
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            _owner.Status = Loc.F("status.frameParseFailed", ex.Message);

            // 先记错误：帧区空状态要据此显示"解析失败：…"而不是"该版本不支持"
            _frameLoadError = ex.Message;
            _owner.Frames.Clear();
            _owner.SelectedFrames.Clear();
            _session.ClearImage();
            _owner.View.ClearCurrentImage();
            _owner.View.ResetFrameCache();
            _owner.View.ResetCanvasBounds(null);
            _owner.Editor.Detach();
            NotifyVersionSupport();
            _owner.Editor.RefreshChangeMarkers();
        } finally {
            _owner.IsBusy = false;
        }
    }

    /// <summary>清空整个通道的文档状态（打开失败 / 关闭时调用）。</summary>
    private void ResetAll() {
        _owner.EntryFilter = string.Empty;
        _owner.LoadEntries([]);
        _owner.Frames.Clear();
        _owner.SelectedFrames.Clear();
        _session.Dispose();
        _owner.View.ClearCurrentImage();
        _owner.View.ResetFrameCache();
        _owner.FileName = "NPKStudio";
        _owner.Editor.Detach();
        _frameLoadError = null;
        _saveConfirmation.Reset();
        NotifyModeChanged();
        _owner.Editor.RefreshChangeMarkers();
        NotifyVersionSupport();
    }

    // ── 保存 / 导出 ────────────────────────────────────────────────────────────

    /// <summary>导出当前帧为 PNG（永远导出原始解码结果）。</summary>
    public void ExportCurrentPng(string path) =>
        _owner.Status = _files.ExportCurrentPng(_owner.View.RawImage, _owner.View.CurrentImage, path).StatusMessage;

    /// <summary>保存：NPK 模式另存；文件夹模式原子写回原 .img。</summary>
    public async Task SaveAsync(string? path = null) {
        _owner.Stop();

        // 实验性版本（v4/v5/v6）写回可能降质：编辑过就先确认一次（同一 IMG 只问一次）
        if (!await ConfirmExperimentalWriteAsync()) {
            return;
        }

        _owner.Status = Loc["status.saving"];
        _owner.IsBusy = true;   // 保存期间禁用条目列表：否则会在后台重开句柄 / 重建条目表时读到旧偏移
        try {
            var r = await _files.SaveAsync(path, _owner.SelectedEntry);
            _owner.Status = r.StatusMessage;

            // 写回成功才清脏：失败时继续保留 ●；同时拍新基线，所有变更标记随之归零
            if (r.Success) {
                _owner.Editor.MarkSaved();
                _owner.Editor.RefreshChangeMarkers();
                _owner.MarkAudioSaved();
                _owner.NotifyAudio();
            }
        } finally {
            _owner.IsBusy = false;
        }
    }

    /// <summary>
    ///     实验性版本写回前的确认：只有"版本实验性 + 编辑过 + 宿主允许弹窗 + 还没确认过"才会问。
    ///     <para>回调为空说明宿主不允许弹窗（自动化 / 无头），按"继续"处理。</para>
    /// </summary>
    private async Task<bool> ConfirmExperimentalWriteAsync() {
        if (_session.CurrentImg is not { } img) return true;
        if (!_saveConfirmation.ShouldConfirmExperimentalSave(
                CurrentImageKey, img.Version, _owner.Editor.IsDirty, _owner.ConfirmExperimentalSave is not null)) {
            return true;
        }

        if (_owner.ConfirmExperimentalSave is { } confirm && !await confirm(img.Version)) {
            _owner.Status = Loc["status.experimentalSaveCancelled"];
            return false;
        }

        _saveConfirmation.MarkConfirmed(CurrentImageKey);
        return true;
    }

    /// <summary>把当前包另存到指定路径（未修改条目按原始字节直拷）。</summary>
    public async Task SaveArchiveAsync(string path) {
        _owner.Stop();
        if (_session.Archive is null) {
            _owner.Status = Loc["status.noArchive"];
            return;
        }

        _owner.Status = Loc["status.saving"];
        _owner.IsBusy = true;
        try {
            var r = await _files.SaveArchiveAsync(path);
            _owner.Status = r.StatusMessage;
            if (r.Success) {
                _owner.Editor.MarkSaved();
                _owner.Editor.RefreshChangeMarkers();
                _owner.MarkAudioSaved();
                _owner.NotifyAudio();
            }
        } finally {
            _owner.IsBusy = false;
        }
    }

    /// <summary>把"NPK 源目录 / 单个 img"打包成一个 NPK。</summary>
    public async Task ExportFolderToNpkAsync(FolderEntrySource folder, string? manifestPath, string outputPath) {
        _owner.Stop();
        _owner.IsBusy = true;
        _owner.Status = Loc["status.exportingNpk"];
        try {
            _owner.Status = (await _files.ExportFolderToNpkAsync(folder, manifestPath, outputPath)).StatusMessage;
        } finally {
            _owner.IsBusy = false;
        }
    }

    // ── 资源清单 ───────────────────────────────────────────────────────────────

    /// <summary>已记住的资源清单路径；<c>null</c> 表示还没设置过。</summary>
    public string? ManifestPath => _files.ManifestPath;

    /// <summary>记住（或清除）资源清单，立即落盘。</summary>
    public string SetManifest(string? path) {
        var status = _files.SetManifest(path);
        _owner.Notify(nameof(ManifestPath));
        return status;
    }

    /// <summary>扫描一个装满 NPK 的目录，反向生成资源清单（清单的标准来源）。</summary>
    public async Task GenerateManifestAsync(string npkDirectory, string outputPath) {
        _owner.Stop();
        _owner.IsBusy = true;
        _owner.Status = Loc["status.generatingManifest"];
        try {
            _owner.Status = (await _files.GenerateManifestAsync(npkDirectory, outputPath)).StatusMessage;
        } finally {
            _owner.IsBusy = false;
        }
    }

    // ── 内部同步 ───────────────────────────────────────────────────────────────

    private void NotifyModeChanged() {
        _owner.Notify(nameof(FrameChannel.HasArchive));
        _owner.Notify(nameof(FrameChannel.IsFolderMode));
        _owner.Notify(nameof(FrameChannel.CanSave));
        _owner.Notify(nameof(FrameChannel.ModeText));
        _owner.Notify(nameof(FrameChannel.SaveTooltip));
    }

    /// <summary>版本支持等级相关的派生属性整体刷新（切换 / 清空 IMG 后调用，0 帧时尤为关键）。</summary>
    private void NotifyVersionSupport() {
        _owner.Notify(nameof(FrameChannel.IsPassthroughVersion));
        _owner.Notify(nameof(FrameChannel.IsExperimentalVersion));
        _owner.Notify(nameof(FrameChannel.CanEditFrames));
        _owner.Notify(nameof(FrameChannel.CanTrim));
        _owner.Notify(nameof(FrameChannel.FrameAreaEmptyText));
        _owner.NotifyAudio();
    }
}
