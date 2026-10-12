using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using NaviDnD.Installer;
using NaviDnD.Display;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaviDnD.Helpers;

internal static class GameUpdates
{
    private const string ProjectApi = "https://gitlab.com/api/v4/projects/navitalevich%2Fnavi-dnd";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    internal static string CurrentVersion => typeof(GameUpdates).Assembly.GetName().Version!.ToString(3);
    private static Task<Release?>? pending;
    internal sealed record Asset(string Name, string Url);
    internal sealed record Release(string Version, string Page, Asset[] Assets);

    // The updater is the Windows installer: elsewhere there is no background check, and the Updates screen points to git.
    internal static bool Supported => OperatingSystem.IsWindows();

    internal static void Initialize()
    {
        if (Supported && Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") == null)
            pending = CheckAsync();
    }

    private static string ErrorPath => Path.Combine(AppConfig.ProjectRoot, "Storage", "update-error.txt");
    internal static string MenuLabel => File.Exists(ErrorPath) ? L.T("UPDATE ERROR") : pending?.IsCompletedSuccessfully == true && pending.Result is { } release
        ? L.F("UPDATE {0}", release.Version) : L.T("UPDATES");

    internal static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.TrimStart('v'), out var next) && Version.TryParse(current, out var installed) && next > installed;

    private static async Task<Release?> CheckAsync(CancellationToken token = default, bool reportErrors = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await Client.GetAsync(ProjectApi + "/releases/permalink/latest", timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = json.RootElement;
            string version = root.GetProperty("tag_name").GetString()!;
            if (!IsNewer(version, CurrentVersion)) return null;
            var assets = root.GetProperty("assets").GetProperty("links").EnumerateArray()
                .Select(a => new Asset(a.GetProperty("name").GetString()!, a.GetProperty("url").GetString()!)).ToArray();
            return new Release(version, root.GetProperty("_links").GetProperty("self").GetString()!, assets);
        }
        catch (Exception error)
        {
            Log(error.Message);
            if (reportErrors) throw;
            return null;
        }
    }

    internal static async Task<bool> ShowAsync(UpdateDisplay view)
    {
        view.Open();
        if (!Supported)
        {
            await view.ConfirmAsync(L.F("Automatic updates are available only on Windows. You have version {0}: to update, use git pull and rebuild.", CurrentVersion), false);
            return false;
        }
        using var cancellation = new CancellationTokenSource();
        try
        {
            if (File.Exists(ErrorPath))
            {
                string error = await File.ReadAllTextAsync(ErrorPath);
                Log(error);
                await view.ConfirmAsync(L.T("Previous update: ") + error, false);
                File.Delete(ErrorPath);
                return false;
            }
            pending = CheckAsync(cancellation.Token, reportErrors: true);
            var release = await view.RunAsync(pending, cancellation);
            if (release == null)
            {
                await view.ConfirmAsync(L.F("You have the latest version {0}.", CurrentVersion), false);
                return false;
            }
            if (!await view.ConfirmAsync(L.F("Version {0} is available. Saves and settings will be kept.", release.Version), true)) return false;
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "release-version.txt")))
            {
                await view.ConfirmAsync(L.T("Running from source: use git pull and rebuild. Only the installed game updates itself."), false);
                return false;
            }
            string directory = Path.Combine(Path.GetTempPath(), "NaviDnD-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                // A release with an update description — via patches (and the helper in the game folder); otherwise the old full install.
                var updates = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.FileName);
                var (program, arguments) = updates != null
                    ? await PreparePatchesAsync(view, updates, directory, cancellation)
                    : await PrepareFullAsync(view, release, directory, cancellation);
                var start = new ProcessStartInfo(program) { UseShellExecute = true };
                view.Set(L.T("Download complete. Restarting the game…"), 1, L.T("Saves and settings are kept"));
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                _ = Process.Start(start) ?? throw new IOException(L.T("Could not start the installer."));
                return true;
            }
            catch
            {
                Directory.Delete(directory, true);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            Log(error.Message);
            await view.ConfirmAsync(L.T("Update not installed: ") + error.Message, false);
            return false;
        }
    }

    // The old path (a release without NaviDnD-updates.json): the installer and all parts of the full build, installer run with --update-silent.
    private static async Task<(string, string[])> PrepareFullAsync(UpdateDisplay view, Release release, string directory, CancellationTokenSource cancellation)
    {
        var sums = release.Assets.Single(a => a.Name == "SHA256SUMS.txt");
        string manifest = Path.Combine(directory, sums.Name);
        view.Set(L.T("Getting the file list…"));
        await view.RunAsync(DownloadAsync(sums, manifest, cancellation.Token), cancellation);
        var files = ParseChecksums(await File.ReadAllTextAsync(manifest));
        if (!files.ContainsKey(UpdateManifest.InstallerName) || !files.Keys.Any(n => n.StartsWith("NaviDnD-payload.")))
            throw new InvalidDataException(L.T("The release has no installer files."));
        var list = files.Select(f => (release.Assets.Single(a => a.Name == f.Key), f.Value)).ToList();
        await DownloadVerifiedAsync(view, list, directory, cancellation);
        return (Path.Combine(directory, UpdateManifest.InstallerName),
            ["--update-silent", AppContext.BaseDirectory, Environment.ProcessId.ToString()]);
    }

    // Patches from the installed version (small ZIPs), with a base check; a corrupt base or a version outside the chain gets the parts
    // of the full build and all patches from that base. The helper applies them: the installed Updater/ (the same as in the release) is copied to
    // a temporary folder, otherwise downloaded. The game closes, the helper applies everything with rollback and starts it again.
    private static async Task<(string, string[])> PreparePatchesAsync(UpdateDisplay view, Asset updates, string directory, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        string manifestPath = Path.Combine(directory, updates.Name);
        view.Set(L.T("Getting the update list…"));
        await view.RunAsync(DownloadAsync(updates, manifestPath, token), cancellation);
        var manifest = UpdateManifest.Parse(await File.ReadAllTextAsync(manifestPath, token));
        string root = AppContext.BaseDirectory;
        string installed = (await File.ReadAllTextAsync(Path.Combine(root, "release-version.txt"), token)).Trim();
        var patches = manifest.PatchesFrom(installed);
        if (patches is { Length: > 0 })
        {
            await DownloadVerifiedAsync(view, patches.Select(p => (new Asset(p.Name, p.Url), p.Sha256)).ToList(), directory, cancellation);
            string first = Path.Combine(directory, patches[0].Name);
            try
            {
                await view.RunAsync(Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(first);
                    Patch.Read(archive).VerifyBase(root, full: true, f => view.Set(L.T("Checking game files…"), f));
                    return true;
                }, token), cancellation);
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                Log("Base does not match the patch, doing a full install: " + error.Message);
                patches = null;
            }
        }
        if (patches == null)
        {
            // Full build: the parts of the unchanged base and the whole patch chain from it.
            foreach (string patch in Directory.GetFiles(directory, "NaviDnD-patch-*.zip")) File.Delete(patch);
            var full = manifest.Base.Files.Select(f => (new Asset(f.Name, f.Url), f.Sha256))
                .Concat(manifest.Patches.Select(p => (new Asset(p.Name, p.Url), p.Sha256))).ToList();
            await DownloadVerifiedAsync(view, full, directory, cancellation);
        }
        string helper = Path.Combine(directory, UpdateManifest.InstallerName);
        string installedHelper = Path.Combine(root, UpdateManifest.HelperPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(installedHelper) && UpdateManifest.Sha256(installedHelper) == manifest.Installer.Sha256.ToLowerInvariant())
            File.Copy(installedHelper, helper);
        else
            await DownloadVerifiedAsync(view, [(new Asset(manifest.Installer.Name, manifest.Installer.Url), manifest.Installer.Sha256)], directory, cancellation);
        return (helper, ["--apply-update", root, Environment.ProcessId.ToString(), directory]);
    }

    private static async Task DownloadVerifiedAsync(UpdateDisplay view, List<(Asset Asset, string Sha256)> files, string directory,
        CancellationTokenSource cancellation)
    {
        int index = 0;
        foreach (var (asset, sha) in files)
        {
            index++;
            string label = L.F("Downloading {0}/{1}: {2}", index, files.Count, asset.Name);
            view.Set(label, 0);
            string target = Path.Combine(directory, asset.Name);
            await view.RunAsync(DownloadAsync(asset, target, cancellation.Token, (read, total) =>
                view.Set(label, total > 0 ? (double)read / total : null, (total > 0 ? L.F("{0:F1} of {1:F1} MB", read / 1048576.0, total / 1048576.0) : L.F("{0:F1} MB downloaded", read / 1048576.0)) + " • " + L.T("Esc — cancel"))), cancellation);
            view.Set(L.T("Verifying checksum: ") + asset.Name, 1);
            await using var input = File.OpenRead(target);
            string hash = await view.RunAsync(HashAsync(input, cancellation.Token), cancellation);
            if (!hash.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Checksum mismatch: ") + asset.Name);
        }
    }

    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+(NaviDnD-Setup-win-x64\.exe|NaviDnD-payload\.\d{3})$");
            if (!match.Success || !files.TryAdd(match.Groups[2].Value, match.Groups[1].Value))
                throw new InvalidDataException(L.T("Invalid checksum list."));
        }
        return files;
    }

    private static async Task<string> HashAsync(Stream input, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(input, token));

    private static async Task<bool> DownloadAsync(Asset asset, string destination, CancellationToken token,
        Action<long, long?>? progress = null)
    {
        var uri = new Uri(asset.Url);
        if (uri.Scheme != "https" || uri.Host != "gitlab.com") throw new InvalidDataException(L.T("Invalid update address."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(destination);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await CopyWithProgressAsync(input, output, response.Content.Headers.ContentLength, progress, timeout.Token);
        return true;
    }

    internal static async Task CopyWithProgressAsync(Stream input, Stream output, long? length,
        Action<long, long?>? progress, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            received += read;
            progress?.Invoke(received, length);
        }
    }

    private static void Log(string message)
    {
        try { string dir = Path.Combine(AppConfig.ProjectRoot, "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "updates.log"), $"{DateTime.Now:O} {message}\n"); } catch { }
    }
}
