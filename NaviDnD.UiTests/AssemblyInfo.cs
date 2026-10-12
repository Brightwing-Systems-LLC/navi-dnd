using Xunit;

// GameConsole.WithGameConsole does FreeConsole()/AttachConsole() at the PROCESS level
// (not the thread) — if xUnit ran two game-driving tests in parallel in the same
// test host process, they would tear the console away from each other. So the whole assembly runs strictly
// sequentially, no matter how many independent GameSession cases appear.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
