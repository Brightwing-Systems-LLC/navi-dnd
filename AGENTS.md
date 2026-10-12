# Working on NaviDnD

Respond in English. Running and releases: [docs/development.md](docs/development.md). Architecture: [docs/architecture.md](docs/architecture.md). Checks: [docs/testing.md](docs/testing.md). Open tasks: [docs/roadmap.md](docs/roadmap.md).

- The engine is independent of the rules system; D&D is defined in `NaviDnD/Prompts/Dnd5e`.
- Claude and Codex share one JSON protocol and the game's MCP tools. Check the consequences of a response in the state, not just that the JSON is valid.
- Cut unnecessary model calls and repeated data requests, while keeping dice rolls, target selection and rules checks.
- When adding a field, check the model, `Storage.Apply*`, `AiContextBuilder.Serialize`, the MCP keys, the related prompts and the existing tests.
- New interface text goes through `L.T`/`L.F`, text in the game's language (story, names) through `L.W`/`L.WF`. The key is the English string in the code; the Russian translation goes in `NaviDnD.Data/Localization/ru.json`. Service instructions to the DM are English and don't go through `L` (see [docs/localization.md](docs/localization.md)).
- Code compares data by keys (`NaviDnD.Data/Models/Keys.cs`), not by words in any language. If you change the data file format — bump `StorageFormat.Current` and add a migration step (`NaviDnD/Migrations`); the old format is not supported in code.
- Keep prompt edits short: one line per rule, no extra examples. Replace outdated text instead of piling on context.
- Preserve the user's changes, personal saves, settings and portraits. Secrets must never end up in source code, examples or Git.
- The usual check: `dotnet test NaviDnD.Tests/NaviDnD.Tests.csproj`. Don't run the whole solution's tests: PromptTests make real requests, and the UI tests launch the Windows app.
- At the end, briefly report the changes and the checks.
