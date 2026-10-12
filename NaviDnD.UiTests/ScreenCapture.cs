using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NaviDnD.UiTests;

// Captures the client area of the console window via PrintWindow and samples a pixel at
// character coordinates (col, row) — this checks the REAL rendered color,
// bypassing the legacy Windows Console API (ReadConsoleOutputAttribute), which doesn't reflect
// 24-bit ANSI. Call ONLY while the game console is attached (inside GameConsole.WithGameConsole) —
// GetConsoleWindow()/GetStdHandle() return the handles of the currently attached console.
[SupportedOSPlatform("windows")]
internal static class ScreenCapture
{
    private const uint PW_CLIENTONLY = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern bool GetCurrentConsoleFont(IntPtr hConsoleOutput, bool bMaximumWindow, out CONSOLE_FONT_INFO info);

    [DllImport("kernel32.dll")]
    private static extern COORD GetConsoleFontSize(IntPtr hConsoleOutput, int nFont);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_FONT_INFO { public int nFont; public COORD dwFontSize; }

    // Returns the "ink" color of the character cell (col, row), 0-based — the same coordinates
    // ConsoleBufferReader/GameConsole use. Sampling a single pixel in the center
    // of the cell is unreliable: thin letter strokes (especially Cyrillic ones) often don't land exactly
    // in the center, and we get the background instead of the text. Instead we scan the WHOLE cell and take
    // the pixel most unlike the background — that is the real text color,
    // robust to imprecise hits and slight coordinate/DPI mismatch.
    // Throws an exception describing exactly which step failed (rather than silently returning null) —
    // the only way to find the cause in an environment you can't see with your own eyes.
    public static (byte r, byte g, byte b) SampleCellColor(int col, int row, (byte r, byte g, byte b) background,
        Action<Bitmap, Rectangle>? inspect = null)
    {
        IntPtr hwnd = GetConsoleWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("GetConsoleWindow() returned NULL — the console has no window handle (possibly a headless/virtual session without GDI windows).");

        if (!GetClientRect(hwnd, out var rect))
            throw new InvalidOperationException($"GetClientRect failed (Win32Error={Marshal.GetLastWin32Error()}).");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"GetClientRect returned zero size ({width}x{height}).");

        using var stdOut = ConsoleHandles.OpenOutput();
        if (!GetCurrentConsoleFont(stdOut.DangerousGetHandle(), false, out var fontInfo))
            throw new InvalidOperationException($"GetCurrentConsoleFont failed (Win32Error={Marshal.GetLastWin32Error()}).");

        var cellSize = GetConsoleFontSize(stdOut.DangerousGetHandle(), fontInfo.nFont);
        if (cellSize.X <= 0 || cellSize.Y <= 0)
            throw new InvalidOperationException($"GetConsoleFontSize returned a zero cell size ({cellSize.X}x{cellSize.Y}).");

        int cellLeft = col * cellSize.X;
        int cellTop = row * cellSize.Y;
        if (cellLeft < 0 || cellTop < 0 || cellLeft >= width || cellTop >= height)
            throw new InvalidOperationException($"Cell ({cellLeft},{cellTop}) is outside the client area ({width}x{height}).");
        int cellRight = Math.Min(width, cellLeft + cellSize.X);
        int cellBottom = Math.Min(height, cellTop + cellSize.Y);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            bool ok;
            try { ok = PrintWindow(hwnd, hdc, PW_CLIENTONLY); }
            finally { graphics.ReleaseHdc(hdc); }
            if (!ok)
                throw new InvalidOperationException($"PrintWindow returned false (Win32Error={Marshal.GetLastWin32Error()}).");
        }

        inspect?.Invoke(bitmap, new Rectangle(cellLeft, cellTop, cellRight - cellLeft, cellBottom - cellTop));
        Color best = default;
        int bestDist = -1;
        for (int y = cellTop; y < cellBottom; y++)
        {
            for (int x = cellLeft; x < cellRight; x++)
            {
                var p = bitmap.GetPixel(x, y);
                int dist = Math.Abs(p.R - background.r) + Math.Abs(p.G - background.g) + Math.Abs(p.B - background.b);
                if (dist > bestDist) { bestDist = dist; best = p; }
            }
        }
        return (best.R, best.G, best.B);
    }
}
