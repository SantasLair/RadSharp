using RadSharp;
using RadSharp.Drawing;

namespace TileEditor;

/// <summary>
/// The map's settings as a non-visual component. Its [Published] properties get generated metadata,
/// so the form file can store it and the PropertyGrid (the inspector) can edit it.
/// </summary>
public class MapInfo : Component
{
    [Published(Category = "Map")] public string MapName { get; set; } = "Untitled";
    [Published(Category = "Map")] public int Columns { get; set; } = 32;
    [Published(Category = "Map")] public int Rows { get; set; } = 24;
    [Published(Category = "View")] public int TileSize { get; set; } = 24;
    [Published(Category = "View")] public bool ShowGrid { get; set; } = true;
    [Published(Category = "View")] public Color Background { get; set; } = Color.FromHex("#FFFFFF");
}
