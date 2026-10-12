using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NaviDnD.Helpers;

// The game draws in the classic console (conhost): Windows Terminal does not allow controlling the window, font and mouse the way
// it needs. Any launch without RelaunchFlag relaunches in conhost.exe with that flag (only the game itself sets the flag).
public static class ConsoleHostLauncher
{
    public const string RelaunchFlag = "--navidnd-conhost";

    // The Windows console host (conhost) — the "default console" value in HKCU\Console\%%Startup.
    public const string ConhostDelegation = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";
    private const string StartupKey = @"Console\%%Startup";
    private static readonly string[] DelegationValues = ["DelegationConsole", "DelegationTerminal"];

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    // The player's previous console settings for the duration of the relaunch: the relaunched game restores them (its console has been
    // created by then); if it fails to, the next launch restores them.
    public static string RestorePath { get; set; } = Path.Combine(Path.GetTempPath(), "navidnd-console-restore.json");

    // A visible classic console window; a terminal's pseudoconsole has a hidden service window.
    public static bool InClassicConsole()
    {
        nint window = GetConsoleWindow();
        return window != 0 && IsWindowVisible(window);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    // One game per user: a second launch (another click on the shortcut while the game is loading) does not open a second copy —
    // it shows the window of the running one. Without this every click started a game with the voice model, and a dozen copies
    // filled up memory. The game (with the flag) holds the mutex until exit; the window handle is in a file next to it.
    private const string InstanceName = @"Local\NaviDnD.Game";
    private const string SwitchName = @"Local\NaviDnD.ConsoleSwitch";
    private static Mutex? _instance;
    public static string WindowPath { get; set; } = Path.Combine(Path.GetTempPath(), "navidnd-window.txt");

    // Must run before ConsoleSetup hides the window or initializes input/sound/storage. true means this process should exit.
    public static bool TryRelaunch(string[] args)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") != null
            || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        if (args.Contains(RelaunchFlag))
        {
            using (SwitchGate()) RestorePending();
            // Another game is already running (two launches racing) — show its window and close this one.
            if (!ClaimInstance()) { ActivateRunning(); return true; }
            nint own = GetConsoleWindow();
            try { File.WriteAllText(WindowPath, own.ToString()); } catch { /* not important for startup */ }
            if (own != 0) SetForegroundWindow(own);
            // The relaunch already happened, yet there is still no classic console window — the terminal intercepted it too.
            if (!InClassicConsole()) Log("After the relaunch the game is still not in the classic console.");
            return false;
        }
        // The game is already running — bring its window to the front, do not start a second copy.
        if (IsRunning()) { ActivateRunning(); return true; }
        // Without the flag, always relaunch (shortcut, exe, relaunch after an update): guessing the console owner from the window
        // is unreliable — on some Windows 11 setups the terminal intercepts even an explicit conhost.exe from a shortcut.
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return false;
        try
        {
            string? assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? Assembly.GetEntryAssembly()?.Location : null;
            using (SwitchGate())
            {
                RestorePending();   // the previous relaunch did not restore the settings (a failure) — restore them before swapping again
                // For the duration of the launch the "default console" is the classic one (otherwise Windows 11 with the terminal as default
                // may hand even conhost.exe to the terminal); the relaunched game restores the previous settings.
                var saved = ForceConhostDelegation();
                if (saved.Count > 0) File.WriteAllText(RestorePath, System.Text.Json.JsonSerializer.Serialize(saved));
                // The new game window gets focus (otherwise Windows may open it behind other windows).
                AllowSetForegroundWindow(-1);
                using var child = Process.Start(CreateStartInfo(executable, args, Environment.CurrentDirectory, assembly));
                if (child == null)
                {
                    Log("conhost.exe did not start.");
                    RestorePending();
                    return false;
                }
            }
            // The first window is hidden once the second has started: the game opens in it, no need to wait.
            if (GetConsoleWindow() is var own and not 0) ShowWindow(own, 0);
            return true;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log("Could not relaunch in the classic console: " + error.Message);
            return false;
        }
    }

    private static bool IsRunning() => Mutex.TryOpenExisting(InstanceName, out var existing) && Dispose(existing);

    private static bool Dispose(Mutex mutex) { mutex.Dispose(); return true; }

    // Claim the game's slot; if the previous copy is still closing (relaunch after an update), wait for it a little.
    private static bool ClaimInstance()
    {
        _instance = new Mutex(false, InstanceName);
        try { return _instance.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { return true; }   // the previous game crashed without releasing it
    }

    // The running game's window — restore it and bring it to the front.
    private static void ActivateRunning()
    {
        try
        {
            if (!nint.TryParse(File.ReadAllText(WindowPath).Trim(), out nint window) || window == 0) return;
            if (IsIconic(window)) ShowWindow(window, 9);   // SW_RESTORE
            SetForegroundWindow(window);
        }
        catch { /* no window — just do not duplicate the game */ }
    }

    // Swapping and restoring the console setting — one process at a time (parallel launches do not overwrite each other).
    private static IDisposable SwitchGate()
    {
        var gate = new Mutex(false, SwitchName);
        bool owned;
        try { owned = gate.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        return new Gate(gate, owned);
    }

    private sealed class Gate(Mutex mutex, bool owned) : IDisposable
    {
        public void Dispose()
        {
            if (owned) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> args, string workingDirectory, string? assembly = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"))
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add(executable);
        if (!string.IsNullOrEmpty(assembly)) start.ArgumentList.Add(assembly);
        foreach (string arg in args)
            if (arg != RelaunchFlag) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(RelaunchFlag);
        return start;
    }

    // The previous "default console/terminal" values (null means there was none) and setting the classic console.
    public static Dictionary<string, object?> ForceConhostDelegation()
    {
        var saved = new Dictionary<string, object?>();
        if (!OperatingSystem.IsWindows()) return saved;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey, writable: true);
            foreach (string name in DelegationValues)
            {
                saved[name] = key.GetValue(name);
                key.SetValue(name, ConhostDelegation, RegistryValueKind.String);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log("Could not temporarily select the classic console: " + error.Message);
        }
        return saved;
    }

    public static void RestoreDelegation(Dictionary<string, object?> saved)
    {
        if (!OperatingSystem.IsWindows() || saved.Count == 0) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey, writable: true);
            foreach (var (name, value) in saved)
                if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
                else key.SetValue(name, value);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log("Could not restore the console settings: " + error.Message);
        }
    }

    // Restore the console settings saved on relaunch (the RestorePath file) and delete the file.
    public static void RestorePending()
    {
        try
        {
            if (!File.Exists(RestorePath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(RestorePath));
            if (saved != null) RestoreDelegation(saved.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
            File.Delete(RestorePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log("Could not restore the console settings: " + error.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(AppConfig.ProjectRoot, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "console.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* the log matters less than startup */ }
    }
}
