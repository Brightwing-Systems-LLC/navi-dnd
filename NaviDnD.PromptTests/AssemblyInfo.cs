using Xunit;

// Storage.ValidateKeys caches known fields in a static (process-wide, not per-instance)
// Dictionary<Type,HashSet<string>> — running test classes from this project in parallel
// (different Storage instances, but the same static cache) is correctly caught by .NET as
// "Operations that change non-concurrent collections must have exclusive access" and fails tests
// that never even get to the actual AI call. On top of that, every test writes to the same
// file (AppConfig.ProjectRoot resolves to NaviDnD.PromptTests/Storage/ for the whole project, see
// PromptScenario) — parallel Storage.Save() calls would also race over one file. Strictly
// sequential, just like NaviDnD.UiTests (see its AssemblyInfo.cs).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
