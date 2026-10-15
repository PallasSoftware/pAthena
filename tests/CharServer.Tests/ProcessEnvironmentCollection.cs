namespace Athena.Net.CharServer.Tests;

/// <summary>
/// Tests that change process-wide environment variables must not run at the same time as any other test (configuration objects
/// read the environment when they are created). xUnit runs this collection alone, after the parallel tests; everything else
/// stays parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
