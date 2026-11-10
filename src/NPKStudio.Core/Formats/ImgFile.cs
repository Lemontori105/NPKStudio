using System.Text;

using NPKStudio.Core.Exceptions;
using NPKStudio.Core.Formats.Handlers;
using NPKStudio.Core.Formats.Npk;
using NPKStudio.Core.Imaging;

namespace NPKStudio.Core.Formats;

/// <summary>
///     一个 IMG 文件（`*.img`）的内存模型：版本、索引区长度、帧列表、调色板。
///     <para>本类型是纯托管 POCO，不实现 <c>INotifyPropertyChanged</c>，可跨线程传递（见架构 §5）。</para>
/// </summary>
public sealed class ImgFile
{
    /// <summary>v2 系公共头长度：魔数 16 + IndexLength 8 + version 4 + count 4。</summary>
    public const int CommonHeaderSize = 32;

    private readonly FrameTable _frames = new();
    private readonly List<Color32> _palette = [];
    private readonly List<IReadOnlyList<Color32>> _palettes = [];
    private readonly List<ImgTexture> _textures = [];

    private ImgFile(string path)
    {
        Path = path;
    }

    /// <summary>
    ///     IMG 的相对路径（NPK 内路径或文件名）。
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    ///     版本号。
    /// </summary>
    public ImgVersion Version { get; internal set; } = ImgVersion.Ver2;

    /// <summary>
    ///     索引区语义：v1 = 头部那个恒 0 的保留字段（读侧记录其值）；v2/v4/v6 = 索引区字节数；v5 = v2 帧索引区字节数。
    /// </summary>
    public long IndexLength { get; internal set; }

    /// <summary>
    ///     帧列表（顺序即文件中的顺序）。
    /// </summary>
    public IReadOnlyList<ImgFrame> Frames => _frames;

    /// <summary>
    ///     主调色板（v4/v5/v6 有内容，v1/v2 为空）。
    /// </summary>
    public IReadOnlyList<Color32> Palette => _palette;

    /// <summary>
    ///     v6 的全部色表。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Color32>> Palettes => _palettes;

    /// <summary>
    ///     v5 的纹理页表（其它版本恒为空）。与 <see cref="Palettes" /> 一样由 handler 在读取时填充。
    ///     <para>
    ///         帧通过 <see cref="ImgFrame.Texture" /> 的 <see cref="TextureRef.TextureIndex" /> 引用这里的某一页，
    ///         因此本列表<b>顺序即下标</b>，不允许在中间插入/删除（只在末尾追加新页）。
    ///     </para>
    /// </summary>
    internal IReadOnlyList<ImgTexture> Textures => _textures;

    /// <summary>
    ///     头部声明的帧数（读取时由文件头给出）。
    /// </summary>
    internal int DeclaredFrameCount { get; set; }

    /// <summary>
    ///     v5 头里那个语义不明的长度字段（<c>textureCount</c> 之后的第二个 int32）。
    ///     <para>
    ///         旧实现写的是 <c>Album.Length</c>（<c>data.Length + 40</c>，比真实总长多 8 字节），
    ///         且没有任何读取方使用它。这里<b>原样保留</b>：不认识语义的字段就不要自己编一个值，
    ///         否则"打开再另存"会平白改动文件字节。<c>null</c> 表示本文件是新建的，写出时按真实总长计算。
    ///     </para>
    /// </summary>
    internal int? V5DeclaredLength { get; set; }

    /// <summary>
    ///     本文件对应的读写策略。
    /// </summary>
    internal IImgHandler Handler { get; set; } = null!;

    /// <summary>
    ///     非 IMG 条目（如 .ogg）的原始内容，仅 <c>ImgVersion.Other</c> 使用。
    /// </summary>
    internal byte[] RawPayload { get; set; } = [];

    /// <summary>
    ///     非 IMG 条目（如 .ogg 音效）的完整原始内容（只读视图）；IMG 条目（含未实现版本）返回空。
    /// </summary>
    public ReadOnlyMemory<byte> RawContent => Version == ImgVersion.Other ? RawPayload : ReadOnlyMemory<byte>.Empty;

    /// <summary>
    ///     整段替换非 IMG 条目（如 .ogg 音效）的内容，并把所属 NPK 条目标记为已修改：保存时写入新字节，
    ///     其余条目照旧按原始字节直拷。
    /// </summary>
    /// <param name="content">新内容（原样写入，不做任何转换）。</param>
    /// <exception cref="NotSupportedException">当前条目是 IMG（包括未实现的版本），不能整段替换。</exception>
    public void ReplaceRawContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Version != ImgVersion.Other)
        {
            throw new NotSupportedException($"只有非 IMG 条目（如 .ogg）可以整段替换，当前是 {Version}。");
        }

        RawPayload = content;
        IndexLength = content.Length;
        MarkModified();
    }

    /// <summary>
    ///     新建一个空 IMG（供"新建/导入"流程使用）。
    /// </summary>
    /// <param name="version">目标版本，必须是已注册的版本。</param>
    /// <param name="path">相对路径。</param>
    /// <returns>空帧列表的 <see cref="ImgFile" />。</returns>
    public static ImgFile Create(ImgVersion version, string path)
    {
        var file = new ImgFile(path)
        {
            Version = version,
            Handler = ImgHandlerRegistry.Get(version)
        };
        return file;
    }

    /// <summary>
    ///     追加一帧并返回它（下标自动分配）。
    /// </summary>
    /// <returns>新帧，字段为默认值。</returns>
    public ImgFrame AddFrame() => _frames.Append();
    /// <summary>
    ///     在指定下标插入一帧 1×1 全透明帧（REQ-29）。
    ///     <para>用 1×1 透明而不是 0×0：0 尺寸帧会让解码与缩略图都进特例分支，而 1×1 是格式合法的最小表示。</para>
    /// </summary>
    /// <param name="index">插入位置，<c>[0, Frames.Count]</c>。</param>
    /// <returns>新插入的帧。</returns>
    /// <exception cref="ArgumentOutOfRangeException">下标越界。</exception>
    /// <exception cref="NotSupportedException">当前版本没有可用的 handler（只有 <c>OtherHandler</c> 会拒绝）。</exception>
    public ImgFrame InsertFrame(int index)
    {
        EnsureEditable();
        var frame = _frames.Insert(index, FramePixelEncoder.BytesPerPixel(Version));
        if (FramePixelEncoder.IsPaletteIndexed(Version))
        {
            FramePixelEncoder.FillBlankFrame(this, frame);
        }
        else if (Version == ImgVersion.Ver5)
        {
            // v5 的帧像素只能来自纹理页：空帧也必须落成一张 1×1 纹理，
            // 否则会写出"内联 2 字节数据"的 v2 式帧，虽然可读但与整表布局不一致。
            FramePixelEncoder.WritePixels(this, frame, new RgbaImage(1, 1));
        }

        MarkModified();
        return frame;
    }

    /// <summary>
    ///     删除一帧，并把指向它的 LINK 安全地搬走（REQ-09）。
    ///     <para>
    ///         ① 被删的是普通帧 → 第一个引用它的 LINK 帧<b>顶替</b>它的像素，其余引用者改为指向顶替者；
    ///         ② 被删的是 LINK 帧 → 引用者改指向它原本的目标（目标也断了则降级为 1×1 透明帧）。
    ///     </para>
    ///     <para>两种情形都保证删除后其它帧的渲染结果不变。</para>
    /// </summary>
    /// <param name="frame">要删除的帧。</param>
    /// <returns>是否真的删除了（帧不属于本文件时返回 <c>false</c>）。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    public bool RemoveFrame(ImgFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureEditable();

        if (!_frames.Contains(frame))
        {
            return false;
        }

        FrameLinks.DetachReferrers(_frames, frame, FramePixelEncoder.BytesPerPixel(Version));
        _frames.Remove(frame);
        MarkModified();
        return true;
    }

    /// <summary>
    ///     调整帧在 IMG 内的顺序（REQ-25 的数据层支撑）。
    ///     <para>
    ///         <see cref="ImgFrame.LinkTarget" /> 是对象引用，移动不会改变链接关系；
    ///         移动后由 <see cref="FrameTable.Renumber" /> 重排下标，写 LINK 时才不会指错。
    ///     </para>
    /// </summary>
    /// <param name="fromIndex">源下标。</param>
    /// <param name="toIndex">目标下标（移动后该帧所处的位置）。</param>
    /// <returns>是否发生了移动。</returns>
    /// <exception cref="ArgumentOutOfRangeException">下标越界。</exception>
    public bool MoveFrame(int fromIndex, int toIndex)
    {
        EnsureEditable();
        if (!_frames.Move(fromIndex, toIndex))
        {
            return false;
        }

        MarkModified();
        return true;
    }

    /// <summary>改帧在角色坐标系中的位置（REQ-10）。只改索引表字段，O(1)、零分配。</summary>
    /// <param name="frame">目标帧。</param>
    /// <param name="x">新的 X，允许为负。</param>
    /// <param name="y">新的 Y，允许为负。</param>
    /// <returns>是否有变化。</returns>
    public bool SetFramePosition(ImgFrame frame, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureOwner(frame);
        return FrameGeometry.SetPosition(this, frame, x, y);
    }

    /// <summary>把一帧的像素缩放到指定尺寸（REQ-10 的改宽高，会重采样并重编码）。</summary>
    /// <param name="frame">目标帧，不能是 LINK 帧。</param>
    /// <param name="width">新宽度，<c>1..8192</c>。</param>
    /// <param name="height">新高度，<c>1..8192</c>。</param>
    /// <returns>是否有变化。</returns>
    public bool ResizeFrame(ImgFrame frame, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureOwner(frame);
        return FrameGeometry.Resize(this, frame, width, height);
    }

    /// <summary>把所属 NPK 条目标记为已修改；不标记的话 <c>NpkArchive.Save</c> 会原样直拷、丢弃本次编辑。</summary>
    internal void MarkModified()
    {
        if (OwnerEntry is not null)
        {
            OwnerEntry.IsModified = true;
        }
    }

    /// <summary>
    ///     只有"真正没有 handler"的条目（<c>OtherHandler</c>：.ogg 等非 IMG、v7+ 等未知版本）不可改写帧表，
    ///     否则会写出结构损坏的文件。v5 已有 <c>Ver5Handler</c>，不再走这条拒绝分支。
    /// </summary>
    private void EnsureEditable()
    {
        if (Handler is Handlers.OtherHandler)
        {
            throw new NotSupportedException($"版本 {Version} 暂不支持改写帧表（当前只能原样透传）。");
        }
    }

    /// <summary>校验帧属于本文件，避免拿别的 IMG 的帧来改。</summary>
    private void EnsureOwner(ImgFrame frame)
    {
        if (!_frames.Contains(frame))
        {
            throw new ArgumentException($"帧 #{frame.Index} 不属于本文件（{Path}）。", nameof(frame));
        }
    }

    /// <summary>
    ///     读取一个 IMG。
    /// </summary>
    /// <param name="stream">源流，<b>当前游标即 IMG 起点</b>。</param>
    /// <param name="path">相对路径（仅用于展示与名称）。</param>
    /// <param name="length">
    ///     本 IMG 在流中占用的字节数；传负数表示"读到流末尾"。
    ///     该值用于识别非 IMG 条目（.ogg 等）以及未注册版本（v5 等）的整体取样。
    /// </param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 <c>null</c>。</exception>
    /// <exception cref="ImgFormatException">头部字段非法（帧数为负、尺寸越界等）。</exception>
    public static ImgFile Read(Stream stream, string path, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var start = stream.CanSeek ? stream.Position : 0L;
        var remaining = length >= 0 ? length : stream.Length - start;
        var file = new ImgFile(path);

        // 两种魔数长度不同（16 / 18），统一先取 18 字节再比对，避免"读一次 Read 猜长度"。
        Span<byte> head = stackalloc byte[18];
        var read = 0;
        while (read < head.Length)
        {
            var count = stream.Read(head[read..]);
            if (count <= 0)
            {
                break;
            }

            read += count;
        }

        if (Matches(head[..read], NpkCipher.ImageFlagBytes))
        {
            // v1：魔数 18 字节已消费，剩余版本头由 handler 读（4 + 2 + 4 + 4）。
            file.Version = ImgVersion.Ver1;
            file.Handler = ImgHandlerRegistry.Get(ImgVersion.Ver1);
            file.Handler.ReadIndex(stream, file);
            return file;
        }

        if (Matches(head[..read], NpkCipher.ImgFlagBytes))
        {
            // v2 系：魔数 16 字节，需要回退 2 字节对齐到 IndexLength。
            if (stream.CanSeek)
            {
                stream.Position = start + NpkCipher.ImgFlagBytes.Length;
            }
            else
            {
                throw new ImgFormatException("流不可定位，无法读取 IMG 头部。");
            }

            file.IndexLength = stream.ReadInt64();
            var version = (ImgVersion)stream.ReadInt32();
            file.Version = version;
            file.DeclaredFrameCount = stream.ReadInt32();
            if (file.DeclaredFrameCount < 0)
            {
                throw new ImgFormatException($"IMG 头部帧数非法：{file.DeclaredFrameCount}。");
            }

            file.Handler = ImgHandlerRegistry.Get(version);
            if (file.Handler is OtherHandler)
            {
                // 版本尚未实现（v5 等）：整段取样，避免只取 IndexLength 而丢掉数据区。
                file.IndexLength = Math.Max(0, remaining - CommonHeaderSize);
            }

            file.Handler.ReadIndex(stream, file);
            return file;
        }

        // 非 IMG（.ogg 等）：整段原始字节交给 OtherHandler。
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        file.Version = ImgVersion.Other;
        file.IndexLength = remaining;
        file.Handler = ImgHandlerRegistry.Get(ImgVersion.Other);
        file.Handler.ReadIndex(stream, file);
        return file;
    }

    /// <summary>
    ///     写出 IMG。
    /// </summary>
    /// <param name="stream">目标流（从当前位置开始写）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 <c>null</c>。</exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Handler.WriteIndex(writer, this);
        Handler.WritePayload(writer, this);
        writer.Flush();
    }

    /// <summary>
    ///     序列化为字节数组。
    /// </summary>
    /// <returns>完整 IMG 字节。</returns>
    public byte[] ToArray()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    ///     解码一帧（对外入口，内部转到当前版本的 handler）。
    /// </summary>
    /// <param name="frame">要解码的帧。</param>
    /// <returns>解码结果；LINK 帧返回 <c>null</c>。</returns>
    public RgbaImage? Decode(ImgFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Handler.Decode(this, frame);
    }

    /// <summary>
    ///     取一帧的未压缩原始像素字节（解压 + 去压缩头，<b>不做</b>像素格式转换）。
    /// </summary>
    /// <param name="frame">目标帧。</param>
    /// <returns>
    ///     未压缩像素字节；LINK 帧返回 <c>null</c>（D-04）；
    ///     v5 的纹理帧也返回 <c>null</c>——它的像素在纹理页里，帧本身没有数据区字节。
    /// </returns>
    public byte[]? GetUncompressedPixels(ImgFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Handler.ReadUncompressedPixels(this, frame);
    }

    /// <summary>
    ///     用一张位图替换某帧的内容（"换图"闭环的核心入口）。
    ///     <para>
    ///         依次完成：确定目标像素格式 → 更新几何 → 把 BGRA 像素编码成目标格式的
    ///         <b>未压缩</b>字节 → zlib 压缩 → 写回 <see cref="ImgFrame.RawData" /> 与
    ///         <see cref="ImgFrame.Length" /> → 标记所属 NPK 条目为已修改。
    ///     </para>
    ///     <para>
    ///         <b>v4/v6</b>：像素是 1 字节色号，<paramref name="type" /> 被忽略（保持帧原有的 0x0E 字段），
    ///         由 <see cref="FramePixelEncoder" /> 做"精确命中 / 就近色"量化。
    ///     </para>
    ///     <para>
    ///         <b>v5</b>：像素写进一张新纹理页（ARGB1555 重编码，<b>不做 DXT 编码</b>），
    ///         帧的像素格式统一落成 0x0E；原有纹理页保持不变（可能与其它帧共享）。
    ///     </para>
    ///     <para>
    ///         <b>为什么必须标记条目</b>：<c>NpkArchive.Save</c> 对未标记条目走"原样直拷"，
    ///         不标记的话这里的修改在保存时会被整段丢弃。
    ///     </para>
    /// </summary>
    /// <param name="frame">要替换的帧；LINK 帧会被就地改造成普通帧。</param>
    /// <param name="image">新的像素内容（内部 BGRA8888）。</param>
    /// <param name="type">
    ///     目标像素格式；传 <see cref="ColorBits.UNKNOWN" /> 表示沿用该帧原有格式。
    ///     LINK 帧或被要求写成 LINK 时强制使用 <see cref="ColorBits.ARGB_1555" />；
    ///     v5 一律落成 <see cref="ColorBits.ARGB_1555" />（本轮不做 DXT 编码）。
    /// </param>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    /// <exception cref="NotSupportedException">
    ///     目标格式不是 1555/4444/8888（调色板索引格式由 v4/v6 handler 接管，不走本检查）。
    /// </exception>
    public void ReplaceFrameImage(ImgFrame frame, RgbaImage image, ColorBits type)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(image);
        if (!_frames.Contains(frame))
        {
            throw new ArgumentException($"帧 #{frame.Index} 不属于本文件（{Path}）。", nameof(frame));
        }

        // 1. 确定目标像素格式：UNKNOWN 沿用原格式；LINK（无论是帧本身还是被要求的）一律强制 1555。
        var paletteIndexed = FramePixelEncoder.IsPaletteIndexed(Version);
        var targetType = type == ColorBits.UNKNOWN ? frame.Type : type;
        if (frame.IsLink || targetType == ColorBits.LINK)
        {
            targetType = ColorBits.ARGB_1555;
        }

        if (paletteIndexed)
        {
            // v4/v6 的索引区字段仍是 0x0E，但真实数据是色号；强制沿用原字段，避免写出 2 字节/像素。
            // LINK 帧例外：它没有自己的 type，必须落成 0x0E 才能变成普通帧。
            targetType = frame.IsLink ? ColorBits.ARGB_1555 : frame.Type;
        }
        else if (Version == ImgVersion.Ver5)
        {
            // v5 的写回是"ARGB1555 重编码"（本轮不做 DXT 编码），因此不论原帧是 DXT_1/3/5 还是 1555，
            // 一律落成 0x0E；否则下面的格式白名单会把 DXT 类型当成"需要量化"而拒绝。
            targetType = ColorBits.ARGB_1555;
        }
        else if (targetType is not (ColorBits.ARGB_1555 or ColorBits.ARGB_4444 or ColorBits.ARGB_8888))
        {
            throw new NotSupportedException(
                $"ReplaceFrameImage 不支持像素格式 {targetType}（0x{(int)targetType:X2}）：调色板索引格式需先量化（见 PaletteIndexCodec）。");
        }

        // 2. LINK 帧被改造为普通帧：清掉链接信息，否则写盘时会被当成 LINK 只写 8 字节索引。
        frame.LinkTarget = null;
        frame.LinkIndex = -1;
        frame.Type = targetType;

        // 3. 几何：宽高跟随新图；帧域只增不减（原版 ReplaceImage 的语义，避免图片变大后被裁掉）。
        frame.Width = image.Width;
        frame.Height = image.Height;
        frame.FrameWidth = Math.Max(frame.FrameWidth, image.Width);
        frame.FrameHeight = Math.Max(frame.FrameHeight, image.Height);

        // 4. BGRA → 目标格式的未压缩字节 → zlib 压缩 → 写回（v5 则落成一张新纹理页）。
        FramePixelEncoder.WritePixels(this, frame, image);

        // 5. 打通"改帧 → 存包"链路：不标记的话 Save 会原样直拷，修改被丢弃。
        MarkModified();
    }

    /// <summary>
    ///     裁掉一帧四周的全透明边，并把裁剪偏移<b>累加</b>到帧的 X/Y 上（REQ-11）。
    ///     <para>
    ///         这样贴图在角色坐标系里的绝对位置保持不变：<c>X += bounds.X</c>、<c>Y += bounds.Y</c>。
    ///         全透明帧、已经是紧包围盒的帧返回 <c>false</c> 且不做任何改动。
    ///     </para>
    /// </summary>
    /// <param name="frame">目标帧，不能是 LINK 帧。</param>
    /// <returns>是否真的裁了。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">帧不属于本文件。</exception>
    /// <exception cref="InvalidOperationException">LINK 帧没有自己的像素。</exception>
    public bool TrimFrame(ImgFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureOwner(frame);
        return FrameTrimmer.Trim(this, frame);
    }

    /// <summary>
    ///     本文件在 NPK 中的所属条目；由 <c>NpkArchive.LoadImg</c> 回填，用于把编辑结果标记为"已修改"。
    /// </summary>
    internal NpkEntry? OwnerEntry { get; set; }

    /// <summary>
    ///     按给定顺序重建帧表（仅供 <see cref="ImgEditHistory" /> 恢复快照使用）。
    ///     <para>不校验帧归属：快照里的帧本来就来自本文件；非法用法只会在写盘时暴露。</para>
    /// </summary>
    /// <param name="frames">按目标顺序排列的帧（引用复用）。</param>
    internal void RestoreFrameTable(IReadOnlyList<ImgFrame> frames) => _frames.Restore(frames);

    /// <summary>
    ///     追加一个已构造好的帧（handler 解析时使用）。
    /// </summary>
    /// <param name="frame">帧对象，其 <see cref="ImgFrame.Index" /> 由调用方设定。</param>
    internal void AppendFrame(ImgFrame frame) => _frames.AppendExisting(frame);

    /// <summary>
    ///     设置主调色板（v4/v5 handler 使用）。
    /// </summary>
    /// <param name="palette">颜色序列。</param>
    internal void SetPalette(IEnumerable<Color32> palette)
    {
        _palette.Clear();
        _palette.AddRange(palette);
    }

    /// <summary>
    ///     追加一张色表（v6 handler 使用）。
    /// </summary>
    /// <param name="palette">颜色序列。</param>
    internal void AddPalette(IEnumerable<Color32> palette)
    {
        _palettes.Add(palette.ToArray());
    }

    /// <summary>
    ///     追加一张纹理页（v5 handler 读取时、以及"换图"新建纹理时使用）。
    ///     <para><b>只能追加</b>：已有帧的 <see cref="TextureRef.TextureIndex" /> 是列表下标，中间插入会让它们全部错位。</para>
    /// </summary>
    /// <param name="texture">纹理页。</param>
    internal void AddTexture(ImgTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        _textures.Add(texture);
    }

    /// <summary>
    ///     返回可读文本，便于日志定位。
    /// </summary>
    public override string ToString()
    {
        return $"ImgFile({Path}, {Version}, {_frames.Count} frame(s), IndexLength={IndexLength})";
    }

    private static bool Matches(ReadOnlySpan<byte> head, ReadOnlySpan<byte> flag)
    {
        return head.Length >= flag.Length && head[..flag.Length].SequenceEqual(flag);
    }
}
