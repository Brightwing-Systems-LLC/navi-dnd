using System.Text;
using System.Text.Json;

namespace NaviDnD;

// The player's own notes (journal → [F10] NOTES). They live in a SEPARATE file next to the game
// (game{N}.notes.json), not in WorldState: neither the AI context (AiContextBuilder) nor the MCP server (it reads only
// the game file) sees them or can see them. Added and removed without the AI; the DM can only
// append an entry (response key "playerNote", Storage) — it cannot read them.
public static class PlayerNotes
{
    public sealed class Note
    {
        public string Text { get; set; } = "";
        public int Day { get; set; }
        public string? PartOfDay { get; set; }
        public DateTime Created { get; set; }
        public bool ByMaster { get; set; } // the entry was added by the DM (AI) — it never reads the notes itself
    }

    // File: { formatVersion, notes: [...] } (StorageFormat; the old bare array is rewritten by the migration).
    public sealed class NotesFile
    {
        public int FormatVersion { get; set; } = StorageFormat.Current;
        public List<Note> Notes { get; set; } = [];
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private static List<Note>? _notes;
    private static string? _loadedFor;

    public static string PathFor(string savePath) => Path.ChangeExtension(savePath, ".notes.json");

    private static string CurrentPath => PathFor(Storage.SavePath);

    // Notes of the current game (re-read when the game changes).
    public static List<Note> All
    {
        get
        {
            if (_notes != null && _loadedFor == CurrentPath) return _notes;
            _loadedFor = CurrentPath;
            try
            {
                _notes = File.Exists(CurrentPath)
                    ? JsonSerializer.Deserialize<NotesFile>(File.ReadAllText(CurrentPath, Encoding.UTF8), _json)?.Notes ?? []
                    : [];
            }
            catch
            {
                // Could not be read (corrupt, not migrated) — keep a copy alongside so the next write does not wipe the notes.
                _notes = [];
                try { File.Copy(CurrentPath, CurrentPath + ".unreadable", overwrite: false); } catch { }
            }
            return _notes;
        }
    }

    public static void Add(string text, int day, string? partOfDay, bool byMaster = false)
    {
        All.Add(new Note { Text = text.Trim(), Day = day, PartOfDay = partOfDay, Created = DateTime.Now, ByMaster = byMaster });
        Save();
    }

    public static void RemoveAt(int index)
    {
        if (index < 0 || index >= All.Count) return;
        All.RemoveAt(index);
        Save();
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(CurrentPath, JsonSerializer.Serialize(new NotesFile { Notes = All }, _json), Encoding.UTF8);
        }
        catch { /* not saved — the notes live until the end of the session */ }
    }
}
