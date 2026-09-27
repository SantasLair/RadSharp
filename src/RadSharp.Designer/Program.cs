using RadSharp.Platform.Sdl;
using RadSharp.Designer;

SdlHost.Initialize();
// The designer opens a project given on the command line; otherwise it shows the welcome page.
// Test hook: RADSHARP_DESIGNER_NEW=<folder> creates a new application there (as File > New Application does) and opens it.
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_NEW") is { Length: > 0 } newFolder)
    args = [RadSharp.Design.Code.RadProject.Create(newFolder, Path.GetFileName(newFolder), packageSource: RadSharp.Design.Code.RadSharpPackages.FindLocalSource()).ProjectFile];
var designer = new DesignerForm(args.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
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
SdlHost.Run(designer);
