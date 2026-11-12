using System;
using System.Windows.Input;
using Xunit;

namespace RadSharp.VisualStudio.Tests
{
    public class KeyRoutingTests
    {
        private const ModifierKeys None = ModifierKeys.None;
        private const ModifierKeys Ctrl = ModifierKeys.Control;
        private const ModifierKeys Shift = ModifierKeys.Shift;
        private const ModifierKeys Alt = ModifierKeys.Alt;

        // Keys the designer surface needs (nudge, tab order, in-place edit, typing) go to the host in both contexts.
        [Theory]
        [InlineData(Key.Left, None)]
        [InlineData(Key.Right, Shift)]
        [InlineData(Key.Up, Ctrl)]
        [InlineData(Key.Down, Ctrl | Shift)]
        [InlineData(Key.Tab, None)]
        [InlineData(Key.Tab, Shift)]
        [InlineData(Key.Escape, None)]
        [InlineData(Key.Enter, None)]
        [InlineData(Key.Space, None)]
        [InlineData(Key.Back, None)]
        [InlineData(Key.Home, None)]
        [InlineData(Key.A, None)]
        [InlineData(Key.Z, Shift)]
        [InlineData(Key.D1, None)]
        [InlineData(Key.F2, None)]
        [InlineData(Key.B, Ctrl)]
        [InlineData(Key.D, Ctrl)]
        public void Surface_keys_go_to_the_host(Key key, ModifierKeys mods)
        {
            Assert.Equal(KeyRoute.Host, KeyRouting.Classify(key, mods, KeyContext.Designer));
            Assert.Equal(KeyRoute.Host, KeyRouting.Classify(key, mods, KeyContext.ToolWindow));
        }

        // VS keeps its global shortcuts everywhere.
        [Theory]
        [InlineData(Key.S, Ctrl)]
        [InlineData(Key.S, Ctrl | Shift)]
        [InlineData(Key.Tab, Ctrl)]
        [InlineData(Key.Tab, Ctrl | Shift)]
        [InlineData(Key.F4, Ctrl)]
        [InlineData(Key.F4, None)]
        [InlineData(Key.F4, Alt)]
        [InlineData(Key.F5, None)]
        [InlineData(Key.F5, Ctrl)]
        [InlineData(Key.F5, Shift)]
        [InlineData(Key.F7, None)]
        [InlineData(Key.F9, None)]
        [InlineData(Key.F10, None)]
        [InlineData(Key.F11, None)]
        [InlineData(Key.F12, None)]
        [InlineData(Key.Q, Ctrl)]
        [InlineData(Key.F, Alt)]
        [InlineData(Key.E, Alt)]
        [InlineData(Key.LeftAlt, None)]
        [InlineData(Key.B, Ctrl | Shift)]
        [InlineData(Key.F, Ctrl | Shift)]
        [InlineData(Key.OemComma, Ctrl)]
        [InlineData(Key.OemMinus, Ctrl)]
        [InlineData(Key.W, Ctrl)]
        [InlineData(Key.K, Ctrl)]
        public void Global_shortcuts_stay_with_vs(Key key, ModifierKeys mods)
        {
            Assert.True(KeyRouting.IsGlobalShortcut(key, mods));
            Assert.Equal(KeyRoute.VisualStudio, KeyRouting.Classify(key, mods, KeyContext.Designer));
            Assert.Equal(KeyRoute.VisualStudio, KeyRouting.Classify(key, mods, KeyContext.ToolWindow));
        }

        // Edit shortcuts: designer commands in the document tab, text editing in the tool windows.
        [Theory]
        [InlineData(Key.Z, Ctrl)]
        [InlineData(Key.Y, Ctrl)]
        [InlineData(Key.Z, Ctrl | Shift)]
        [InlineData(Key.X, Ctrl)]
        [InlineData(Key.C, Ctrl)]
        [InlineData(Key.V, Ctrl)]
        [InlineData(Key.A, Ctrl)]
        [InlineData(Key.Delete, None)]
        [InlineData(Key.Insert, Ctrl)]
        [InlineData(Key.Insert, Shift)]
        [InlineData(Key.Delete, Shift)]
        public void Edit_keys_depend_on_the_pane(Key key, ModifierKeys mods)
        {
            Assert.True(KeyRouting.IsEditCommandKey(key, mods));
            Assert.False(KeyRouting.IsGlobalShortcut(key, mods));
            Assert.Equal(KeyRoute.VisualStudio, KeyRouting.Classify(key, mods, KeyContext.Designer));
            Assert.Equal(KeyRoute.Host, KeyRouting.Classify(key, mods, KeyContext.ToolWindow));
        }

        [Fact]
        public void Delete_with_ctrl_is_not_the_edit_command()
        {
            Assert.False(KeyRouting.IsEditCommandKey(Key.Delete, Ctrl));
            Assert.Equal(KeyRoute.Host, KeyRouting.Classify(Key.Delete, Ctrl, KeyContext.Designer));
        }

        [Fact]
        public void Messages_other_than_key_down_are_never_claimed()
        {
            var vkLeft = new IntPtr(KeyInterop.VirtualKeyFromKey(Key.Left));
            Assert.True(KeyMessages.ShouldClaim(KeyMessages.WM_KEYDOWN, vkLeft, None, KeyContext.Designer));
            Assert.False(KeyMessages.ShouldClaim(KeyMessages.WM_KEYUP, vkLeft, None, KeyContext.Designer));
            Assert.False(KeyMessages.ShouldClaim(KeyMessages.WM_CHAR, new IntPtr('a'), None, KeyContext.Designer));
        }

        [Theory]
        [InlineData(Key.LeftCtrl)]
        [InlineData(Key.LeftShift)]
        [InlineData(Key.RightCtrl)]
        [InlineData(Key.LWin)]
        public void Modifier_keys_alone_are_never_claimed(Key key)
        {
            var vk = new IntPtr(KeyInterop.VirtualKeyFromKey(key));
            Assert.False(KeyMessages.ShouldClaim(KeyMessages.WM_KEYDOWN, vk, None, KeyContext.ToolWindow));
        }

        [Fact]
        public void Syskeydown_counts_as_alt()
        {
            // Alt+Left arrives as WM_SYSKEYDOWN; Alt combinations belong to VS.
            var vkLeft = new IntPtr(KeyInterop.VirtualKeyFromKey(Key.Left));
            Assert.False(KeyMessages.ShouldClaim(KeyMessages.WM_SYSKEYDOWN, vkLeft, None, KeyContext.Designer));
        }

        [Fact]
        public void Ctrl_c_is_claimed_only_in_tool_windows()
        {
            var vkC = new IntPtr(KeyInterop.VirtualKeyFromKey(Key.C));
            Assert.False(KeyMessages.ShouldClaim(KeyMessages.WM_KEYDOWN, vkC, Ctrl, KeyContext.Designer));
            Assert.True(KeyMessages.ShouldClaim(KeyMessages.WM_KEYDOWN, vkC, Ctrl, KeyContext.ToolWindow));
        }
    }
}
