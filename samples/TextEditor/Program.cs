using RadSharp.Platform.Sdl;
using TextEditor;

Application.Initialize();
var form = new MainForm();
if (Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT") != null) form.LoadDemoText();
Application.Run(form);
