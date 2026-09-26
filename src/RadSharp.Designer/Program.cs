using RadSharp.Platform.Sdl;
using RadSharp.Designer;

Application.Initialize();
// The designer opens a project given on the command line; otherwise it shows the welcome page.
var designer = new DesignerForm(args.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SELECT") is { Length: > 0 } select
    && designer.Document?.Form.FindControl(select) is { } control)
    designer.Document.Select(control);
// Test hook: press F9 once the designer is up.
switch (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_RUN"))
{
    case "1": designer.Shown += _ => Application.Post(() => designer.Run()); break;
    // Clicks the Run tool button through the same input path as the mouse.
    case "click": designer.Shown += _ => Application.Post(() => designer.Simulate.Click(designer.FindControl("btnRun")!)); break;
}
Application.Run(designer);
