using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]
// Many integration fixtures mutate process-wide environment/logging state and
// create SQLite schemas on the same disk. Serialize collections; concurrency
// regressions still run their explicit Task.WhenAll scenarios within a test.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
