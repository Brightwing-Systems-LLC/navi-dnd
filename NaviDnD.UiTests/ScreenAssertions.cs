using NaviDnD.Data;

namespace NaviDnD.UiTests;

// Shared screen checks reused across test cases (GameScenarioTests,
// NewGameScenarioTests, ...) — frames and tabs are the same regardless of the scenario.
internal static class ScreenAssertions
{
    private static readonly char[] LeftBorderChars = ['╭', '╰', '├', '│'];
    private static readonly char[] RightBorderChars = ['╮', '╯', '┤', '│'];

    // Frame: wherever a row actually contains something (not the empty tail of the buffer),
    // there must be frame characters on the left and right at exactly the expected columns,
    // not shifted/overflowing content.
    public static void CheckBorders(List<string> rows, string context, List<string> failures)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            string line = rows[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // DisplayConfig.LeftMargin (=1) — the column before the frame itself (BorderDrawer.WriteLeftMargin),
            // the frame character comes RIGHT AFTER the margin, not in column 0.
            if (line.Length <= DisplayConfig.LeftMargin) continue;
            char left = line[DisplayConfig.LeftMargin];
            if (!LeftBorderChars.Contains(left))
                failures.Add($"[{context}] Row {i}: no frame character on the left (column {DisplayConfig.LeftMargin}) (found '{left}'): «{line.TrimEnd()}»");

            if (line.Length > GameConsole.RightBorderCol)
            {
                char right = line[GameConsole.RightBorderCol];
                if (!RightBorderChars.Contains(right) && !char.IsWhiteSpace(right))
                    failures.Add($"[{context}] Row {i}: no frame character on the right (column {GameConsole.RightBorderCol}) (found '{right}') — content may have overflowed the card: «{line.TrimEnd()}»");
            }
        }
    }

    // Checks that the declared text (F1/F2/... + label) is actually present in the title —
    // and that the label comes right after the prefix, i.e. the button hasn't been renamed or gone missing.
    public static void RequireTab(string title, string prefix, string expectedLabel, string context, List<string> failures)
    {
        int idx = title.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            failures.Add($"[{context}] Button «{prefix}» not found in the title: «{title.TrimEnd()}»");
            return;
        }
        int labelStart = idx + prefix.Length;
        string rest = title[labelStart..];
        if (!rest.TrimStart().StartsWith(expectedLabel, StringComparison.Ordinal))
            failures.Add($"[{context}] Expected label «{expectedLabel}» after «{prefix}», title: «{title.TrimEnd()}»");
    }

    // +2: DisplayConfig.LeftMargin(1) + frame(1) — mirrors MouseUiHelper.ComputeTitleTabs.
    public static (int startX, int endX)? FindTab(string title, string prefix)
    {
        int idx = title.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        int end = idx + prefix.Length;
        while (end < title.Length && title[end] != ' ') end++;
        return (2 + idx + prefix.Length, 2 + end); // only the name is clickable, without the «[Fn]»
    }

    // A real text color check via a screenshot of the window's client area
    // (ScreenCapture.SampleCellColor) — the console's legacy attributes (ReadConsoleOutputAttribute)
    // don't reflect the 24-bit ANSI the game paints with, so the only reliable way is
    // to grab a pixel and compare its RGB with a tolerance (font antialiasing/scaling can
    // shift the value slightly).
    public static void CheckTextColor(
        int gamePid, List<string> rows, string searchText, (int r, int g, int b) expected, int tolerance,
        (byte r, byte g, byte b) background, string context, List<string> failures)
    {
        int rowIndex = rows.FindIndex(r => r.Contains(searchText));
        if (rowIndex < 0) return; // the missing text itself is already reported by a separate check

        int col = rows[rowIndex].IndexOf(searchText, StringComparison.Ordinal);
        if (col < 0) return;

        (byte r, byte g, byte b) sample;
        try { sample = GameConsole.SampleCellColor(gamePid, col + 2, rowIndex, background); }
        catch (Exception ex)
        {
            failures.Add($"[{context}] Failed to grab a pixel to check the color of «{searchText}»: {ex.Message}");
            return;
        }

        var (r, g, b) = sample;
        bool close = Math.Abs(r - expected.r) <= tolerance
                  && Math.Abs(g - expected.g) <= tolerance
                  && Math.Abs(b - expected.b) <= tolerance;
        if (!close)
            failures.Add($"[{context}] Color of «{searchText}»: expected ~({expected.r},{expected.g},{expected.b}), got ({r},{g},{b}) — tolerance ±{tolerance}.");
    }

    // Like CheckTextColor, but for a single character in a known (col, row) cell — without the +2 shift
    // (that shift is only needed to land INSIDE a multi-character word rather than on its edge).
    // Used for the action/speed symbols in the legend (● ■ ▲), which differ only by
    // color, not text — finding them by substring isn't enough.
    public static void CheckCellColor(
        int gamePid, int col, int row, (int r, int g, int b) expected, int tolerance,
        (byte r, byte g, byte b) background, string context, List<string> failures)
    {
        (byte r, byte g, byte b) sample;
        try { sample = GameConsole.SampleCellColor(gamePid, col, row, background); }
        catch (Exception ex)
        {
            failures.Add($"[{context}] Failed to grab pixel ({col},{row}): {ex.Message}");
            return;
        }

        var (r, g, b) = sample;
        bool close = Math.Abs(r - expected.r) <= tolerance
                  && Math.Abs(g - expected.g) <= tolerance
                  && Math.Abs(b - expected.b) <= tolerance;
        if (!close)
            failures.Add($"[{context}] Cell color ({col},{row}): expected ~({expected.r},{expected.g},{expected.b}), got ({r},{g},{b}) — tolerance ±{tolerance}.");
    }
}
