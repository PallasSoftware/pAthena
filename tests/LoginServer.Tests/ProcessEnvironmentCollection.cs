namespace Athena.Net.LoginServer.Tests;

/// <summary>
/// Tests that change process-wide environment variables must not run at the same time as any other test: configuration objects
/// read the environment when they are created (for example <c>new LoginConfig()</c>), so a concurrent change makes unrelated
/// tests fail at random. Putting them in this collection makes xUnit run them alone, after the parallel tests. Everything else
/// still runs in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
