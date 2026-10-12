namespace NaviDnD.Clients;

// The AI cannot be started because of setup (CLI not installed, not logged in): "retry the action" won't help here —
// the player is shown the error text itself, even without debug mode (GameAiClient.RecordError).
public class AiSetupException(string message) : Exception(message);
