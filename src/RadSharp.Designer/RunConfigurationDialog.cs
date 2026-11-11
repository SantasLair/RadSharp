using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>Project › Run Configuration: command-line arguments, working directory and environment variables of Ctrl+F5.</summary>
public sealed class RunConfigurationDialog : Form
{
    readonly Edit _args, _dir;
    readonly Memo _env;
    readonly RunConfiguration _initial;

    public RunConfigurationDialog(RunConfiguration config, RadProject project, Form? owner)
    {
        _initial = config;
        DialogLayout.Setup(this, $"Run Configuration: {project.Name}", 560, 340, owner);
        DialogLayout.Put(this, new Label { Caption = "Arguments" }, 16, 20, 130, 24);
        _args = DialogLayout.Put(this, new Edit { Name = "edtArguments", Text = config.Arguments }, 150, 18, 390, 26);
        DialogLayout.Put(this, new Label { Caption = "Working directory" }, 16, 58, 130, 24);
        _dir = DialogLayout.Put(this, new Edit { Name = "edtWorkingDirectory", Text = config.WorkingDirectory, Placeholder = "empty: the project folder" }, 150, 56, 390, 26);
        DialogLayout.Put(this, new Label { Caption = "Environment variables" }, 16, 96, 130, 24);
        _env = DialogLayout.Put(this, new Memo { Name = "memEnvironment", WordWrap = false }, 150, 94, 390, 120);
        _env.Text = LaunchSettings.FormatEnvironment(config.Environment);
        DialogLayout.Put(this, new Label
        {
            Caption = "One NAME=value per line. Saved in Properties/launchSettings.json.",
            FontColor = Drawing.Color.FromHex("#6B7280"),
        }, 150, 220, 390, 24);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 356, 290, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 454, 290, 86, 30);
        PerformLayout();
        Shown += _ => _args.Focus();
    }

    /// <summary>The configuration as edited.</summary>
    public RunConfiguration Result => new()
    {
        Arguments = _args.Text.Trim(),
        WorkingDirectory = _dir.Text.Trim(),
        Environment = LaunchSettings.ParseEnvironment(_env.Text),
    };
}
