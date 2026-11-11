using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RadSharp.Design.Code;

/// <summary>Reads MSBuild / compiler diagnostic lines: <c>file(line,col): error CS0103: message [project]</c>.</summary>
public static partial class MsBuildOutput
{
    [GeneratedRegex(@"^\s*(?:(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)|(?<tool>[^:()]+?))\s*:\s*(?<sev>error|warning|info)\s+(?<code>[^\s:]+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticLine();

    /// <summary>Parses one output line; null if it isn't a diagnostic. Relative file names are resolved against
    /// <paramref name="baseDirectory"/> (the project folder). A diagnostic without a file (<c>MSBUILD : error …</c>) has an empty
    /// <see cref="BuildMessage.File"/> and line 0.</summary>
    public static BuildMessage? TryParse(string line, string? baseDirectory = null)
    {
        var m = DiagnosticLine().Match(line);
        if (!m.Success) return null;
        var file = m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : "";
        if (file.Length > 0 && baseDirectory != null && !Path.IsPathRooted(file)) file = Path.GetFullPath(Path.Combine(baseDirectory, file));
        int.TryParse(m.Groups["line"].Value, out var ln);
        int.TryParse(m.Groups["col"].Value, out var col);
        return new BuildMessage(file, ln, col, m.Groups["sev"].Value.ToLowerInvariant(), m.Groups["code"].Value, m.Groups["msg"].Value.Trim());
    }
}

/// <summary>The outcome of <see cref="ProjectBuild.BuildAsync"/>.</summary>
public sealed record BuildResult(int ExitCode, bool Cancelled, IReadOnlyList<BuildMessage> Messages, string? OutputFile)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled;
    public int Errors => Messages.Count(m => m.SeverityOf() == ProblemSeverity.Error);
    public int Warnings => Messages.Count(m => m.SeverityOf() == ProblemSeverity.Warning);
}

/// <summary>Build (Ctrl+Shift+B): <c>dotnet build</c> in Debug, streaming output and collecting diagnostics.</summary>
public static class ProjectBuild
{
    /// <summary>Builds the project. <paramref name="onLine"/> receives every output line (from a worker thread);
    /// diagnostics are also returned as <see cref="BuildMessage"/>s (each distinct one once). Cancelling kills the
    /// build's process tree and returns a result with <see cref="BuildResult.Cancelled"/>.</summary>
    public static async Task<BuildResult> BuildAsync(RadProject project, Action<string> onLine, string configuration = "Debug",
        string? extraArguments = null, IDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{project.ProjectFile}\" -c {configuration} -nologo -p:GenerateFullPaths=true -consoleloggerparameters:NoSummary {extraArguments}".TrimEnd())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.Directory,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;

        var messages = new List<BuildMessage>();
        var seen = new HashSet<BuildMessage>();
        string? output = null;
        void Line(string? line)
        {
            if (line == null) return;
            lock (messages)
            {
                if (MsBuildOutput.TryParse(line, project.Directory) is { } m && seen.Add(m)) messages.Add(m);
                var t = line.TrimEnd();
                var arrow = t.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0 && (t.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    output = t[(arrow + 4)..].Trim();
            }
            onLine(line);
        }

        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var kill = cancel.Register(() => AppProcess.KillTree(process));
        await process.WaitForExitAsync();
        bool cancelled = cancel.IsCancellationRequested;
        lock (messages) return new BuildResult(process.ExitCode, cancelled, [.. messages], process.ExitCode == 0 && !cancelled ? output : null);
    }
}

/// <summary>A started application process whose stdout and stderr stream to a callback, and which can be stopped.</summary>
public sealed class AppProcess : IDisposable
{
    readonly Process _process;
    readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    AppProcess(Process process) { _process = process; }

    /// <summary>Completes with the exit code once the process ended and all its output was delivered.</summary>
    public Task<int> Exited => _exited.Task;
    public bool HasExited => _exited.Task.IsCompleted;
    /// <summary>True if <see cref="Stop"/> ended it.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="arguments"/> (already quoted).</summary>
    public static AppProcess Start(string fileName, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.Start();
        var app = new AppProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync();
                app._exited.TrySetResult(process.ExitCode);
            }
            catch (Exception e) { app._exited.TrySetException(e); }
        });
        return app;
    }

    /// <summary>Starts a built application (<c>dotnet app.dll</c>) with a <see cref="RunConfiguration"/>.</summary>
    public static AppProcess StartProject(RadProject project, string assembly, RunConfiguration config, Action<string> onLine)
    {
        var extra = config.Arguments.Trim().Length > 0 ? " " + config.Arguments.Trim() : "";
        var dir = config.ResolveWorkingDirectory(project);
        return assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Start("dotnet", $"\"{assembly}\"" + extra, dir, config.Environment, onLine)
            : Start(assembly, extra.TrimStart(), dir, config.Environment, onLine);
    }

    /// <summary>Kills the process and everything it started.</summary>
    public void Stop()
    {
        if (HasExited) return;
        Stopped = true;
        KillTree(_process);
    }

    internal static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
