using System.Runtime.InteropServices;

namespace NaviDnD.Helpers;

internal static class AiSetupNotice
{
    internal static void Show()
    {
        string message = L.T("Claude Code and Codex CLI were not found.\n\nThe game needs one of these clients:\n")
            + "• Claude Code: code.claude.com/docs/en/quickstart\n"
            + "• Codex CLI: developers.openai.com/codex/cli\n\n"
            + L.T("Install a client and sign in. If it is already installed, set the path to its exe in the game settings.\n\nPress OK to open the settings.");
        if (OperatingSystem.IsWindows())
            // The notification must also be visible above the fullscreen backdrop.
            MessageBox(IntPtr.Zero, message, L.T("NaviDnD — AI setup"), 0x40 | 0x10000 | 0x40000);
        else
            Console.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
