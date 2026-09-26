using RadSharp.Platform.Sdl;
using RadSharp.Designer;

Application.Initialize();
var designer = new DesignerForm(args.FirstOrDefault());
if (Environment.GetEnvironmentVariable("RADSHARP_DESIGNER_SELECT") is { Length: > 0 } select
    && designer.Document.Form.FindControl(select) is { } control)
    designer.Document.Select(control);
Application.Run(designer);
