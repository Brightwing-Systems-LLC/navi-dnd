// The UI and world language (L), saves and the Storage folder are process-wide: tests that change them must not
// run in parallel with the rest. The suite is fast, so it runs sequentially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
