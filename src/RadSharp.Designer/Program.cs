using RadSharp.Platform.Sdl;
using RadSharp.Designer;

Application.Initialize();
// The designer opens a project given on the command line; otherwise it shows the welcome page.
var designer = new DesignerForm(args.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SELECT") is { Length: > 0 } select
    && designer.Document?.Form.FindControl(select) is { } control)
    designer.Document.Select(control);
Application.Run(designer);
