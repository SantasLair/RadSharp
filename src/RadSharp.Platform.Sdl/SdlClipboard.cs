using RadSharp.Text;
using static SDL.SDL3;

namespace RadSharp.Platform.Sdl;

/// <summary>The system clipboard, through SDL.</summary>
internal sealed class SdlClipboard : IClipboard
{
    public string? GetText() => SDL_HasClipboardText() ? SDL_GetClipboardText() : null;

    public void SetText(string text) => SDL_SetClipboardText(text);
}
