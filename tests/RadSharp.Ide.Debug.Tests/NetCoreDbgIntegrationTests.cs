using System.Diagnostics;
using RadSharp.Ide.Debug.Acquisition;

namespace RadSharp.Ide.Debug.Tests;

/// <summary>
/// Skipped unless netcoredbg is already available (explicit NETCOREDBG variable, PATH, or the tool cache). Set
/// RADSHARP_TEST_DOWNLOAD_NETCOREDBG=1 to let the test download it into the cache first.
/// </summary>
public sealed class NetCoreDbgFactAttribute : FactAttribute
{
    public NetCoreDbgFactAttribute()
    {
        if (NetCoreDbgIntegrationTests.ResolveAdapter() is null)
            Skip = "netcoredbg is not available (set NETCOREDBG, put it on PATH, or set RADSHARP_TEST_DOWNLOAD_NETCOREDBG=1).";
    }
}

[Collection("netcoredbg-env")]
public class NetCoreDbgIntegrationTests
{
    public static string? ResolveAdapter()
    {
        try
        {
            var acq = new NetCoreDbgAcquisition();
            var existing = acq.FindExisting();
            if (existing is not null) return existing;
            if (Environment.GetEnvironmentVariable("RADSHARP_TEST_DOWNLOAD_NETCOREDBG") == "1")
                return acq.EnsureAsync().GetAwaiter().GetResult();
        }
        catch (Exception) { }
        return null;
    }

    private const string Program = """
        var a = 20;
        var b = a + 22;
        System.Console.WriteLine(b);
        return 0;
        """;

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <UseAppHost>false</UseAppHost>
            <DebugType>portable</DebugType>
            <Optimize>false</Optimize>
          </PropertyGroup>
        </Project>
        """;

    [NetCoreDbgFact]
    public async Task Breakpoint_local_step_over_continue_to_exit()
    {
        var adapter = ResolveAdapter()!;
        var dir = Path.Combine(Path.GetTempPath(), "radsharp-dbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Target.csproj"), Csproj);
            var source = Path.Combine(dir, "Program.cs");
            File.WriteAllText(source, Program);
            var dll = await Build(dir);

            var store = new BreakpointStore();
            var bp = store.Add(source, 2);
            await using var session = DebugSession.StartAdapter(adapter, store);

            var stopQueue = System.Threading.Channels.Channel.CreateUnbounded<Protocol.StoppedEventArgs>();
            var exited = new TaskCompletionSource<Protocol.ExitedEventArgs>();
            var terminated = new TaskCompletionSource<bool>();
            var stdout = new System.Text.StringBuilder();
            session.Stopped += (_, e) => stopQueue.Writer.TryWrite(e);
            session.Exited += (_, e) => exited.TrySetResult(e);
            session.Terminated += (_, _) => terminated.TrySetResult(true);
            session.Output += (_, e) => { if (e.Category == "stdout") lock (stdout) stdout.Append(e.Output); };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await session.LaunchAsync(new DebugLaunchOptions { Program = "dotnet", Args = [dll], WorkingDirectory = dir }, cts.Token);

            // 1. the breakpoint is hit and was verified
            var hit = await stopQueue.Reader.ReadAsync(cts.Token);
            Assert.Equal("breakpoint", hit.Reason);
            Assert.True(store.Get(bp.Id)!.Verified);
            var threadId = hit.ThreadId!.Value;
            var frame = (await session.GetStackTraceAsync(threadId, ct: cts.Token)).StackFrames[0];
            Assert.Equal(2, frame.Line);

            // 2. read a local
            var locals = await Locals(session, frame.Id, cts.Token);
            Assert.Equal("20", locals["a"]);

            // 3. step over: now on line 3, b is 42
            await session.NextAsync(threadId, cts.Token);
            var stepped = await stopQueue.Reader.ReadAsync(cts.Token);
            Assert.Equal("step", stepped.Reason);
            frame = (await session.GetStackTraceAsync(threadId, ct: cts.Token)).StackFrames[0];
            Assert.Equal(3, frame.Line);
            locals = await Locals(session, frame.Id, cts.Token);
            Assert.Equal("42", locals["b"]);
            Assert.Equal("42", (await session.EvaluateAsync("a + b - 20", frame.Id, ct: cts.Token)).Result);
            Assert.Equal(1, store.Get(bp.Id)!.TimesHit);

            // 4. continue to exit
            await session.ContinueAsync(threadId, cts.Token);
            Assert.Equal(0, (await exited.Task.WaitAsync(cts.Token)).ExitCode);
            await terminated.Task.WaitAsync(cts.Token);
            lock (stdout) Assert.Contains("42", stdout.ToString());
            Assert.Equal(DebugSessionState.Ended, session.State);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }


    private static async Task<Dictionary<string, string>> Locals(DebugSession session, int frameId, CancellationToken ct)
    {
        var scopes = await session.GetScopesAsync(frameId, ct);
        var locals = scopes.First(s => s.Name.Contains("Local", StringComparison.OrdinalIgnoreCase));
        var vars = await session.GetVariablesAsync(locals.VariablesReference, ct);
        return vars.ToDictionary(v => v.Name, v => v.Value);
    }

    private static async Task<string> Build(string dir)
    {
        var psi = new ProcessStartInfo("dotnet", "build -c Debug -nologo -v:q")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException("Building the target app failed:\n" + await output + await error);
        return Directory.GetFiles(Path.Combine(dir, "bin"), "Target.dll", SearchOption.AllDirectories).Single();
    }
}
