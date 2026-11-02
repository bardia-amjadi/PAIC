using PAIC.InstallerRuntime;

// Ctrl+C cancels the running operation; the engine then rolls back what it has done so far.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return await InstallerHost.RunAsync(args, Console.Out, Console.In, cts.Token);
