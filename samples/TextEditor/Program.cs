using RadSharp.Platform.Sdl;
using TextEditor;

SdlHost.Initialize();
var form = new MainForm();
if (Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT") != null) form.LoadDemoText();
SdlHost.Run(form);
