using System.Text.RegularExpressions;

namespace NaviDnD;

// Highlights the model's text in the dialog: creature/object names in their map color (Russian case forms included),
// mechanics in [...] dimmed, damage/healing/"A→B" changes and roll outcomes in color. Words in both game languages.
// A pure function over a string: computed when visible lines are drawn, not when the cache is built.
public sealed class DialogHighlighter
{
    public sealed record Palette(List<int> Mechanics, List<int> Bad, List<int> Good);

    private readonly List<(Regex regex, List<int> color)> _names;
    private readonly Palette _palette;

    // A whole number or dice formula: "8", "2к4=5", "1d6+2=5", "2к4 + 3".
    private const string Num = @"\d+(?:\s*[кkdд]\s*\d+)?(?:\s*[+\-]\s*\d+(?:\s*[кkdд]\s*\d+)?)*(?:\s*=\s*\d+)?";

    private static readonly Regex Damage = new(
        $@"(?<![\p{{L}}\d])((урон|damage)\p{{L}}*\s*{Num}|{Num}\s*(урон|колющ|рубящ|дробящ|огн|холод|некрот|психич|излуч|звук|кисл|силов|электр|яд|damage|piercing|slashing|bludgeoning|fire|cold|necrotic|psychic|radiant|thunder|acid|force|lightning|poison)\p{{L}}*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Heal = new(
        $@"(?<![\p{{L}}\d])((лечени|исцел|восстан|heal|restor|regain)\p{{L}}*\s*\+?{Num}|\+\d+\s*(хп|hp)(?![\p{{L}}\d]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Change = new(@"(\d+)\s*→\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex GoodWord = new(@"(?<!\p{L})(попадани|успех|успешн|крит\p{L}*\s+успех|hits?(?!\s+points?)(?!\p{L})|success|crit\p{L}*\s+success)\p{L}*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BadWord = new(@"(?<!\p{L})(промах|провал|miss(?:es|ed)?(?!\p{L})|fail)\p{L}*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string Vowels = "аяоеёуюыийьАЯОЕЁУЮЫИЙЬ";
    // A short stem ("Гул" from "Гуль") is an ordinary word on its own ("гул"), so an ending is required.
    private const string ShortStemEndings = "(ь|а|я|у|ю|е|и|ы|й|ой|ей|ом|ем|ам|ям|ами|ями|ах|ях)";

    // symbols: map symbols ("SK1", "NUR"): exact, case-sensitive match.
    public DialogHighlighter(IEnumerable<(string name, List<int> color)> names, Palette palette,
        IEnumerable<(string symbol, List<int> color)>? symbols = null)
    {
        _palette = palette;
        _names = names
            .Where(n => !string.IsNullOrWhiteSpace(n.name) && n.color is { Count: 3 })
            .SelectMany(n => PatternsFor(n.name).Select(p => (p, n.color)))
            .OrderByDescending(x => x.p.Length) // longer names first: "Torvin's Skeleton" beats "Skeleton"
            .Select(x => (new Regex(x.p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), x.color))
            .Concat((symbols ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.symbol) && s.color is { Count: 3 })
                .Select(s => (new Regex(@"(?<![\p{L}\d])" + Regex.Escape(s.symbol.Trim()) + @"(?![\p{L}\d])"), s.color)))
            .ToList();
    }

    // Color of each character in the line (null = default color). bracketDepth: whether a [...] bracket is open
    // at the start of the line (mechanics carry over to the message's next lines); updated to the end of the line.
    public List<(string text, List<int>? color)> Segments(string line, ref int bracketDepth)
    {
        var colors = new List<int>?[line.Length];

        int depth = bracketDepth;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '[') depth++;
            if (depth > 0) colors[i] = _palette.Mechanics;
            if (line[i] == ']' && depth > 0) depth--;
        }
        bracketDepth = depth;

        Paint(colors, Damage, _palette.Bad);
        Paint(colors, Heal, _palette.Good);
        Paint(colors, BadWord, _palette.Bad);
        Paint(colors, GoodWord, _palette.Good);
        foreach (Match m in Change.Matches(line))
        {
            if (!int.TryParse(m.Groups[1].Value, out int from) || !int.TryParse(m.Groups[2].Value, out int to) || from == to) continue;
            Fill(colors, m.Index, m.Length, to < from ? _palette.Bad : _palette.Good);
        }

        var named = new bool[line.Length];
        foreach (var (regex, color) in _names)
            foreach (Match m in regex.Matches(line))
            {
                if (named.AsSpan(m.Index, m.Length).Contains(true)) continue; // already taken by a longer name
                Fill(colors, m.Index, m.Length, color);
                named.AsSpan(m.Index, m.Length).Fill(true);
            }

        var result = new List<(string, List<int>?)>();
        int start = 0;
        for (int i = 1; i <= line.Length; i++)
        {
            if (i < line.Length && ReferenceEquals(colors[i], colors[start])) continue;
            result.Add((line[start..i], colors[start]));
            start = i;
        }
        return result;

        void Paint(List<int>?[] target, Regex regex, List<int> color)
        {
            foreach (Match m in regex.Matches(line)) Fill(target, m.Index, m.Length, color);
        }
    }

    private static void Fill(List<int>?[] colors, int index, int length, List<int> color)
    {
        for (int i = index; i < index + length; i++) colors[i] = color;
    }

    // The full name (every word by its stem) and, for compound names, the first word on its own
    // ("Скелет Торвина" matches both "скелету Торвина" and plain "скелет").
    private static IEnumerable<string> PatternsFor(string name)
    {
        var words = name.Split([' ', '-', ','], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        const string left = @"(?<![\p{L}\d])", right = @"(?![\p{L}\d])";
        yield return left + string.Join(@"[\s\-]+", words.Select(WordPattern)) + right;
        if (words.Length > 1 && words[0].Length >= 4)
            yield return left + WordPattern(words[0]) + right;
    }

    private static string WordPattern(string word)
    {
        // An English name has no case forms: "Goblin", "Goblins", "Goblin's".
        if (!word.Any(c => c is >= '\u0400' and <= '\u04FF')) return Regex.Escape(word) + "(?:'s|s|es)?";
        string stem = word.TrimEnd(Vowels.ToCharArray());
        if (stem.Length >= 4) return Regex.Escape(stem) + @"\p{L}{0,3}";
        if (stem.Length >= 2) return Regex.Escape(stem) + ShortStemEndings;
        return Regex.Escape(word);
    }
}
