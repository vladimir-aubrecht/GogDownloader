using GameArchiver.Cli;

using var cancellation = new CancellationTokenSource();
var interrupts = 0;
Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Increment(ref interrupts) > 1) return;
    e.Cancel = true;
    cancellation.Cancel();
};
return await ArchiveCommandLine.RunAsync(args, cancellation.Token);
