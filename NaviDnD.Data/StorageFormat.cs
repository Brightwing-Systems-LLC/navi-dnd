namespace NaviDnD;

// Format version of data files (saves, draft, hero sheets, worlds with locations, notes) — the
// formatVersion field. New files are written with the current version; old ones are rewritten by
// NaviDnD.Migrations.StorageMigration (when the game updates). Changed the data format? Bump Current and add a migration step.
public static class StorageFormat
{
    public const int Current = 2;
}
