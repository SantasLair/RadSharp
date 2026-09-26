using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Shared behaviour of the native file dialogs: <see cref="Execute"/> blocks until the user chooses.</summary>
public abstract class FileDialog : Component
{
    [Published(Category = "Behavior")] public string Title { get; set; } = "";
    /// <summary>Delphi-style filter, e.g. <c>"Maps|*.map|All files|*.*"</c>.</summary>
    [Published(Category = "Behavior")] public string Filter { get; set; } = "";
    [Published("initialDir", Category = "Behavior")] public string InitialDirectory { get; set; } = "";
    [Published(Category = "Behavior")] public string FileName { get; set; } = "";

    /// <summary>All chosen paths (several when multi-select is allowed).</summary>
    public string[] Files { get; private set; } = [];

    protected abstract FileDialogKind Kind { get; }
    protected virtual bool AllowMultiple => false;

    /// <summary>Shows the dialog; returns true (and sets <see cref="FileName"/>/<see cref="Files"/>) if the user chose.</summary>
    public bool Execute()
    {
        var host = PlatformHost.Require();
        string[]? result = null;
        bool done = false;
        host.ShowFileDialog(Kind, OwningForm, new FileDialogOptions
        {
            Title = Title,
            Filter = Filter,
            InitialDirectory = InitialDirectory,
            FileName = FileName,
            AllowMultiple = AllowMultiple,
        }, r => { result = r; done = true; });
        host.ProcessEventsUntil(() => done);
        if (result is not { Length: > 0 }) return false;
        Files = result;
        FileName = result[0];
        return true;
    }
}

public class OpenDialog : FileDialog
{
    [Published(Category = "Behavior")] public bool MultiSelect { get; set; }
    protected override FileDialogKind Kind => FileDialogKind.Open;
    protected override bool AllowMultiple => MultiSelect;
}

public class SaveDialog : FileDialog
{
    protected override FileDialogKind Kind => FileDialogKind.Save;
}

public class FolderDialog : FileDialog
{
    protected override FileDialogKind Kind => FileDialogKind.Folder;
}

public enum MessageType { Information, Warning, Error, Confirmation }

[Flags]
public enum MessageButtons { Ok = 1, Cancel = 2, Yes = 4, No = 8, Abort = 16, Retry = 32, Ignore = 64, OkCancel = Ok | Cancel, YesNo = Yes | No, YesNoCancel = Yes | No | Cancel }

/// <summary>Delphi-style message boxes, drawn by RADSharp (so they look the same everywhere).</summary>
public static class Dialogs
{
    public static void ShowMessage(string message, Form? owner = null) =>
        MessageDlg(message, MessageType.Information, MessageButtons.Ok, owner);

    /// <summary>Shows a modal message box and returns the button pressed (Cancel if closed another way).</summary>
    public static ModalResult MessageDlg(string message, MessageType type = MessageType.Information,
        MessageButtons buttons = MessageButtons.Ok, Form? owner = null) =>
        CreateMessageForm(message, type, buttons, owner).ShowModal();

    /// <summary>Builds (but doesn't show) the message form; exposed for tests and customization.</summary>
    public static Form CreateMessageForm(string message, MessageType type, MessageButtons buttons, Form? owner = null)
    {
        double scale = Form.DefaultScale;
        int S(int v) => LayoutMath.Round(v * scale);
        var theme = owner?.Theme ?? Theme.Default;
        var font = new Font(theme.FontFace, (float)(theme.FontSize * scale));

        var lines = message.Replace("\r\n", "\n").Split('\n');
        int textWidth = (int)Math.Ceiling(lines.Max(l => font.MeasureWidth(l)));
        int lineHeight = (int)Math.Ceiling(font.LineHeight) + S(2);

        var order = new[] { MessageButtons.Yes, MessageButtons.No, MessageButtons.Ok, MessageButtons.Abort, MessageButtons.Retry, MessageButtons.Ignore, MessageButtons.Cancel };
        var shown = order.Where(b => buttons.HasFlag(b)).ToList();
        if (shown.Count == 0) shown.Add(MessageButtons.Ok);
        int buttonW = S(84), buttonH = S(28), gap = S(8), margin = S(20), icon = S(32);

        int contentW = Math.Max(icon + S(16) + textWidth, shown.Count * (buttonW + gap) - gap);
        int width = Math.Min(S(640), contentW + 2 * margin);
        int textH = Math.Max(icon, lines.Length * lineHeight);
        int height = margin + textH + S(20) + buttonH + margin;

        var form = new Form
        {
            Caption = type switch
            {
                MessageType.Warning => "Warning",
                MessageType.Error => "Error",
                MessageType.Confirmation => "Confirm",
                _ => "Information",
            },
            Owner = owner,
            Theme = theme,
        };
        form.SetBounds(0, 0, width, height);
        form.Scale = scale;
        form.DesignWidth = width;
        form.DesignHeight = height;

        var badge = new MessageIcon(type);
        badge.SetBounds(margin, margin, icon, icon);
        form.Add(badge);
        for (int i = 0; i < lines.Length; i++)
        {
            var label = new Label { Caption = lines[i], Name = $"lblLine{i}" };
            label.SetBounds(margin + icon + S(16), margin + i * lineHeight + (lines.Length == 1 ? (icon - lineHeight) / 2 : 0), textWidth + S(4), lineHeight);
            form.Add(label);
        }

        int x = width - margin - shown.Count * (buttonW + gap) + gap;
        foreach (var b in shown)
        {
            var result = b switch
            {
                MessageButtons.Ok => ModalResult.Ok, MessageButtons.Cancel => ModalResult.Cancel,
                MessageButtons.Yes => ModalResult.Yes, MessageButtons.No => ModalResult.No,
                MessageButtons.Abort => ModalResult.Abort, MessageButtons.Retry => ModalResult.Retry,
                _ => ModalResult.Ignore,
            };
            var button = new Button
            {
                Name = "btn" + b,
                Caption = b.ToString(),
                ModalResult = result,
                Default = b == shown[0],
                Cancel = b is MessageButtons.Cancel or MessageButtons.No && !shown.Contains(MessageButtons.Cancel) || b == MessageButtons.Cancel,
                Anchors = Anchors.Right | Anchors.Bottom,
            };
            button.SetBounds(x, height - margin - buttonH, buttonW, buttonH);
            form.Add(button);
            x += buttonW + gap;
        }
        form.PerformLayout();
        form.Shown += f => form.FindControl("btn" + shown[0])?.Focus();
        return form;
    }

    sealed class MessageIcon(MessageType type) : Control
    {
        protected override void Paint(Canvas canvas)
        {
            var (color, glyph) = type switch
            {
                MessageType.Warning => (Color.FromHex("#E8A317"), "!"),
                MessageType.Error => (Color.FromHex("#D93F3F"), "×"),
                MessageType.Confirmation => (canvas.Theme.Accent, "?"),
                _ => (canvas.Theme.Accent, "i"),
            };
            var r = new Rect(0, 0, Width, Height);
            canvas.FillEllipse(r, color);
            canvas.DrawText(glyph, canvas.DefaultBoldFont.WithSize(Height * 0.6f), Color.White, r, TextAlign.Center, VerticalAlign.Center);
        }
    }
}
