using RadSharp;
using RadSharp.Platform.Sdl;

Application.Initialize();

using var stream = typeof(Program).Assembly.GetManifestResourceStream("MainForm.radform.json")!;
var json = new StreamReader(stream).ReadToEnd();
var (form, warnings) = FormReader.Load(json, new FormLoadOptions { Scale = Application.Scale });
foreach (var w in warnings) Console.Error.WriteLine(w);

Application.Run(form);
