using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace RadSharp.DesignHost.Harness;

/// <summary>Command line: <c>&lt;form.radform.json&gt; [--host &lt;RadSharpDesignHost.exe&gt;] [--screenshot &lt;out.png&gt; [--delay-ms N]]</c>.</summary>
internal static class Program
{
    internal sealed class Options
    {
        public string FormPath { get; set; } = "";
        public string? HostPath { get; set; }
        public string? ScreenshotPath { get; set; }
        public int DelayMs { get; set; } = 500;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Options? options;
        try { options = Parse(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            options = null;
        }
        if (options == null)
        {
            Console.Error.WriteLine("Usage: RadSharpDesignHostHarness <form.radform.json> [--host <RadSharpDesignHost.exe>] [--screenshot <out.png> [--delay-ms N]]");
            Console.Error.WriteLine("Default host: %RADSHARP_DESIGNHOST%, else src\\RadSharp.DesignHost\\bin\\Debug\\net10.0\\RadSharpDesignHost.exe under the repository root.");
            return 64;
        }
        options.HostPath ??= DefaultHostPath();
        Log("form: " + options.FormPath);
        Log("host: " + (options.HostPath ?? "(not found)"));

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(options);
        return app.Run(window);
    }

    internal static void Log(string line) => Console.Error.WriteLine("[harness " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + line);

    private static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value after " + a + ".");
            switch (a)
            {
                case "--host": o.HostPath = Path.GetFullPath(Next()); break;
                case "--screenshot": o.ScreenshotPath = Path.GetFullPath(Next()); break;
                case "--delay-ms":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) || ms < 0)
                        throw new ArgumentException("--delay-ms needs a non-negative number.");
                    o.DelayMs = ms;
                    break;
                case "-h":
                case "--help":
                case "/?":
                    return null;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown option " + a + ".");
                    if (o.FormPath.Length != 0) throw new ArgumentException("Only one form file can be opened.");
                    o.FormPath = Path.GetFullPath(a);
                    break;
            }
        }
        return o.FormPath.Length == 0 ? null : o;
    }

    /// <summary>%RADSHARP_DESIGNHOST%, else the Debug build of the host under the repository root (found by walking up to
    /// RadSharp.slnx from this exe, then from the current directory).</summary>
    private static string? DefaultHostPath()
    {
        var env = Environment.GetEnvironmentVariable("RADSHARP_DESIGNHOST");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        foreach (var start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RadSharp.slnx")))
                    return Path.Combine(dir.FullName, "src", "RadSharp.DesignHost", "bin", "Debug", "net10.0", "RadSharpDesignHost.exe");
            }
        }
        return null;
    }
}
