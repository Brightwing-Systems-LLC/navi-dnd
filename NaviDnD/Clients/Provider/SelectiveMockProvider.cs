using System.Text;

namespace NaviDnD.Clients;

// Wraps a real provider and intercepts requests for actions listed in mockedActions.
// If response.json exists next to the system prompt, returns it instead of calling the real provider.
// Use ["*"] to mock all actions, or specific names like ["SendAction", "CreateNewGame"].
// mockResponsesDirOverride (optional) — read response.json from a DIFFERENT base folder instead of
// actionPath (the real Prompts/{RuleSet}/{Action}/) — used by E2E tests to keep
// their mocks separate from the git-tracked prompts (see NaviDnD.UiTests/GameSession.cs).
internal sealed class SelectiveMockProvider(IAiProvider inner, IReadOnlySet<string> mockedActions, string? mockResponsesDirOverride = null) : IAiProvider
{
    private const int ChunkSize = 8;
    private const int DelayMs = 10;

    // How many times the mock has already been called for each action name — needed to DISTINGUISH
    // consecutive calls of the SAME action within one test. For example,
    // FireEnemyTurn uses the same actionPath ("SendAction") as a regular player action —
    // without numbering both calls would get the SAME response file, so the dialog
    // would show the same text twice (see EnemyTurnBehaviorScenarioTests).
    private readonly Dictionary<string, int> _callCounts = [];

    // The mock does not answer instantly — it simulates real network/model latency so the waiting UI
    // (Spinner.While — "Creating character...", "Creating world..." etc.) actually has time
    // to render and can be checked in E2E tests rather than just assumed to exist.
    private const int MockResponseDelayMs = 5000;

    public async Task<string> Complete(IReadOnlyList<string> systemBlocks, string userMessage, string actionPath, string model)
    {
        if (TryGetMock(actionPath, out string mock))
        {
            await Task.Delay(MockResponseDelayMs);
            return mock;
        }
        return await inner.Complete(systemBlocks, userMessage, actionPath, model);
    }

    public async Task<string> CompleteWithStreaming(
        IReadOnlyList<string> systemBlocks, string userMessage, string actionPath, string model,
        Action<string>? onChunk)
    {
        if (TryGetMock(actionPath, out string mock))
        {
            await Task.Delay(MockResponseDelayMs);
            if (onChunk != null)
                for (int i = 0; i < mock.Length; i += ChunkSize)
                {
                    onChunk(mock.Substring(i, Math.Min(ChunkSize, mock.Length - i)));
                    await Task.Delay(DelayMs);
                }
            return mock;
        }
        return await inner.CompleteWithStreaming(systemBlocks, userMessage, actionPath, model, onChunk);
    }

    private bool TryGetMock(string actionPath, out string content)
    {
        string actionName = Path.GetFileName(actionPath);
        if (!mockedActions.Contains("*") && !mockedActions.Contains(actionName))
        {
            content = "";
            return false;
        }

        int callNumber = _callCounts[actionName] = _callCounts.GetValueOrDefault(actionName) + 1;
        string baseDir = mockResponsesDirOverride != null
            ? Path.Combine(mockResponsesDirOverride, actionName)
            : actionPath;

        // 1st call — always response.json (compatible with all existing tests). 2nd and
        // later — first try responseN.json (a distinct response for a repeated call of the same
        // action, e.g. a regular player SendAction → FireEnemyTurn), and only if that
        // file is missing — silently repeat response.json (the old behavior for tests where the second
        // call doesn't need to differ from the first).
        string stubPath = callNumber > 1 && File.Exists(Path.Combine(baseDir, $"response{callNumber}.json"))
            ? Path.Combine(baseDir, $"response{callNumber}.json")
            : Path.Combine(baseDir, "response.json");

        if (!File.Exists(stubPath))
        {
            content = "";
            return false;
        }

        content = File.ReadAllText(stubPath, Encoding.UTF8);
        return true;
    }
}
