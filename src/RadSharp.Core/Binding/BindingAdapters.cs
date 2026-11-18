using System.Globalization;

namespace RadSharp;

/// <summary>Reads, writes and clears one kind of control's value for <see cref="Binder{T}"/>, and tells it when the user changed it.</summary>
internal abstract class ControlAdapter<TRaw>
{
    public abstract IReadOnlyList<Control> Controls { get; }
    public abstract TRaw Read();
    public abstract void Write(TRaw raw);
    /// <summary>What the control shows while the binder has no object: empty text, unchecked, the minimum, no selection.</summary>
    public abstract void Clear();
    public abstract void Attach(NotifyEvent handler);
    public abstract void Detach(NotifyEvent handler);
}

internal sealed class TextAdapter(EditBase edit) : ControlAdapter<string>
{
    public override IReadOnlyList<Control> Controls { get; } = [edit];
    public override string Read() => edit.Text;
    public override void Write(string raw) => edit.Text = raw;
    public override void Clear() => edit.Text = "";
    public override void Attach(NotifyEvent handler) => edit.Change += handler;
    public override void Detach(NotifyEvent handler) => edit.Change -= handler;
}

internal sealed class ToggleAdapter(ToggleControl box) : ControlAdapter<bool>
{
    public override IReadOnlyList<Control> Controls { get; } = [box];
    public override bool Read() => box.Checked;
    public override void Write(bool raw) => box.Checked = raw;
    public override void Clear() => box.Checked = false;
    public override void Attach(NotifyEvent handler) => box.Change += handler;
    public override void Detach(NotifyEvent handler) => box.Change -= handler;
}

/// <summary>A radio group as one value: the index of the checked button, -1 for none.</summary>
internal sealed class RadioAdapter(RadioButton[] buttons) : ControlAdapter<int>
{
    public override IReadOnlyList<Control> Controls { get; } = buttons;
    public override int Read() => Array.FindIndex(buttons, b => b.Checked);

    public override void Write(int raw)
    {
        // Uncheck the others first (they may not share a parent), then check the one.
        for (int i = 0; i < buttons.Length; i++) if (i != raw) buttons[i].Checked = false;
        if (raw >= 0 && raw < buttons.Length) buttons[raw].Checked = true;
    }

    public override void Clear() => Write(-1);
    public override void Attach(NotifyEvent handler) { foreach (var b in buttons) b.Change += handler; }
    public override void Detach(NotifyEvent handler) { foreach (var b in buttons) b.Change -= handler; }
}

/// <summary>A spin edit by its text, so a number typed but not yet confirmed (Enter, leaving the box) still counts.</summary>
internal sealed class SpinAdapter(SpinEdit spin) : ControlAdapter<string>
{
    public override IReadOnlyList<Control> Controls { get; } = [spin];
    public override string Read() => spin.Text;

    public override void Write(string raw)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) spin.Value = v;
        else spin.Text = raw;
    }

    public override void Clear() => spin.Text = "";
    public override void Attach(NotifyEvent handler) { spin.Change += handler; spin.ValueChanged += handler; }
    public override void Detach(NotifyEvent handler) { spin.Change -= handler; spin.ValueChanged -= handler; }

    public static bool TryParse(SpinEdit spin, string raw, out int value, out string? error)
    {
        error = null;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = "Enter a whole number.";
            return false;
        }
        bool limited = spin.MinValue != 0 || spin.MaxValue != 0;
        if (limited && (value < spin.MinValue || value > Math.Max(spin.MinValue, spin.MaxValue)))
        {
            error = $"Enter a number from {spin.MinValue} to {Math.Max(spin.MinValue, spin.MaxValue)}.";
            return false;
        }
        return true;
    }
}

internal sealed class TrackAdapter(TrackBar bar) : ControlAdapter<int>
{
    public override IReadOnlyList<Control> Controls { get; } = [bar];
    public override int Read() => bar.Position;
    public override void Write(int raw) => bar.Position = raw;
    public override void Clear() => bar.Position = Math.Min(bar.Min, bar.Max);
    public override void Attach(NotifyEvent handler) => bar.Change += handler;
    public override void Detach(NotifyEvent handler) => bar.Change -= handler;
}

/// <summary>A combo box by its text: the edit's text when editable, else the chosen item's (a value that is no item selects none).</summary>
internal sealed class ComboTextAdapter(ComboBox box) : ControlAdapter<string>
{
    public override IReadOnlyList<Control> Controls { get; } = [box];
    public override string Read() => box.Text;

    public override void Write(string raw)
    {
        if (box.Style == ComboBoxStyle.DropDown) box.Text = raw;
        else box.ItemIndex = Array.IndexOf(box.Items, raw);
    }

    public override void Clear()
    {
        box.ItemIndex = -1;
        if (box.Style == ComboBoxStyle.DropDown) box.Text = "";
    }

    public override void Attach(NotifyEvent handler) => box.Change += handler;
    public override void Detach(NotifyEvent handler) => box.Change -= handler;
}

internal sealed class ComboIndexAdapter(ComboBox box) : ControlAdapter<int>
{
    public override IReadOnlyList<Control> Controls { get; } = [box];
    public override int Read() => box.ItemIndex;
    public override void Write(int raw) => box.ItemIndex = raw;

    public override void Clear()
    {
        box.ItemIndex = -1;
        if (box.Style == ComboBoxStyle.DropDown) box.Text = "";
    }

    public override void Attach(NotifyEvent handler) => box.Change += handler;
    public override void Detach(NotifyEvent handler) => box.Change -= handler;
}

/// <summary>A date picker by <see cref="DateTimePicker.Value"/>: the date, or null when its check box is shown and unchecked.</summary>
internal sealed class DateAdapter(DateTimePicker picker) : ControlAdapter<DateOnly?>
{
    public override IReadOnlyList<Control> Controls { get; } = [picker];
    public override DateOnly? Read() => picker.Value;
    public override void Write(DateOnly? raw) => picker.Value = raw;

    public override void Clear()
    {
        if (picker.ShowCheckbox) picker.Value = null;
        else picker.SelectedDate = null;
    }

    public override void Attach(NotifyEvent handler) => picker.Change += handler;
    public override void Detach(NotifyEvent handler) => picker.Change -= handler;
}
