using RadSharp;
using RadSharp.Platform.Sdl;
using WindowsDemo;

SdlHost.Initialize();   // first: the monitors and the scale come from SDL
var form = new MainForm();
// Reopen where the window was last time (or centred on the primary monitor), and save its place when it closes.
// The first argument, if any, is another file to keep it in.
var path = args.Length > 0 ? Path.GetFullPath(args[0]) : WindowPlacement.DefaultPath("RadSharp WindowsDemo");
WindowPlacement.Remember(form, path);
if (form.StartPosition == FormStartPosition.Default) form.StartPosition = FormStartPosition.CenterScreen;
form.PlacementPath = path;
SdlHost.Run(form);
