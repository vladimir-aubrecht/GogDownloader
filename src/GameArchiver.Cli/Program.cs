using GameArchiver.Cli;
using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
var interrupts = 0;
Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Increment(ref interrupts) > 1) return;
    e.Cancel = true;
    cancellation.Cancel();
};
// Docker stops its main process with SIGTERM. Let downloads finish cancellation/cleanup
// instead of terminating between archive publication and its metadata checkpoint.
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    if (Interlocked.Increment(ref interrupts) > 1) return;
    context.Cancel = true;
    cancellation.Cancel();
});
return await ArchiveCommandLine.RunAsync(args, cancellation.Token);
