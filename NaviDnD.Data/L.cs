using System.Text.Json;

namespace NaviDnD;

// Languages. The translation key is the English string in the code; Russian lives in Localization/ru.json (embedded in
// the assembly); with no translation the English string is shown. A key may carry a context suffix after "##"
// ("north##to") when one English word has several Russian translations; the suffix is never shown. Two languages:
// · interface (L.T/L.F/L.Plural) — menus and screens, a player setting;
// · world (L.W/L.WF/L.WPlural) — what is saved and sent to the DM: history, summaries, generator names. The game's
//   language, chosen when the world is created (WorldState.Language), is set when the game loads.
// The file is also compiled into the installer (language from settings.json).
public static class L
{
    public const string Russian = "ru", English = "en";

    public static string Language { get; private set; } = English;
    public static bool IsEnglish => Language == English;

    public static string World { get; private set; } = English;
    public static bool WorldIsEnglish => World == English;

    private static readonly Lazy<Dictionary<string, string>> Ru = new(Load);
    public static IReadOnlyDictionary<string, string> RussianTable => Ru.Value;

    public static string Normalize(string? language) => language == Russian ? Russian : English;

    // Screens read strings while drawing: a language change shows from the next redraw.
    public static void SetLanguage(string? language) => Language = Normalize(language);
    public static void SetWorld(string? language) => World = Normalize(language);

    public static string T(string en) => In(Language, en);
    public static string W(string en) => In(World, en);

    // Template with {0}, {1}… — like string.Format; the argument order in a translation may differ.
    public static string F(string en, params object?[] args) => string.Format(T(en), args);
    public static string WF(string en, params object?[] args) => string.Format(W(en), args);

    // Noun for a number: English singular and plural. Russian takes three forms (1, 2–4, 5+) from ru.json under the
    // key "one|many", written "form1|form2-4|form5+".
    public static string Plural(long n, string one, string many) => PluralIn(Language, n, one, many);
    public static string WPlural(long n, string one, string many) => PluralIn(World, n, one, many);

    public static string Strip(string key)
    {
        int i = key.IndexOf("##", StringComparison.Ordinal);
        return i < 0 ? key : key[..i];
    }

    private static string In(string language, string en) =>
        language == Russian && Ru.Value.TryGetValue(en, out var ru) ? ru : Strip(en);

    private static string PluralIn(string language, long n, string one, string many)
    {
        long a = Math.Abs(n);
        if (language != Russian || !Ru.Value.TryGetValue(one + "|" + many, out var forms) || forms.Split('|') is not [var f1, var f2, var f5])
            return a == 1 ? one : many;
        return a % 10 == 1 && a % 100 != 11 ? f1 : a % 10 is >= 2 and <= 4 && a % 100 is < 12 or > 14 ? f2 : f5;
    }

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("NaviDnD.Localization.ru.json");
        if (stream == null) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
