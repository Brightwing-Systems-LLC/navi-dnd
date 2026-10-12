using System.Runtime.InteropServices;

namespace NaviDnD.Helpers;

// Text from the Windows clipboard (user32, without WinForms) — for Ctrl+V / Shift+Insert pasting into the
// character sheet and settings fields: the console does not paste by itself (we read input directly from the console buffer).
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    // Clipboard text as a single line (line breaks → spaces), or "" — empty / not text / clipboard busy.
    public static string Get()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return "";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(20); continue; } // clipboard held by another window
            try
            {
                IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return "";
                try
                {
                    string text = Marshal.PtrToStringUni(ptr) ?? "";
                    return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        return "";
    }

    // Is the key press a paste: Ctrl+V or Shift+Insert.
    public static bool IsPasteKey(ConsoleKeyInfo key) =>
        (key.Key == ConsoleKey.V && (key.Modifiers & ConsoleModifiers.Control) != 0)
        || (key.Key == ConsoleKey.Insert && (key.Modifiers & ConsoleModifiers.Shift) != 0);
}
