using System.Reflection;
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

    [Fact]
    public void The_host_executable_loads_a_projects_build_output_and_draws_its_controls()
    {
        // The SampleControls fixture's output as Visual Studio's TargetPath, in a folder of its own.
        var built = Path.GetDirectoryName(typeof(HostProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "SampleControls").Value!)!;
        var dir = TestFiles.TempDir();
        foreach (var file in Directory.EnumerateFiles(built)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        var form = Path.Combine(dir, "Gauges.radform.json");
        File.WriteAllText(form, """
            {
              "format": "radform",
              "version": "1.0",
              "root": {
                "class": "Form",
                "name": "frmGauges",
                "height": 200,
                "width": 300,
                "children": [
                  { "class": "Gauge", "name": "gauge1", "left": 8, "top": 8, "width": 200, "height": 100 }
                ]
              }
            }
            """);

        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RadSharpDesignHost.exe" : "RadSharpDesignHost");
        var pipeName = "rsh-" + Guid.NewGuid().ToString("N")[..12];
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(psi)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            using var client = new HostClient(pipe);
            client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "process test" });
            client.Expect<P.HelloAck>();
            client.Send(new P.SetProjectAssembly { DocId = 1, Path = Path.Combine(dir, "SampleControls.dll") });
            Assert.Contains("Gauge (Project)", client.Expect<P.Log>(l => l.Text.StartsWith("Project controls from")).Text);
            var panes = client.Open(1, form);
            var frame = client.Show(panes.Surface, 640, 400);
            // Gauge paints itself #3366CC (BGRA), with the colour from the project's other assembly, SampleData.dll.
            int blue = 0;
            for (int i = 0; i + 3 < frame.Pixels.Length; i += 4)
                if (frame.Pixels[i] == 0xCC && frame.Pixels[i + 1] == 0x66 && frame.Pixels[i + 2] == 0x33) blue++;
            Assert.True(blue > 150 * 80, $"the Gauge is drawn ({blue} pixels of its colour)");
        }
        finally
        {
            if (!process.WaitForExit(10_000)) process.Kill();
        }
    }
}
