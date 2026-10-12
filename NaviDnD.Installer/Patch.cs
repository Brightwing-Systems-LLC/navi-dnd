using System.IO.Compression;
using System.Text.Json;

namespace NaviDnD.Installer;

// A release patch is a ZIP: patch.json (the description) and files/<path> (new and changed files).
//  BaseFiles — the full file list of the source version with SHA256: used to check that exactly that version is installed
//  and nothing is damaged (otherwise a full base install); Files — new/changed files with the SHA256 of the result;
//  Removed — source-version files to delete. A patch never touches Storage/, logs/ or the Updater/ helper.
internal sealed record Patch(string BaseVersion, string Version, Dictionary<string, string> BaseFiles,
    Dictionary<string, string> Files, string[] Removed)
{
    internal const string ManifestName = "patch.json";
    internal const string FilesPrefix = "files/";

    internal static Patch Read(ZipArchive archive)
    {
        using var input = archive.GetEntry(ManifestName)?.Open() ?? throw new InvalidDataException(L.T("The patch has no description."));
        Patch patch;
        try { patch = JsonSerializer.Deserialize<Patch>(input, UpdateManifest.Json) ?? throw new InvalidDataException(L.T("Invalid patch.")); }
        catch (JsonException) { throw new InvalidDataException(L.T("Invalid patch description.")); }
        if (patch.BaseVersion == null || patch.Version == null
            || UpdateManifest.ParseVersion(patch.Version) <= UpdateManifest.ParseVersion(patch.BaseVersion))
            throw new InvalidDataException(L.T("Invalid patch version."));
        if (patch.BaseFiles == null || patch.Files == null || patch.Removed == null || !patch.Files.ContainsKey("release-version.txt"))
            throw new InvalidDataException(L.T("Incomplete patch description."));
        foreach (var file in patch.BaseFiles.Concat(patch.Files))
        {
            RelativePath(file.Key);
            if (file.Value is not { Length: 64 } || !file.Value.All(Uri.IsHexDigit)) throw new InvalidDataException(L.T("Invalid file checksum."));
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in patch.Files.Keys.Concat(patch.Removed))
            if (!names.Add(RelativePath(name))) throw new InvalidDataException(L.T("Duplicate patch path."));
        foreach (string name in patch.Removed)
            if (!patch.BaseFiles.ContainsKey(name)) throw new InvalidDataException(L.T("A file to remove is missing from the base."));
        foreach (string name in patch.Files.Keys)
            if (archive.GetEntry(FilesPrefix + name) == null) throw new InvalidDataException(L.T("The patch is missing a file: ") + name);
        return patch;
    }

    // Whether the patch's source version is installed. full — every base file (damaged install); otherwise only the ones
    // the patch replaces or removes (later patches in the chain: the rest was already checked by the first).
    internal void VerifyBase(string root, bool full, Action<double>? progress = null)
    {
        string versionFile = Path.Combine(root, "release-version.txt");
        string version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
        if (version != BaseVersion) throw new InvalidDataException(L.F("The patch is for version {0}, installed is {1}.", BaseVersion, version.Length > 0 ? version : L.T("unknown")));
        var check = full ? BaseFiles.ToList() : BaseFiles.Where(f => Files.ContainsKey(f.Key) || Removed.Contains(f.Key)).ToList();
        for (int i = 0; i < check.Count; i++)
        {
            var (name, sha) = (check[i].Key, check[i].Value);
            string path = Path.Combine(root, RelativePath(name));
            RejectLinks(root, path);
            if (!File.Exists(path) || UpdateManifest.Sha256(path) != sha.ToLowerInvariant())
                throw new InvalidDataException(L.T("A game file is modified or missing: ") + name);
            progress?.Invoke((i + 1) / (double)check.Count);
        }
    }

    internal static string RelativePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Contains('\0') ||
            name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException(L.T("Invalid patch path: ") + name);
        string first = name.Split('/')[0];
        if (first.Equals("Storage", StringComparison.OrdinalIgnoreCase) || first.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Updater", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".update-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Invalid patch path: ") + name);
        return name;
    }

    internal static void RejectLinks(string root, string target)
    {
        for (string? path = target; path != null && path.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("An installed file must not be a link."));
    }
}

internal static class PatchInstaller
{
    // Patches in order as one operation: the first checks the whole base, each next one that it starts from the previous
    // one's version. Files are extracted and verified by SHA256 before replacing; replaced and removed files go to a backup,
    // and on any error everything is restored. Storage/ and logs/ are not touched. Returns the installed version.
    internal static string ApplyChain(string root, IReadOnlyList<string> patchZips, Action<string, double?>? progress = null)
    {
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("The install folder must not be a link."));
        if (patchZips.Count == 0) throw new ArgumentException(L.T("No patches."));
        string work = Path.Combine(root, ".update-" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(work, "new"), backup = Path.Combine(work, "old");
        var changed = new List<(string Target, string? Backup)>();
        var removedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool keepBackup = false;
        string? version = null;
        try
        {
            for (int i = 0; i < patchZips.Count; i++)
            {
                using var archive = ZipFile.OpenRead(patchZips[i]);
                var patch = Patch.Read(archive);
                if (version != null && patch.BaseVersion != version) throw new InvalidDataException(L.T("Gap in the patch chain."));
                progress?.Invoke(L.F("Checking game files ({0})…", patch.BaseVersion), 0);
                patch.VerifyBase(root, full: i == 0, f => progress?.Invoke(L.F("Checking game files ({0})…", patch.BaseVersion), f));

                string stage = Path.Combine(staging, i.ToString());
                int n = 0;
                foreach (var (name, sha) in patch.Files)
                {
                    string temp = SafePath(stage, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                    archive.GetEntry(Patch.FilesPrefix + name)!.ExtractToFile(temp);
                    if (UpdateManifest.Sha256(temp) != sha.ToLowerInvariant()) throw new InvalidDataException(L.T("Corrupted patch file: ") + name);
                    progress?.Invoke(L.F("Updating to {0}…", patch.Version), ++n / (double)(patch.Files.Count + patch.Removed.Length));
                }
                foreach (var name in patch.Files.Keys)
                {
                    string target = SafePath(root, name);
                    Patch.RejectLinks(root, target);
                    if (Directory.Exists(target)) throw new IOException(L.T("A folder is in place of the file: ") + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string? old = null;
                    if (File.Exists(target))
                    {
                        old = SafePath(backup, $"{i}/{name}");
                        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                        File.Move(target, old);
                    }
                    changed.Add((target, old));
                    File.Move(SafePath(stage, name), target);
                }
                foreach (var name in patch.Removed)
                {
                    string target = SafePath(root, name);
                    Patch.RejectLinks(root, target);
                    if (!File.Exists(target)) continue;
                    string old = SafePath(backup, $"{i}/{name}");
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    File.Move(target, old);
                    changed.Add((target, old));
                    removedDirs.Add(Path.GetDirectoryName(target)!);
                }
                version = patch.Version;
            }
        }
        catch (Exception installError)
        {
            try
            {
                foreach (var file in changed.AsEnumerable().Reverse())
                {
                    if (File.Exists(file.Target)) File.Delete(file.Target);
                    if (file.Backup != null) File.Move(file.Backup, file.Target);
                }
            }
            catch (Exception rollbackError)
            {
                keepBackup = true;
                throw new IOException(L.T("Update and rollback failed. Backup files are kept in: ") + backup,
                    new AggregateException(installError, rollbackError));
            }
            throw;
        }
        finally { if (!keepBackup && Directory.Exists(work)) Directory.Delete(work, true); }

        // Remove folders left empty after deletion (not above the game folder).
        foreach (string dir in removedDirs.OrderByDescending(d => d.Length))
            for (string? path = dir; path != null && path.Length > root.Length; path = Path.GetDirectoryName(path))
            {
                try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); else break; }
                catch (IOException) { break; }
            }
        return version!;
    }

    private static string SafePath(string root, string relative)
    {
        string target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Invalid path in the patch."));
        return target;
    }
}
