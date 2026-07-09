using GogDownloader.Cli;
using GogDownloader.Core.Auth;

var cts = new CancellationTokenSource();
var interrupts = 0;

Console.CancelKeyPress += (_, e) =>
{
    // Unwinding a download and asking what to do with it takes a moment. A second Ctrl+C
    // means the user wants out now, so let it kill the process.
    if (Interlocked.Increment(ref interrupts) > 1) return;

    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("\nInterrupting... press Ctrl+C again to force quit.");
};

try
{
    return await CommandLine.RunAsync(args, cts.Token);
}
catch (GogAuthException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Interrupted.");
    return 130;
}
