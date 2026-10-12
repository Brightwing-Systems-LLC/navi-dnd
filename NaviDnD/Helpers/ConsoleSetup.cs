using NaviDnD.Data;
using NaviDnD.Data.Models;
using System.Runtime.InteropServices;
using System.Text;

namespace NaviDnD;

internal static class ConsoleSetup
{
    private delegate IntPtr LowLevelKeyboardProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
    private static LowLevelKeyboardProcDelegate? _keyboardHookProc;
    private static IntPtr _keyboardHook;

    private const int WH_KEYBOARD_LL = 13;
    private const int VK_F11 = 0x7A;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT = 0x0040;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_CLOSE = 0x0010;
    private const IntPtr ICON_SMALL = (IntPtr)0;
    private const IntPtr ICON_BIG = (IntPtr)1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    const int SW_RESTORE = 9;

    // Windows 11 only (build 22000+) — on older systems DwmSetWindowAttribute returns
    // an unknown-attribute error, which is harmless (see the try/catch in SetRoundedCorners).
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // DWMWA_CLOAK — undocumented but long-known: removes the window from DWM composition
    // DIRECTLY, regardless of its "state" (minimised/normal). This decouples
    // "put the window into its normal state so resizing works correctly" (SW_RESTORE) from
    // "show the window to the user" — the window can be restored while staying physically invisible,
    // SetConsoleConfig applied, and only then the cloak removed.
    private const int DWMWA_CLOAK = 13;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFOEX
    {
        public uint cbSize;
        public uint nFont;
        public short FontSizeX;
        public short FontSizeY;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out WinRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    // BlockConsoleFullscreen above strips WS_SYSMENU/WS_CAPTION from the console window — it has neither
    // a close button nor a system menu. A plain Environment.Exit(0) ends the process, but the console
    // window itself sometimes lingers — an explicit WM_CLOSE before exiting reliably closes it.
    internal static void CloseConsoleAndExit()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
            SendMessage(consoleWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Environment.Exit(0);
    }

    // Windows creates and immediately shows the console window BEFORE a single line of Main()
    // runs — with the default size/black background. We hide the window on the very first line
    // and show it only once everything (borderless style, map-sized dimensions, colours, font)
    // is configured — otherwise the user sees a "plain console → game" flicker.
    internal static void HideWindow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
            ShowWindow(consoleWindow, SW_HIDE);
    }

    internal static void ShowWindowReady()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
            ShowWindow(consoleWindow, SW_SHOW);
    }

    internal static void SetConsoleConfig(WorldState settings)
    {
        Console.Title = "NaviDnD";
        Console.OutputEncoding = Encoding.UTF8;

        var display = new DisplayConfig();
        // frame(2) + guard against auto-wrap on the right(1) + DisplayConfig.LeftMargin on the left.
        int width = display.InnerWidth(display.ViewCols(settings.Map)) + 3 + DisplayConfig.LeftMargin;
        // The character/abilities card stretches to at least FixedMapContentHigh
        // (see DisplayConfig.MaxHigh) — the window must be at least as tall, otherwise
        // there is no room for the dialogue box and the picture block below the card.
        int cardHigh = Math.Max(display.ViewRows(settings.Map) * 2, DisplayConfig.FixedMapContentHigh);
        int height = cardHigh + display.MaxHistoryLines + 18;

        // EnableAnsiSupport strips WS_CAPTION BEFORE this point, but conhost computes the pixels for
        // N rows as if the title bar were still there — an empty strip remains at the bottom of the window. A buffer
        // taller than the window caused scrolling (not allowed), and pixel trimming via SetWindowPos affected
        // the width. -2 rows clipped real content (the top row vanished) — rolled back to -1.
        int windowHeight = Math.Max(1, height - 1);

        // A fixed window size ceiling — it does NOT depend on screen resolution. Previously this role
        // was played only by Console.LargestWindowWidth/Height (the most that fits on the current
        // monitor) — so on a higher-resolution monitor the window became taller/wider
        // than on the old one, even though the map was the same. The numbers match Window Size in the shortcut properties.
        const int MaxWidth = 150;
        const int MaxHeight = 63;
        if (width > MaxWidth) width = MaxWidth;
        if (windowHeight > MaxHeight) windowHeight = MaxHeight;

        // Font: 16 in windowed mode; in full screen by scale (auto — the largest at which the window fits the monitor).
        // The grid stays the same (rows/columns), the cell grows. If it does not fit (the font's character width is not exactly half
        // its height) — one step smaller.
        short font = FontSizeFor(width, windowHeight);
        SetConsoleFontSize(font);
        while ((Console.LargestWindowWidth < width || Console.LargestWindowHeight < windowHeight)
               && FontSizes(FontFace).Where(x => x < font).DefaultIfEmpty(0).Max() is var smaller and > 0)
            SetConsoleFontSize(font = (short)smaller);   // did not fit — the next size of this font

        // Console.LargestWindowWidth/Height is an emergency barrier ONLY for the case where even
        // the fixed ceiling above does not fit on screen (a very small monitor) — under normal
        // conditions it should never kick in.
        if (width > Console.LargestWindowWidth)
            width = Console.LargestWindowWidth;
        if (windowHeight > Console.LargestWindowHeight)
            windowHeight = Console.LargestWindowHeight;

        // Do NOT reduce the width: the "+3" in width (margin beyond border.InnerWidth) is not wasted space
        // but a guard against auto-wrap on the console's last column. -1 on the width ate this
        // margin and caused lines to wrap at the very edge (the Windows console wraps text written
        // exactly into the buffer's last column).
        Console.WindowWidth = width;
        Console.WindowHeight = windowHeight;
        Console.BufferWidth = Console.WindowWidth;
        Console.BufferHeight = Console.WindowHeight;
        TrimWindowToRows();
        NaviDnD.Helpers.FullscreenBackdrop.Recenter();   // full screen — after a resize the window is centred again
    }

    // The console window can be slightly taller than a whole number of rows (the title bar is gone but conhost counts pixels with it) — below
    // the bottom frame a strip of a few pixels remained, painted by the console with the current background (water from
    // the world map, or some other colour). Trim the window height to exactly the rows; leave the width alone (adjusting it broke
    // wrapping on the last column).
    private static void TrimWindowToRows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var client)) return;
            var font = new CONSOLE_FONT_INFOEX();
            font.cbSize = (uint)Marshal.SizeOf(font);
            if (!GetCurrentConsoleFontEx(GetStdHandle(STD_OUTPUT_HANDLE), false, ref font) || font.FontSizeY <= 0) return;
            int extra = (client.Bottom - client.Top) - Console.WindowHeight * font.FontSizeY;
            if (extra <= 0 || extra >= font.FontSizeY || !GetWindowRect(hwnd, out var win)) return;
            const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, win.Right - win.Left, win.Bottom - win.Top - extra, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { /* did not work — the strip stays, as before */ }
    }

    // Full screen (the SCREEN setting) and its font size: 0 — auto, otherwise the size (if it does not fit — whatever fits).
    internal static bool Fullscreen;
    // Console font (the "FONT FACE" setting) and the choices available.
    internal static readonly string[] FontFaces = ["Consolas", "DejaVu Sans Mono"];

    // The game's fonts (Fonts folder: DejaVu Sans Mono) — loaded while the game runs, without installing them system-wide
    // (AddFontResourceEx without FR_PRIVATE: the console draws in another process and must see them), removed on exit.
    internal static void LoadBundledFonts()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            string dir = Path.Combine(AppConfig.ProjectRoot, "Fonts");
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "*.ttf");
            foreach (var f in files) AddFontResourceEx(f, 0, IntPtr.Zero);
            // Announce the fonts without waiting for other applications: the HWND_BROADCAST timeout
            // applies to each window separately and can delay the menu by seconds.
            PostMessage((IntPtr)0xffff, 0x001D /* WM_FONTCHANGE */, IntPtr.Zero, IntPtr.Zero);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var f in files) RemoveFontResourceEx(f, 0, IntPtr.Zero); };
        }
        catch { /* not loaded — they stay in the list, the console substitutes its own font */ }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveFontResourceEx(string file, uint flags, IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    internal static string FontFace = "Consolas";
    internal static int FullscreenFontSize;
    private const short BaseFontSize = 16;

    // Font sizes at which it looks even (1:2 cell): Consolas — 14 and 16, DejaVu Sans Mono — 16–22.
    internal static int[] FontSizes(string face) => face switch
    {
        "Consolas" => [14, 16],
        "DejaVu Sans Mono" => [14, 16, 20, 22],
        _ => [16],
    };

    // 16 in windowed mode; in full screen — the chosen font size or (auto) the largest of its sizes at which the game
    // fits the monitor; if even the smallest does not fit — the smallest.
    private static short FontSizeFor(int cols, int rows)
    {
        if (!Fullscreen) return BaseFontSize;
        var (mw, mh) = NaviDnD.Helpers.FullscreenBackdrop.MonitorSize();
        int fit = Math.Min(mh / Math.Max(1, rows), mw * 2 / Math.Max(1, cols));   // the cell is exactly 1:2
        var sizes = FontSizes(FontFace);
        var fitting = sizes.Where(s => s <= fit && (FullscreenFontSize <= 0 || s <= FullscreenFontSize)).ToList();
        return (short)(fitting.Count > 0 ? fitting.Max() : sizes.Min());
    }

    private static void SetConsoleFontSize(short size)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        var info = new CONSOLE_FONT_INFOEX();
        info.cbSize = (uint)Marshal.SizeOf(info);
        GetCurrentConsoleFontEx(handle, false, ref info);
        // Cell width is exactly half the height: Consolas on its own is 1:2 only up to 16 (24 → 11×24), and the map's half-blocks,
        // braille and frames got squashed at large sizes; an explicit width is honoured by the console (24 → 12×24).
        info.FontSizeX = (short)(size / 2);
        info.FontSizeY = size;
        info.FaceName = FontFace;
        SetCurrentConsoleFontEx(handle, false, ref info);
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // F11 (the console's full-screen mode breaks the layout) is suppressed only in the game window — not in other programs.
        if (nCode >= 0 && Marshal.ReadInt32(lParam) == VK_F11 && GetForegroundWindow() == GetConsoleWindow())
        {
            if (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104) _ = Task.Run(Helpers.Speech.Stop);
            return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    internal static void BlockConsoleFullscreen()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        _keyboardHookProc = KeyboardHookCallback;
        var thread = new Thread(() =>
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandle(null), 0);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_keyboardHook != IntPtr.Zero)
                UnhookWindowsHookEx(_keyboardHook);
        });
        thread.IsBackground = true;
        thread.Start();
    }

    internal static void EnableAnsiSupport()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(handle, out uint mode))
        {
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(handle, mode);
        }

        var consoleWindow = GetConsoleWindow();
        int style = GetWindowLong(consoleWindow, GWL_STYLE);
        style &= ~(WS_THICKFRAME | WS_MAXIMIZEBOX | WS_CAPTION | WS_SYSMENU);
        SetWindowLong(consoleWindow, GWL_STYLE, style);
        // The style will not visibly apply without SWP_FRAMECHANGED — the window does not redraw
        // its non-client area (title bar/frame) by itself after SetWindowLong.
        SetWindowPos(consoleWindow, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        SetConsoleIcon();
        SetRoundedCorners(consoleWindow);
    }

    // DWMWA_WINDOW_CORNER_PREFERENCE is only available from Windows 11 (build 22000). On older
    // systems DwmSetWindowAttribute returns an unknown-attribute error — harmless,
    // the corners just are not rounded.
    private static void SetRoundedCorners(IntPtr consoleWindow)
    {
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(consoleWindow, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    internal static void DisableQuickEdit()
    {
        IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(handle, out uint mode);
        mode &= ~ENABLE_QUICK_EDIT;
        SetConsoleMode(handle, mode);
    }

    internal static void SetConsoleIcon()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        IntPtr hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;

        IntPtr hIcon = LoadImage(IntPtr.Zero, "dnd.ico", IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        if (hIcon != IntPtr.Zero)
        {
            SendMessage(hWnd, WM_SETICON, ICON_SMALL, hIcon);
            SendMessage(hWnd, WM_SETICON, ICON_BIG, hIcon);
        }
        else
        {
            Console.WriteLine(L.T("Could not load the icon. Check the path and format (.ico required)."));
        }
    }


    internal static void ShowWindow()
    {
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;

        //// Cloak BEFORE restoring the state — the "minimised → normal" transition itself stays physically
        //// invisible, even though technically the window is already in its normal state and visible to Windows.
        //int cloak = 1;
        //DwmSetWindowAttribute(hWnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        ShowWindow(hWnd, SW_RESTORE);
    }

    // Removes the cloak set in ShowWindow() — call AFTER SetConsoleConfig(), when
    // the resize has already been applied to the window in its normal state and everything is ready to show the user.
    internal static void RevealWindow()
    {
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;

        int uncloak = 0;
        DwmSetWindowAttribute(hWnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
    }
}
