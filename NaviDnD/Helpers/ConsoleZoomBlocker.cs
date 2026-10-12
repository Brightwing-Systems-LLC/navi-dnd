using System.Runtime.InteropServices;

namespace NaviDnD.Helpers;

// The Windows console (conhost) handles Ctrl+mouse wheel itself — it changes the font size, and with it the
// game window, before the event ever reaches the application. A pinch on a laptop touchpad arrives
// as exactly Ctrl+wheel. conhost owns the window and this cannot be disabled from inside the console — hence
// a low-level mouse hook (WH_MOUSE_LL) on its own thread: Ctrl+wheel over the game window is swallowed
// (the size does not change) and passed to the game as ordinary scrolling (ConsoleMouseReader.AddWheel) —
// e.g. the world map zoom.
internal static class ConsoleZoomBlocker
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_CONTROL = 0x11;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc fn, nint hMod, uint threadId);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);

    private const uint GA_ROOT = 2;

    private static LowLevelMouseProc? _proc; // keep a reference — otherwise the GC collects the delegate behind the hook
    private static nint _hook;
    private static nint _consoleWindow;

    public static void Start()
    {
        if (_proc != null) return;
        _consoleWindow = GetConsoleWindow();
        if (_consoleWindow == 0) return;
        _proc = Hook;

        // A low-level hook needs a message loop on the same thread.
        var thread = new Thread(() =>
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == 0) return;
            while (GetMessage(out _, 0, 0, 0) > 0) { }
        }) { IsBackground = true, Name = "ConsoleZoomBlocker" };
        thread.Start();
    }

    private static nint Hook(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam == WM_MOUSEWHEEL && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (GetAncestor(WindowFromPoint(info.pt), GA_ROOT) == _consoleWindow)
            {
                ConsoleMouseReader.AddWheelDelta((short)(info.mouseData >> 16));
                return 1; // swallow: conhost does not change the font or window size
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
