using RadSharp;
using RadSharp.Platform.Sdl;

Application.Initialize();

using var stream = typeof(Program).Assembly.GetManifestResourceStream("MainForm.radform.json")!;
var json = new StreamReader(stream).ReadToEnd();
var (form, warnings) = FormReader.Load(json, new FormLoadOptions { Scale = Application.Scale });
foreach (var w in warnings) Console.Error.WriteLine(w);

// Hand-wired until the form source generator exists (roadmap M4).
var status = (Label)form.FindControl("lblStatus")!;
int clicks = 0;
form.FindControl("btnNew")!.Click += _ => status.Caption = $"New clicked {++clicks} time(s)";
form.FindControl("btnCancel")!.Click += _ => status.Caption = "Cancel clicked";

Application.Run(form);
