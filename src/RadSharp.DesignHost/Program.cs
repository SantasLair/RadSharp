using System.Diagnostics;
using System.IO.Pipes;
using RadSharp.Drawing;
using RadSharp.DesignHost;

// RadSharpDesignHost --pipe <name> [--parent-pid <pid>]
// Serves one client over the named pipe; exits when the pipe closes or the parent process dies.
// Diagnostics go to stderr.

string? pipeName = null;
int? parentPid = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length: pipeName = args[++i]; break;
        case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid): parentPid = pid; i++; break;
        default:
            Console.Error.WriteLine($"[error] Unknown argument \"{args[i]}\".");
            return 2;
    }
}
if (string.IsNullOrEmpty(pipeName))
{
    Console.Error.WriteLine("Usage: RadSharpDesignHost --pipe <name> [--parent-pid <pid>]");
    return 2;
}

if (parentPid is { } parent) WatchParent(parent);

if (Environment.GetEnvironmentVariable("RADSHARP_THEME") == "dark") Theme.Current = Theme.Dark;

using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
Console.Error.WriteLine($"[info] RADSharp design host {DesignHostServer.HostVersion} waiting on pipe \"{pipeName}\".");
pipe.WaitForConnection();
Console.Error.WriteLine("[info] Client connected.");
new DesignHostServer(pipe).Run();
Console.Error.WriteLine("[info] Client disconnected; exiting.");
return 0;

static void WatchParent(int pid)
{
    Process parent;
    try { parent = Process.GetProcessById(pid); }
    catch (ArgumentException)
    {
        Console.Error.WriteLine($"[error] Parent process {pid} is not running.");
        Environment.Exit(3);
        return;
    }
    var thread = new Thread(() =>
    {
        parent.WaitForExit();
        Console.Error.WriteLine("[info] Parent process exited; exiting.");
        Environment.Exit(0);
    }) { IsBackground = true, Name = "Parent watch" };
    thread.Start();
}
