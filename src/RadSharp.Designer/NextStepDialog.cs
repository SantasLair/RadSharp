using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// An error message that says what to do next: the text, and one button per possible action (<c>btnAction0</c>…) beside
/// Close (<c>btnClose</c>). <see cref="Chosen"/> is the index of the action picked, or -1.
/// </summary>
public sealed class NextStepDialog : Form
{
    public int Chosen { get; private set; } = -1;

    public NextStepDialog(string title, string message, IReadOnlyList<string> actions, Form? owner)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        int buttonW = 176;
        int width = Math.Max(480, 16 + (actions.Count + 1) * (buttonW + 8) + 8);
        int height = 28 + lines.Length * 22 + 24 + 34 + 20;
        DialogLayout.Setup(this, title, width, height, owner);
        for (int i = 0; i < lines.Length; i++)
            DialogLayout.Put(this, new Label { Name = $"lblLine{i}", Caption = lines[i] }, 16, 16 + i * 22, width - 32, 22);
        int x = width - 16 - (actions.Count + 1) * (buttonW + 8) + 8;
        int y = height - 20 - 34;
        for (int i = 0; i < actions.Count; i++)
        {
            int index = i;
            var button = DialogLayout.Put(this, new Button { Name = $"btnAction{i}", Caption = actions[i], Default = i == 0 }, x, y, buttonW, 34);
            button.Click += _ => { Chosen = index; ModalResult = ModalResult.Ok; };
            x += buttonW + 8;
        }
        DialogLayout.Put(this, new Button { Name = "btnClose", Caption = "Close", Cancel = true, ModalResult = ModalResult.Cancel }, x, y, buttonW, 34);
        PerformLayout();
    }
}

public partial class DesignerForm
{
    /// <summary>
    /// Shows an error with the actions that lead on from it; runs the one the user picks and returns its index (-1 for Close).
    /// Every error that would otherwise stop at "OK" goes through here (open the file, retry, open the settings).
    /// </summary>
    int NextStep(string title, string message, params (string Label, Action Do)[] actions)
    {
        var dialog = new NextStepDialog(title, message, [.. actions.Select(a => a.Label)], this);
        dialog.ShowModal();
        if (dialog.Chosen is >= 0 and var i && i < actions.Length) actions[i].Do();
        return dialog.Chosen;
    }
}
