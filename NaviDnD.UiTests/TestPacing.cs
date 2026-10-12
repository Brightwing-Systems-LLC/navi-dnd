namespace NaviDnD.UiTests;

// A pause between scenario steps — so an automated run can be followed visually
// by eye. Off by default (tests run at full speed); enabled by the
// NAVIDND_TEST_STEP_DELAY_MS environment variable (ms between steps, e.g. "1500").
internal static class TestPacing
{
    private static readonly int StepDelayMs =
       //5000;
        int.TryParse(Environment.GetEnvironmentVariable("NAVIDND_TEST_STEP_DELAY_MS"), out int v) ? Math.Max(0, v) : 0;

    public static void Step()
    {
        if (StepDelayMs > 0) Thread.Sleep(StepDelayMs);
    }
}
