using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Catcretary.Widget;

/// <summary>
/// 위젯 창을 바탕화면 층에 붙인다.
///  - WS_EX_TOOLWINDOW: Alt+Tab·작업 보기에 나오지 않는다.
///  - 소유자 = Progman(바탕화면 창): 바탕화면 보기(Win+D) 때 다른 창과 함께 숨지 않는다.
///  - WM_WINDOWPOSCHANGING 에서 Z 순서를 맨 아래로 바꾼다: 클릭해도 다른 창 위로 올라오지 않는다.
///    항상 위 모드에서는 이 처리를 건너뛰고 Window.Topmost 에 맡긴다.
/// 탐색기가 재시작되면 소유자가 사라지면서 이 창도 파괴된다. 다시 만드는 것은 App 이 맡는다.
/// </summary>
internal static class DesktopPin
{
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>SourceInitialized 이후(HWND 가 생긴 뒤) 호출한다. keepBottom 이 false 인 동안은 Z 순서를 건드리지 않는다(항상 위 모드).</summary>
    public static void Attach(Window window, Func<bool> keepBottom)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, progman);

        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            keepBottom() ? Hook(msg, l) : IntPtr.Zero);
        if (keepBottom()) SendToBottom(window);
    }

    /// <summary>항상 위 모드를 끈 직후 바로 맨 아래로 내린다.</summary>
    public static void SendToBottom(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static IntPtr Hook(int msg, IntPtr lParam)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
}
