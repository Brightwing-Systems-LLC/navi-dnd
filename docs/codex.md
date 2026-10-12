# Playing with Codex

You need an up-to-date Codex CLI with `--ignore-user-config` and `--ephemeral`, and a completed `codex login`. The game uses that sign-in; no separate API key is needed.

In the settings menu choose "AI → Codex". "GAME MODEL", "WORLD MODEL" and "HERO MODEL" are switched separately with the arrow keys: Sol 6.1, Astra 6, Luna 6 (availability depends on the account). The game model handles turns and triggers, the world model the concept and the start of the adventure, the hero model creating and describing the character. An empty world/hero setting inherits the game model; an empty game model uses the CLI's default model, not the model from your personal config.toml. "CODEX PATH" is `codex` if it's on PATH, otherwise the full path to codex.exe. All values are saved in `Storage/settings.json`.

"CODEX REASONING": start with "MEDIUM". "FAST" shortens reasoning, but check how well complex rules are followed on your own scenarios. "DEEP" is useful for complex scenes; responses take longer. The hero description and JSON repair always use `low`.

The DM's rules are passed via `model_instructions_file`, the turn context via stdin. Personal CLI settings are not loaded; the shell and web search are disabled. An action gets only its own game MCP tools, allowed to be called without a separate CLI confirmation; rolls and target selection are still requested by the game itself. A failure to start a required MCP server stops the request.

Requests with MCP are not retried automatically after a network failure: the tools may already have changed state or received the player's roll. A CLI error is not turned into a successful response from an intermediate message. The model, reasoning depth and duration are written to `logs/ai_*.log`.

To test on a separate save (a real model call):

```powershell
$env:NAVIDND_PROMPT_PROVIDER = 'codex'
$env:NAVIDND_CODEX_MODEL = 'gpt-6.1-sol'
$env:NAVIDND_CODEX_REASONING = 'medium'
dotnet test NaviDnD.PromptTests --filter 'FullyQualifiedName~InventoryPromptTests'
```

Other scenarios: `InteractiveResponsePromptTests` — an attack roll and target selection, `CombatPromptTests` — combat. Change the filter for the check you need; don't enable mock recording unless necessary. One successful scenario doesn't guarantee the others are correct: compare timings and game invariants on identical scenes.

Check on October 6, 2026 with `gpt-6.1-sol`: swapping weapons passed with `medium` and `low` (about 16 s in both cases); an attack with a `roll_dice` call took about 21 s; an ambiguous target with `select_target` about 22 s. These are single measurements with automatic player responses, not a guarantee of latency or quality. On simple equipment changes lowering the reasoning gave no noticeable gain; for now it makes sense to keep `medium`.
