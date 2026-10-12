# Developing NaviDnD

## Running from source

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and one of the clients: [Codex CLI](https://developers.openai.com/codex/cli) or [Claude Code](https://code.claude.com/docs/en/quickstart). You need an account with access to the chosen model.
2. Download the repository and open PowerShell in its folder.
3. Build the game and the tool server:

   ```powershell
   dotnet build NaviDnD.McpServer/NaviDnD.McpServer.csproj
   ```

4. Start the game in a separate console window:

   ```powershell
   Start-Process .\NaviDnD\bin\Debug\net9.0\NaviDnD.exe
   ```

5. In the **Settings** menu pick the AI. Specify the path to its exe if it isn't found on PATH, and the game, world and hero models available to your account.
   - **Codex:** run `codex login` once. No separate token is needed in the game. Start with Sol 6.1 and medium reasoning if your account has access to the model.
   - **Claude:** sign in with `claude auth login` and leave the token field empty, or get a token using the instructions below.
6. Choose **New game** and create a character.

The storage folders and the settings file are created automatically on first launch; saves, worlds and heroes as you play. Data lives in `NaviDnD/Storage/`, logs in `NaviDnD/logs/`. Both folders are excluded from Git. Close the game before rebuilding. To update the sources, `git pull` and build again.

### Finding the clients

On every launch the game looks for the clients in the background: PATH, standard folders, npm and the VS Code, Cursor and Windsurf extensions. Stale paths are fixed automatically, working manual paths are kept; the menu doesn't wait for the search to finish. If only Codex is found, it is selected automatically. Initial models: Codex — Sol 6.1 with medium reasoning, Claude — Opus 5.5.

### Claude token for the game settings

1. Install [Claude Code](https://code.claude.com/docs/en/quickstart). A token requires a Claude Pro, Max, Team or Enterprise subscription.
2. Run `claude setup-token` in PowerShell. If the command isn't found, give the path to the exe: `& "C:\path\to\claude.exe" setup-token`.
3. Open the suggested link, sign in to your Claude account and confirm access. If the browser shows a confirmation code, paste it into the terminal.
4. Copy the OAuth token. In the game open **Settings**, choose **Claude** and paste it into the **Claude token** field.

An API key from the Anthropic Console does not work here. The token is stored locally in `Storage/settings.json`, which is excluded from Git. If the token has expired, run `claude setup-token` again. [Official instructions](https://code.claude.com/docs/en/authentication#generate-a-long-lived-token).

## Silero voice-over

In the release build Silero, the Russian model, Python and CPU PyTorch are included in the installer; the bundle lives in `Speech/silero/`. Running from source without the bundle requires Python 3.10–3.12 x64; to install: `powershell -NoProfile -ExecutionPolicy Bypass -File NaviDnD/Speech/setup-silero.ps1`. To build a standalone release, the Silero cache must use Python 3.12. [Silero v5.5](https://github.com/snakers4/silero-models) is used.

With voice-over enabled, the DM sends a single `text` with SSML markup: on screen the game hides the tags, stress marks and emphasis, while Silero gets the pauses and intonation settings. The `speechEnabled:true/false` flag is passed as a single line in the context; with voice-over off the DM sends plain text. If the markup is invalid, plain speech is used. When the game starts with voice-over enabled, the model is loaded and warmed up silently in the background. If there is no sound, check the status in the settings and `logs/speech.log`.

## Checking the code

```powershell
dotnet test NaviDnD.Tests/NaviDnD.Tests.csproj
```

Don't run the tests for the whole solution unless you need to: `NaviDnD.PromptTests` makes real requests to the model and spends quota/money. More about Codex: [codex.md](codex.md).

## Languages

English is the source language. On-screen text goes through `L.T("English string")`, with substitutions `L.F("Day {0}", day)`; text that is saved or sent to the DM in the game's language (history, generator names) goes through `L.W`/`L.WF`; plurals through `L.Plural(n, "day", "days")`. The Russian translation lives in `NaviDnD.Data/Localization/ru.json` under the same key (plural forms under `"day|days"`); installer strings also go into `NaviDnD.Installer/ru.json`. The DM's service instructions are in English and are not wrapped. `LocalizationTests` lists strings without a translation. Details and boundaries: [localization.md](localization.md).

## Save format and migrations

Data files (saves, notes, hero cards, worlds with locations) have a `formatVersion` field — `StorageFormat.Current`. If you change how data is laid out in the file (renaming a field, new key values, moving things), bump `StorageFormat.Current` and add a step to `NaviDnD/Migrations/StorageMigration.cs` (from version N to N+1, over JSON), with a test in `StorageMigrationTests`. The game code reads only the current format.

The steps are run by `NaviDnD.exe --migrate <game folder>`: the update helper runs it after patches; when running from source, the game itself does it at startup (or manually after `git pull`). Before writing, the files are copied to `Storage/Backup/format-<N>-<time>`; the log is `logs/migrations.log`.

## Updates

The release build checks for a new version in the background on launch; manually via **Updates** in the main menu. After the SHA256 check the game closes, updates without a separate installer window and starts again. Saves, settings and the Silero model are preserved. Logs: `logs/updates.log`.

Starting with 1.1.5 updates arrive as **patches**: only changed files are downloaded. Before applying, the game verifies the SHA256 of what was downloaded and of all files in the installed version; the patch is applied by the helper `Updater/NaviDnD-Setup-win-x64.exe` (a copy of the installer, installed together with the game). Files are replaced with a backup: on any error the previous files are restored. If the game files are damaged or the version is too old, the game downloads the full base build and the patches for it.

**Moving to patches (versions up to and including 1.1.4).** The old game doesn't understand patches and updates with the full build (about 500 MB). The new installer installs it, downloads the patches and installs the helper. If the transition fails, the game stays on base version 1.1.4 and offers to update again.

The `NaviDnD-payload.*` files are parts of the base build (the game, .NET and Silero voice-over), split because of the file size limit. The installer installs the base and brings it up to the current version with patches from `releases/patches/`; if they aren't next to it, it downloads them from the latest release.

## Publishing a new version

Change `Version` in `Directory.Build.props`, commit the sources, run `build-release.ps1`, commit `releases/` and `.gitlab/release.json`, then push the commit and the tag `v<version>`. GitLab CI creates a release with links to the base, the installer and the patches. For updates without authentication the project must be public.

`powershell -NoProfile -ExecutionPolicy Bypass -File build-release.ps1`:

- **with no switches — a patch release** (the usual case). The game and the MCP server are rebuilt and compared against the file manifest of the previous version (`releases/manifests/<version>.json`); `releases/patches/` gets `NaviDnD-patch-<from>-<to>.zip` with only the changed files, a list of removed files and the full list of source files with SHA256. The base (`NaviDnD-payload.*` with .NET and Silero) is not rebuilt or re-uploaded — the links point to the tag where it was published. The installer is rebuilt only if its sources (`NaviDnD.Installer`) changed — so they must be committed before the build. The patch chain is described in `releases/NaviDnD-updates.json`, while `SHA256SUMS.txt` stays in the old format for clients up to 1.1.4;
- `-Base` — a new full base with Silero (when the voice-over changes or the patches have grown too large): archive parts, installer, file manifest, a patch chain from scratch;
- `-AdoptBase` — declare already-built parts the base without rebuilding (this is how 1.1.4 became the base for patches).

Game data is not included; resources are taken only from files tracked by Git. Sources and releases are kept in one repository. Checking without a window: `NaviDnD-Setup-win-x64.exe --extract <folder>` installs the base, `--apply-patches <folder> <patches folder>` applies the chain.

Intermediate release files are created in the system temp folder and are removed automatically after the build, including on failure.
