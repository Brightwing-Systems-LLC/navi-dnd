using System.Text.RegularExpressions;

namespace NaviDnD.Helpers;

// The game's temporary folders in %TEMP% that were not deleted right away: the installer copy after an update (the helper runs from
// it and cannot delete itself), an interrupted download, a Claude/Codex CLI working folder held by a hung
// MCP server. At startup only those older than a day are deleted: fresh ones may be in use by a second game instance or the helper.
internal static class TempCleanup
{
    private static readonly Regex Owned = new(
        @"^(?:NaviDnD-update-|NaviDnD-patches-|navidnd-|navidnd-codex-)[0-9a-f]{32}$|^NaviDnD-speech-[0-9a-f]{32}\.wav$",
        RegexOptions.CultureInvariant);

    internal static void Run() => Task.Run(() => Sweep(Path.GetTempPath(), DateTime.UtcNow.AddDays(-1)));

    internal static int Sweep(string temp, DateTime olderThanUtc)
    {
        int removed = 0;
        try
        {
            foreach (var item in new DirectoryInfo(temp).EnumerateFileSystemInfos("*avi*"))
            {
                if (!Owned.IsMatch(item.Name) || item.LastWriteTimeUtc > olderThanUtc
                    || (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    if (item is DirectoryInfo directory) directory.Delete(recursive: true);
                    else item.Delete();
                    removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }   // in use — next time
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
