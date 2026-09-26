using RadSharp.Platform.Sdl;
using TileEditor;

Application.Initialize();
var form = new MainForm();
if (Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT") != null) form.PaintDemo();
Application.Run(form);
