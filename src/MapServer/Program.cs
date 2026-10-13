using Athena.Net.MapServer.Startup;

// The exit code matters: 75 tells the orchestrator that the World connection was lost (see WorldConnectionWatchdog).
return await MapServerApp.RunAsync(args);
