using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NaviDnD.Helpers;

// Called by its only consumer, Speech (one request at a time). Each model gets its own process, which stays in
// memory between lines: "en_*" voices use the English model (v3_en — a game file in Speech/models), the rest the Russian one (v5_5_ru —
// bundled with Silero). Both can be preloaded (Speech.WarmUp); switching language does not reload a model.
internal static class SileroSpeech
{
    private sealed record Worker(Process Process, Task<string> Errors);

    private static readonly Dictionary<string, Worker> Workers = [];
    private static string BundledRoot => Path.Combine(AppConfig.ProjectRoot, "Speech", "silero");
    private static bool HasBundle => File.Exists(Path.Combine(BundledRoot, ".ready"));
    private static string Root => HasBundle ? BundledRoot : Path.Combine(AppConfig.ProjectRoot, "Storage", "Speech", "silero");
    private static string Python => !OperatingSystem.IsWindows() ? Path.Combine(Root, HasBundle ? "python" : "venv", "bin", "python3")
        : HasBundle ? Path.Combine(Root, "python", "python.exe") : Path.Combine(Root, "venv", "Scripts", "python.exe");

    internal static string EnglishModelPath => Path.Combine(AppConfig.ProjectRoot, "Speech", "models", "v3_en.pt");

    public static bool IsEnglishVoice(string voice) => voice.StartsWith("en_", StringComparison.Ordinal);

    // Silero is already installed (always in a release build): the model can be preloaded without downloading anything.
    public static bool Installed => File.Exists(Path.Combine(Root, ".ready"));

    public static async Task SynthesizeAsync(string text, string? ssmlText, string voice, string wav, string python,
        Action<string> status, CancellationToken token)
    {
        if (!Installed)
        {
            status(L.T("Installing Silero and PyTorch"));
            var setup = CreateStart(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash");
            string[] setupArguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                   Path.Combine(AppConfig.ProjectRoot, "Speech", "setup-silero.ps1"), "-Python", python, "-Destination", Root]
                : [Path.Combine(AppConfig.ProjectRoot, "Speech", "setup-silero.sh"), "--python", FindPython(python), "--destination", Root];
            foreach (var arg in setupArguments)
                setup.ArgumentList.Add(arg);
            if (!OperatingSystem.IsWindows()) setup.Environment["PATH"] = UnixTools.ChildPath(null);
            using var process = Process.Start(setup) ?? throw new IOException(L.T("Could not start Silero installation"));
            using var kill = token.Register(() => Kill(process));
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr);
            Directory.CreateDirectory(Path.Combine(AppConfig.ProjectRoot, "logs"));
            await File.WriteAllTextAsync(Path.Combine(AppConfig.ProjectRoot, "logs", "silero-setup.log"),
                stdout.Result + stderr.Result, token);
            if (process.ExitCode != 0) throw new IOException(OperatingSystem.IsWindows()
                ? L.T("Silero installation failed. Python 3.10–3.12 x64 is required; check the Python path and your internet connection.")
                : L.T("Silero installation failed. Python 3.10–3.12 is required (for example: brew install python@3.12); check the Python path and your internet connection."));
        }
        string model = IsEnglishVoice(voice) ? EnglishModelPath : Path.Combine(Root, "v5_5_ru.pt");
        if (!File.Exists(model)) throw new FileNotFoundException(L.T("The voice model file is missing — reinstall the game."), model);
        var worker = await WorkerFor(model, status, token);
        try
        {
            status(L.T("Silero synthesis"));
            // The English v3 model takes twice as long at 48 kHz as at 24 kHz (and three times as long as the Russian v5.5); 24 kHz is enough for speech.
            int rate = IsEnglishVoice(voice) ? 24000 : 48000;
            string request = JsonSerializer.Serialize(new { text, ssml_text = ssmlText, voice, output = wav, sample_rate = rate });
            await worker.Process.StandardInput.WriteLineAsync(request.AsMemory(), CancellationToken.None);
            await worker.Process.StandardInput.FlushAsync(CancellationToken.None);
            // The line was stopped — do not abort synthesis (a killed process would have to be reloaded, which takes longer):
            // wait for the response so the protocol stays in sync, but no longer than 10 s — after that the process is considered hung.
            var read = worker.Process.StandardOutput.ReadLineAsync();
            await Task.WhenAny(read, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
            if (!read.IsCompleted && await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))) != read)
                throw new TimeoutException(L.T("Silero exited without a response"));
            string response = await read ?? throw new IOException(L.T("Silero exited without a response"));
            token.ThrowIfCancellationRequested();
            using var result = JsonDocument.Parse(response);
            if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new LineFailedException(L.T("Silero could not voice the line"));
        }
        // The process is broken (not responding, exited, protocol out of sync) — close it; a stopped or failed line leaves the process intact.
        catch (Exception e) when (e is not OperationCanceledException and not LineFailedException)
        {
            Close(model);
            throw;
        }
    }

    private sealed class LineFailedException(string message) : IOException(message);

    private static async Task<Worker> WorkerFor(string model, Action<string> status, CancellationToken token)
    {
        if (Workers.TryGetValue(model, out var alive) && !alive.Process.HasExited) return alive;
        Close(model);
        status(L.T("Loading the Silero model"));
        var start = CreateStart(Python);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in new[] { "-u", Path.Combine(AppConfig.ProjectRoot, "Speech", "silero_worker.py"), model })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException(L.T("Could not start Silero"));
        var worker = new Worker(process, process.StandardError.ReadToEndAsync());
        Workers[model] = worker;
        try
        {
            using var kill = token.Register(() => Kill(process));
            string? ready = await process.StandardOutput.ReadLineAsync(token);
            if (ready == null) throw new IOException(L.T("Silero did not load the model"));
            using var handshake = JsonDocument.Parse(ready);
            if (!handshake.RootElement.TryGetProperty("ready", out var readyValue) || !readyValue.GetBoolean())
                throw new IOException(L.T("Silero did not load the model"));
            return worker;
        }
        catch { Close(model); throw; }
    }

    // macOS/Linux: the configured interpreter if it exists, otherwise the newest supported python3.1x (PyTorch 2.6 and
    // numpy 1.26 have wheels for 3.10–3.12) on PATH, Homebrew, python.org or pyenv; the setup script checks the version.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string FindPython(string configured)
    {
        string home = UnixTools.Home;
        string[] extra = ["/opt/homebrew/opt/python@3.12/bin", "/opt/homebrew/opt/python@3.11/bin", "/opt/homebrew/opt/python@3.10/bin",
            "/usr/local/opt/python@3.12/bin", "/usr/local/opt/python@3.11/bin", "/usr/local/opt/python@3.10/bin",
            "/Library/Frameworks/Python.framework/Versions/3.12/bin", "/Library/Frameworks/Python.framework/Versions/3.11/bin",
            "/Library/Frameworks/Python.framework/Versions/3.10/bin", Path.Combine(home, ".pyenv", "shims"), Path.Combine(home, ".local", "bin")];
        if (configured.Trim() is { Length: > 0 } name && name != "python" && UnixTools.Which(name, extra) is { } chosen) return chosen;
        foreach (string candidate in new[] { "python3.12", "python3.11", "python3.10", "python3" })
            if (UnixTools.Which(candidate, extra) is { } found) return found;
        return "python3";
    }

    private static ProcessStartInfo CreateStart(string executable) => new(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static void Kill(Process? process) { try { process?.Kill(entireProcessTree: true); } catch { } }

    private static void Close(string model)
    {
        if (!Workers.Remove(model, out var worker)) return;
        Kill(worker.Process);
        worker.Process.Dispose();
    }

    public static void Close()
    {
        foreach (var model in Workers.Keys.ToList()) Close(model);
    }
}
