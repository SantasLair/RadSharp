using RadSharp.Platform.Sdl;
using RadSharp.Designer;

SdlHost.Initialize();
// The designer opens a project given on the command line; otherwise it shows the welcome page.
// Test hook: RADSHARP_DESIGNER_NEW=<folder> creates a new application there (as File > New Application does) and opens it.
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_NEW") is { Length: > 0 } newFolder)
    args = [RadSharp.Design.Code.RadProject.Create(newFolder, Path.GetFileName(newFolder), packageSource: RadSharp.Design.Code.RadSharpPackages.FindLocalSource()).ProjectFile];
// Test hook: RADSHARP_DESIGNER_SETTINGS=<file> keeps settings, keymap and session in another folder than the user's.
var settings = Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SETTINGS") is { Length: > 0 } s ? s : null;
var designer = new DesignerForm(args.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), settings);
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SELECT") is { Length: > 0 } select
    && designer.Document?.Form.FindControl(select) is { } control)
    designer.Document.Select(control);
// Test hook: press F9 once the designer is up.
switch (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_RUN"))
{
    case "1": designer.Shown += _ => SdlHost.PostToUiThread(() => designer.Run()); break;
    // Clicks the Run tool button through the same input path as the mouse.
    case "click": designer.Shown += _ => SdlHost.PostToUiThread(() => designer.Simulate.Click(designer.FindControl("btnRun")!)); break;
}
// Test hook: RADSHARP_DESIGNER_OPEN=<file> opens a code file; with RADSHARP_DESIGNER_SNAPSHOT=<png> the window is saved
// (rendered as on screen) once that file has its semantic colours, and the designer exits.
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_OPEN") is { Length: > 0 } open)
    designer.Shown += _ => SdlHost.PostToUiThread(() =>
    {
        var code = designer.OpenCodeFile(open);
        if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SNAPSHOT") is not { Length: > 0 } png || code == null) return;
        var started = DateTime.UtcNow;
        void Check()
        {
            var lang = designer.LanguageHostFor(code.Path)?.Find(code);
            if (lang is { HasSemanticColours: true } || DateTime.UtcNow - started > TimeSpan.FromSeconds(90))
            {
                using var bitmap = RadSharp.Drawing.FormRenderer.RenderToBitmap(designer);
                File.WriteAllBytes(png, RadSharp.Drawing.FormRenderer.EncodePng(bitmap));
                Console.WriteLine(lang is { HasSemanticColours: true } ? "snapshot: semantic colours" : "snapshot: timed out without semantic colours");
                designer.Close();
                return;
            }
            Task.Delay(200).ContinueWith(t => SdlHost.PostToUiThread(Check));
        }
        Check();
    });
// Test hook: RADSHARP_DESIGNER_SNAPSHOT=<png> without RADSHARP_DESIGNER_OPEN saves the window as it first appears (the welcome
// page when no project is given), prints the time since the process started, and exits (the cold-start budget, measured for real).
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_OPEN") is not { Length: > 0 }
    && Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SNAPSHOT") is { Length: > 0 } welcomePng)
    designer.Shown += _ => SdlHost.PostToUiThread(() =>
    {
        using var bitmap = RadSharp.Drawing.FormRenderer.RenderToBitmap(designer);
        var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        File.WriteAllBytes(welcomePng, RadSharp.Drawing.FormRenderer.EncodePng(bitmap));
        Console.WriteLine($"snapshot: first paint {elapsed.TotalMilliseconds:F0} ms after process start");
        designer.Close();
    });
SdlHost.Run(designer);
