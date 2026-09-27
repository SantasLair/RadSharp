using RadSharp.Platform.Sdl;
using TileEditor;

SdlHost.Initialize();
var form = new MainForm();
if (Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT") != null) form.PaintDemo();
SdlHost.Run(form);
