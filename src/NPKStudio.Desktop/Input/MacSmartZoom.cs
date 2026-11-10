using System.Runtime.InteropServices;
using Avalonia.Threading;
using NPKStudio.Core.Diagnostics;

namespace NPKStudio.Desktop.Input;

/// <summary>
///     macOS「智能缩放」：触控板双指轻点两下（Magic Mouse 是单指轻点两下）。
///     <para>
///         Avalonia 11.3.22 的原生视图类 <c>AvnView</c> 没有实现 <c>smartMagnifyWithEvent:</c>
///         （反编译查过 libAvaloniaNative.dylib：手势只接了 magnifyWithEvent: / rotateWithEvent: / swipeWithEvent:），
///         系统发来的这个手势沿响应链传下去就没了。这里用 Objective-C runtime 在运行时给 <c>AvnView</c>
///         补上这个方法，收到后在 UI 线程上调用登记的处理函数。
///     </para>
///     <para>只在 macOS 上生效；找不到 <c>AvnView</c>（例如无头测试、Windows）时什么也不做。</para>
/// </summary>
internal static class MacSmartZoom {
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SmartMagnifyImp(IntPtr self, IntPtr selector, IntPtr nsEvent);

    // 必须一直持有：委托被回收后，原生侧再调用就是野指针
    private static readonly SmartMagnifyImp Imp = OnSmartMagnify;

    private static Action? s_handler;
    private static bool s_attempted;

    /// <summary>是否已经成功挂上（诊断用）。</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>
    ///     登记处理函数，并在第一次调用时给 <c>AvnView</c> 补方法。要在 Avalonia 平台初始化之后调用
    ///     （例如控件挂到可视树时），否则 <c>AvnView</c> 类还没加载。
    /// </summary>
    public static void EnsureInstalled(Action handler) {
        s_handler = handler;
        if (s_attempted || !OperatingSystem.IsMacOS()) return;

        s_attempted = true;
        try {
            var viewClass = objc_getClass("AvnView");
            if (viewClass == IntPtr.Zero) return;

            var selector = sel_registerName("smartMagnifyWithEvent:");
            // "v@:@"：返回 void，参数 self、_cmd、NSEvent*。类里已有同名方法时 class_addMethod 返回 false、不覆盖
            IsInstalled = class_addMethod(viewClass, selector, Marshal.GetFunctionPointerForDelegate(Imp), "v@:@");
            if (!IsInstalled) {
                TraceLog.Instance.Warn("AvnView 已有 smartMagnifyWithEvent:，智能缩放未挂接");
            }
        } catch (Exception ex) {
            TraceLog.Instance.Warn("挂接 macOS 智能缩放失败", ex);
        }
    }

    private static void OnSmartMagnify(IntPtr self, IntPtr selector, IntPtr nsEvent) {
        // 从 AppKit 直接调进来：异常绝不能穿回原生代码，处理放到 UI 线程队列里做
        try {
            var handler = s_handler;
            if (handler is not null) {
                Dispatcher.UIThread.Post(handler);
            }
        } catch (Exception ex) {
            TraceLog.Instance.Warn("处理 macOS 智能缩放失败", ex);
        }
    }

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
}
