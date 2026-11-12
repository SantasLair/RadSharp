using System.Diagnostics;
using System.IO.Pipes;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>The real RadSharpDesignHost executable over a named pipe, per the process contract.</summary>
[Collection("DesignHost")]
public class HostProcessTests
{
    [Fact]
    public void The_host_executable_serves_a_client_over_a_named_pipe_and_exits_when_it_disconnects()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RadSharpDesignHost.exe" : "RadSharpDesignHost");
        Assert.True(File.Exists(exe), $"{exe} was built next to the tests");
        // Short: on macOS and Linux a pipe is a Unix socket under the temp folder, and socket paths are limited to
        // 104 characters (macOS's temp folder alone is about 50).
        var pipeName = "rsh-" + Guid.NewGuid().ToString("N")[..12];
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(psi)!;
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            using (var client = new HostClient(pipe))
            {
                client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "process test" });
                Assert.Equal(P.ProtocolInfo.Version, client.Expect<P.HelloAck>().ProtocolVersion);
                var panes = client.Open(1, TestFiles.HelloForm());
                var frame = client.Show(panes.Surface, 640, 400, scale: 1.5f);
                Assert.Equal(640 * 400 * 4, frame.Pixels.Length);
                Assert.True(TestFiles.NotBlank(frame.Pixels));
            }
            Assert.True(process.WaitForExit(10_000), "the host exits when the pipe closes");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
        lock (stderr) Assert.Contains("Client connected", stderr.ToString());
    }
}
