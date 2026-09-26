using System.Text;
using System.Text.Json;
using RadSharp;
using RadSharp.Drawing;
using Rgb = RadSharp.Drawing.Color;

namespace TileEditor;

/// <summary>
/// The tile editor from GMForms, rebuilt on RADSharp: the layout, menus and toolbar come from
/// MainForm.radform.json (converted from the GameMaker version); this is the code-behind.
/// </summary>
public partial class MainForm : Form
{
    static readonly (string Name, Rgb Color)[] Tiles =
    [
        ("Empty", Rgb.FromHex("#FFFFFF")), ("Grass", Rgb.FromHex("#6DBE45")), ("Water", Rgb.FromHex("#3D8FD6")),
        ("Sand", Rgb.FromHex("#E8D38A")), ("Stone", Rgb.FromHex("#8E8E8E")), ("Lava", Rgb.FromHex("#E2572B")),
        ("Snow", Rgb.FromHex("#EEF3F8")),
    ];

    int[] _cells = [];
    int _columns, _rows;
    readonly Stack<int[]> _undo = new(), _redo = new();
    double _zoom = 1;
    bool _selectTool;
    bool _stroke;
    (int X, int Y)? _selStart, _selEnd;
    string? _fileName;

    public MainForm()
    {
        InitializeComponent();
        lstTiles.Items = [.. Tiles.Select(t => t.Name)];
        lstTiles.ItemIndex = 1;
        gridProps.Target = mapInfo;
        NewMap();
    }

    // ---- Map ----

    void NewMap()
    {
        _columns = Math.Clamp(mapInfo.Columns, 1, 512);
        _rows = Math.Clamp(mapInfo.Rows, 1, 512);
        _cells = new int[_columns * _rows];
        _undo.Clear();
        _redo.Clear();
        _selStart = _selEnd = null;
        _fileName = null;
        vpMap.Invalidate();
        Status("New map");
    }

    /// <summary>Applies Columns/Rows changed in the inspector, keeping existing tiles.</summary>
    void ResizeMap()
    {
        int c = Math.Clamp(mapInfo.Columns, 1, 512), r = Math.Clamp(mapInfo.Rows, 1, 512);
        if (c == _columns && r == _rows) return;
        Snapshot();
        var cells = new int[c * r];
        for (int y = 0; y < Math.Min(r, _rows); y++)
            for (int x = 0; x < Math.Min(c, _columns); x++)
                cells[y * c + x] = _cells[y * _columns + x];
        (_cells, _columns, _rows) = (cells, c, r);
    }

    void Snapshot()
    {
        _undo.Push((int[])_cells.Clone());
        _redo.Clear();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_cells);
        _cells = _undo.Pop();
        vpMap.Invalidate();
        Status("Undo");
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_cells);
        _cells = _redo.Pop();
        vpMap.Invalidate();
        Status("Redo");
    }

    void ClearMap()
    {
        Snapshot();
        if (_selStart is { } a && _selEnd is { } b)
        {
            for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
                for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
                    _cells[y * _columns + x] = 0;
        }
        else Array.Clear(_cells);
        vpMap.Invalidate();
        Status("Cleared");
    }

    void SetTool(bool select)
    {
        _selectTool = select;
        mniPaint.Checked = !select;
        mniSelect.Checked = select;
        btnPaint.Caption = select ? "Paint" : "[Paint]";
        btnSelect.Caption = select ? "[Select]" : "Select";
        if (!select) _selStart = _selEnd = null;
        vpMap.Invalidate();
    }

    void Zoom(double factor)
    {
        _zoom = factor == 0 ? 1 : Math.Clamp(_zoom * factor, 0.25, 4);
        vpMap.Invalidate();
        Status($"Zoom {_zoom * 100:0}%");
    }

    int CellSize => Math.Max(2, (int)Math.Round(mapInfo.TileSize * _zoom * Scale));

    (int X, int Y)? CellAt(int x, int y)
    {
        int cx = x / CellSize, cy = y / CellSize;
        return x >= 0 && y >= 0 && cx < _columns && cy < _rows ? (cx, cy) : null;
    }

    void Status(string text) =>
        lblStatus.Caption = $"{mapInfo.MapName} — {_columns}×{_rows} — {Tiles[Math.Max(0, lstTiles.ItemIndex)].Name} — {text}";

    // ---- Files (a small JSON format, written without reflection so it works with NativeAOT) ----

    void SaveMap(string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("name", mapInfo.MapName);
        w.WriteNumber("columns", _columns);
        w.WriteNumber("rows", _rows);
        w.WriteStartArray("cells");
        foreach (var c in _cells) w.WriteNumberValue(c);
        w.WriteEndArray();
        w.WriteEndObject();
        _fileName = path;
        Status("Saved " + Path.GetFileName(path));
    }

    void OpenMap(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        mapInfo.MapName = root.GetProperty("name").GetString() ?? "Untitled";
        mapInfo.Columns = root.GetProperty("columns").GetInt32();
        mapInfo.Rows = root.GetProperty("rows").GetInt32();
        _columns = mapInfo.Columns;
        _rows = mapInfo.Rows;
        _cells = [.. root.GetProperty("cells").EnumerateArray().Select(e => Math.Clamp(e.GetInt32(), 0, Tiles.Length - 1))];
        Array.Resize(ref _cells, _columns * _rows);
        _undo.Clear();
        _redo.Clear();
        _fileName = path;
        gridProps.RefreshValues();
        vpMap.Invalidate();
        Status("Opened " + Path.GetFileName(path));
    }

    void DoOpen()
    {
        if (!dlgOpen.Execute()) return;
        try { OpenMap(dlgOpen.FileName); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Dialogs.MessageDlg($"Could not open the map:\n{e.Message}", MessageType.Error, owner: this);
        }
    }

    void DoSave()
    {
        if (_fileName == null)
        {
            dlgSave.FileName = mapInfo.MapName + ".tilemap";
            if (!dlgSave.Execute()) return;
            _fileName = dlgSave.FileName;
        }
        SaveMap(_fileName);
    }

    // ---- Map view ----

    void vpMap_Draw(Control sender, Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, vpMap.Width, vpMap.Height), t.PanelBackground);
        int s = CellSize;
        canvas.FillRect(new Rect(0, 0, _columns * s, _rows * s), mapInfo.Background);
        for (int y = 0; y < _rows; y++)
        {
            if (y * s > vpMap.Height) break;
            for (int x = 0; x < _columns; x++)
            {
                if (x * s > vpMap.Width) break;
                int tile = _cells[y * _columns + x];
                if (tile > 0) canvas.FillRect(new Rect(x * s, y * s, s, s), Tiles[tile].Color);
            }
        }
        if (mapInfo.ShowGrid && s >= 6)
        {
            var line = new Rgb(0, 0, 0, 28);
            for (int x = 0; x <= _columns; x++) canvas.FillRect(new Rect(x * s, 0, 1, _rows * s), line);
            for (int y = 0; y <= _rows; y++) canvas.FillRect(new Rect(0, y * s, _columns * s, 1), line);
        }
        if (_selStart is { } a && _selEnd is { } b)
        {
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            var sel = new Rect(x0 * s, y0 * s, (Math.Abs(a.X - b.X) + 1) * s, (Math.Abs(a.Y - b.Y) + 1) * s);
            canvas.FillRect(sel, new Rgb(t.Accent.R, t.Accent.G, t.Accent.B, 40));
            canvas.DrawBorder(sel, t.Accent, Math.Max(1, canvas.Px(2)));
        }
    }

    void vpMap_MouseDown(Control sender, MouseEventArgs e)
    {
        if (CellAt(e.X, e.Y) is not { } cell) return;
        if (_selectTool)
        {
            _selStart = _selEnd = cell;
            _stroke = true;
            vpMap.Invalidate();
            return;
        }
        Snapshot();
        _stroke = true;
        PaintCell(cell, e.Button == MouseButton.Right ? 0 : Math.Max(0, lstTiles.ItemIndex));
    }

    void vpMap_MouseMove(Control sender, MouseEventArgs e)
    {
        var cell = CellAt(e.X, e.Y);
        if (cell is { } c) Status($"cell {c.X},{c.Y}");
        if (!_stroke || cell is not { } target) return;
        if (_selectTool) { _selEnd = target; vpMap.Invalidate(); }
        else PaintCell(target, e.Button == MouseButton.Right ? 0 : Math.Max(0, lstTiles.ItemIndex));
    }

    void vpMap_MouseUp(Control sender, MouseEventArgs e) => _stroke = false;

    void vpMap_Wheel(Control sender, MouseWheelEventArgs e)
    {
        Zoom(e.DeltaY > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    void PaintCell((int X, int Y) cell, int tile)
    {
        ref int slot = ref _cells[cell.Y * _columns + cell.X];
        if (slot == tile) return;
        slot = tile;
        vpMap.Invalidate();
    }

    // ---- Palette and inspector ----

    void lstTiles_Change(Control sender) => Status("tile selected");

    void lstTiles_DrawItem(Control sender, Canvas canvas, int index, Rect rect, bool selected)
    {
        var t = canvas.Theme;
        if (selected) canvas.FillRect(rect, t.Selection);
        int s = rect.Height - canvas.Px(8);
        var swatch = new Rect(rect.X + canvas.Px(6), rect.Y + canvas.Px(4), s, s);
        canvas.FillRoundRect(swatch, canvas.Px(3), Tiles[index].Color);
        canvas.DrawRoundBorder(swatch, canvas.Px(3), t.InputBorder, canvas.Px(1));
        canvas.DrawText(Tiles[index].Name, canvas.DefaultFont, t.Text,
            new Rect(swatch.X + s + canvas.Px(8), rect.Y, rect.Width - s - canvas.Px(20), rect.Height), TextAlign.Left, VerticalAlign.Center);
    }

    void gridProps_Change(Control sender)
    {
        ResizeMap();
        vpMap.Invalidate();
        Status("properties changed");
    }

    // ---- Menus and toolbar (Delphi-style: buttons and menu items call the same code) ----

    void mnuNew_Click(MenuItem sender) => NewMap();
    void mnuOpen_Click(MenuItem sender) => DoOpen();
    void mnuSave_Click(MenuItem sender) => DoSave();
    void mnuExit_Click(MenuItem sender) => Close();
    void mnuUndo_Click(MenuItem sender) => Undo();
    void mnuRedo_Click(MenuItem sender) => Redo();
    void mnuClear_Click(MenuItem sender) => ClearMap();
    void mnuPaint_Click(MenuItem sender) => SetTool(select: false);
    void mnuSelect_Click(MenuItem sender) => SetTool(select: true);
    void mnuZoomIn_Click(MenuItem sender) => Zoom(1.25);
    void mnuZoomOut_Click(MenuItem sender) => Zoom(0.8);
    void mnuResetView_Click(MenuItem sender) => Zoom(0);
    void mnuMapProps_Click(MenuItem sender) => gridProps.Focus();

    void mnuShowGrid_Click(MenuItem sender)
    {
        mapInfo.ShowGrid = !mapInfo.ShowGrid;
        mniShowGrid.Checked = mapInfo.ShowGrid;
        gridProps.RefreshValues();
        vpMap.Invalidate();
    }

    void mnuDesigner_Click(MenuItem sender) => OtherDemo("The form designer arrives in milestone M7.");
    void mnuGallery_Click(MenuItem sender) => OtherDemo("See samples/Hello and the golden images in the tests.");
    void mnuM1Demo_Click(MenuItem sender) => OtherDemo("The layout rules are covered by spec/vectors.");

    void OtherDemo(string text) => Dialogs.ShowMessage(text, this);

    void btnNew_Click(Control sender) => NewMap();
    void btnOpen_Click(Control sender) => DoOpen();
    void btnSave_Click(Control sender) => DoSave();
    void btnUndo_Click(Control sender) => Undo();
    void btnRedo_Click(Control sender) => Redo();
    void btnPaint_Click(Control sender) => SetTool(select: false);
    void btnSelect_Click(Control sender) => SetTool(select: true);
    void btnClear_Click(Control sender) => ClearMap();
    void btnMapProps_Click(Control sender) => gridProps.Focus();

    /// <summary>Paints a little demo landscape (for screenshots).</summary>
    public void PaintDemo()
    {
        var rng = new Random(7);
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _columns; x++)
            {
                double d = Math.Sqrt((x - 12) * (x - 12) + (y - 10) * (y - 10) * 1.4);
                _cells[y * _columns + x] = d < 4 ? 2 : d < 5.5 ? 3 : y > 18 ? 4 : rng.NextDouble() < 0.08 ? 4 : 1;
            }
        for (int x = 20; x < 27; x++) _cells[5 * _columns + x] = 5;
        vpMap.Invalidate();
    }
}
