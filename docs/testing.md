# Testing

## Logic

```powershell
dotnet test NaviDnD.Tests/NaviDnD.Tests.csproj
```

The usual run, with no real requests to the model. Building the game and the server: `dotnet build NaviDnD.McpServer/NaviDnD.McpServer.csproj`. Close the game before rebuilding: Windows locks the exe and dll files.

## UI

`NaviDnD.UiTests` starts a real Windows process and reads the console. Build the game first. Use the existing fixtures and mocks; some scenarios may call the model, so pick the tests you need deliberately.

`GameSession` sets `NAVIDND_TEST_WORLDSTATE`, `NAVIDND_TEST_MOCKED_ACTIONS`, `NAVIDND_TEST_MOCK_RESPONSES_DIR`. The mocks live in `Fixtures/MockResponses`, not in the game prompts. Repeated calls read `response2.json`, `response3.json` and so on; if those don't exist, the original `response.json`.

The override changes the load path, but the game may save state in its own folder. That's why `GameSession` backs up each session's files and restores them in `Dispose`. After stopping the process, `WaitForExit` is required before restoring. Don't weaken this protection and don't run UI tests in parallel: attaching to the console works at the process level.

`ConsoleHandles` opens `CONOUT$`/`CONIN$`: testhost's `GetStdHandle` may stay redirected to a pipe. `NAVIDND_TEST_STEP_DELAY_MS` adds pauses between steps; a mock delay lets you check the spinner.

## Real models

`NaviDnD.PromptTests` uses separate storage and the real CLI providers. Requests spend quota or money, and the results are nondeterministic. Don't run the tests for the whole solution for a routine code check.

Pick a scenario with `--filter`. For Codex set `NAVIDND_PROMPT_PROVIDER=codex`, `NAVIDND_CODEX_MODEL` and, if needed, `NAVIDND_CODEX_REASONING`; Claude is the default. Example: [codex.md](codex.md).

`PromptScenario.ApplyAndParse` applies the response using the engine's protocol. The tests check game consequences: equipment, hit points, targets and tool calls. An auto-responder simulates the player's rolls and choices. `NAVIDND_RECORD_PROMPTS=1` updates the mocks — enable it deliberately and review the response before committing.
