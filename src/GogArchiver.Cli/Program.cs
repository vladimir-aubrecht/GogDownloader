using GogArchiver.Cli;
using GogArchiver.Core.Auth;
using GogArchiver.Core.Download;

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
catch (BackupDirectoryConflictException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (IOException ex)
{
    // Most often the disk filled up mid-download. A stack trace would bury the one fact that
    // matters: nothing was lost, and running the same command again resumes.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"I/O error: {ex.Message}");
    Console.Error.WriteLine("Downloaded files were kept. Run the same command again to resume.");
    return 4;
}
