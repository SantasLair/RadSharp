namespace RadSharp;

/// <summary>Links a <see cref="Button"/> or <see cref="ToolButton"/>: caption, hint, enabled, visible, image index and (tool buttons) down.</summary>
internal sealed class ButtonActionLink(ActionItem action, Button button) : ActionLink(action)
{
    public override object Client => button;

    public override bool Supports(ActionMember m) => m switch
    {
        ActionMember.Shortcut => false,
        ActionMember.Checked => button is ToolButton,
        _ => true,
    };

    public override object? Get(ActionMember m) => m switch
    {
        ActionMember.Caption => button.Caption,
        ActionMember.Hint => button.Hint,
        ActionMember.Enabled => button.Enabled,
        ActionMember.Visible => button.Visible,
        ActionMember.ImageIndex => button.ImageIndex,
        ActionMember.Checked => (button as ToolButton)?.Down ?? false,
        _ => null,
    };

    public override void Set(ActionMember m, object? value)
    {
        switch (m)
        {
            case ActionMember.Caption: button.Caption = (string)value!; break;
            case ActionMember.Hint: button.Hint = (string)value!; break;
            case ActionMember.Enabled: button.Enabled = (bool)value!; break;
            case ActionMember.Visible: button.Visible = (bool)value!; break;
            case ActionMember.ImageIndex: button.ImageIndex = (int)value!; button.Invalidate(); break;
            case ActionMember.Checked when button is ToolButton tool: tool.Down = (bool)value!; break;
        }
    }

    public override bool IsDefault(ActionMember m)
    {
        if (m == ActionMember.Caption && button.Caption == button.Name) return true;   // Delphi treats Caption = Name as unset
        if (PublishedName(m) is not { } name) return true;
        var cls = ComponentRegistry.Of(button);
        return cls.FindProperty(name) is not { } p || p.HasDefaultValue(button, cls.Prototype);
    }

    static string? PublishedName(ActionMember m) => m switch
    {
        ActionMember.Caption => "caption",
        ActionMember.Hint => "hint",
        ActionMember.Enabled => "enabled",
        ActionMember.Visible => "visible",
        ActionMember.ImageIndex => "imageIndex",
        ActionMember.Checked => "down",
        _ => null,
    };

    /// <summary>The member a published property of a button stands for, or null if actions don't touch it.</summary>
    public static ActionMember? MemberOf(string publishedName) => publishedName switch
    {
        "caption" => ActionMember.Caption,
        "hint" => ActionMember.Hint,
        "enabled" => ActionMember.Enabled,
        "visible" => ActionMember.Visible,
        "imageIndex" => ActionMember.ImageIndex,
        "down" => ActionMember.Checked,
        _ => null,
    };
}

/// <summary>Links a <see cref="MenuItem"/>: caption, shortcut, enabled, checked and visible (menu items have no hint or picture).</summary>
internal sealed class MenuItemActionLink(ActionItem action, MenuItem item) : ActionLink(action)
{
    public override object Client => item;

    public override bool Supports(ActionMember m) => m is not (ActionMember.Hint or ActionMember.ImageIndex);

    public override object? Get(ActionMember m) => m switch
    {
        ActionMember.Caption => item.Caption,
        ActionMember.Shortcut => item.Shortcut,
        ActionMember.Enabled => item.Enabled,
        ActionMember.Checked => item.Checked,
        ActionMember.Visible => item.Visible,
        _ => null,
    };

    public override void Set(ActionMember m, object? value)
    {
        switch (m)
        {
            case ActionMember.Caption: item.Caption = (string)value!; break;
            case ActionMember.Shortcut: item.Shortcut = (string)value!; break;
            case ActionMember.Enabled: item.Enabled = (bool)value!; break;
            case ActionMember.Checked: item.Checked = (bool)value!; break;
            case ActionMember.Visible: item.Visible = (bool)value!; break;
        }
        item.InvalidateHost();   // a menu item can't repaint itself: the bar and an open popup show it live
    }

    public override bool IsDefault(ActionMember m) => m switch
    {
        ActionMember.Caption => item.Caption.Length == 0,
        ActionMember.Shortcut => item.Shortcut.Length == 0,
        ActionMember.Enabled => item.Enabled,
        ActionMember.Checked => !item.Checked,
        ActionMember.Visible => item.Visible,
        _ => true,
    };
}
