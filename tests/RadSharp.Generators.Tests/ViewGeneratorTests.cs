namespace RadSharp.Generators.Tests;

/// <summary>M14 items 3–4: a form with ListView/TreeView events, columns and nodes compiles against its generated code.</summary>
public partial class GeneratorTests
{
    [Fact]
    public void List_and_tree_view_events_columns_and_nodes_bind_at_compile_time()
    {
        const string json = """
            {
              "format": "radform", "version": "1.0",
              "root": { "class": "Form", "name": "frmViews", "children": [
                { "class": "ListView", "name": "lvFiles", "onGetItem": "lvFiles_GetItem", "onCompare": "lvFiles_Compare",
                  "onItemChecked": "lvFiles_ItemChecked", "onColumnClick": "lvFiles_ColumnClick",
                  "columns": [ { "title": "Name" } ] },
                { "class": "TreeView", "name": "tvAssets", "onExpanding": "tvAssets_Expanding", "onCollapsing": "tvAssets_Expanding",
                  "onEditing": "tvAssets_Edit", "onEdited": "tvAssets_Edit", "onNodeChecked": "tvAssets_NodeChecked",
                  "nodes": [ { "text": "Maps", "nodes": [ { "text": "level1.map" } ] } ] }
              ] }
            }
            """;
        const string cs = """
            using RadSharp;
            namespace App;
            public partial class Views : Form
            {
                public Views() => InitializeComponent();
                void lvFiles_GetItem(Control sender, ListViewItemEventArgs e) => e.Item.Caption = e.Index.ToString();
                void lvFiles_Compare(Control sender, ListViewCompareEventArgs e) => e.Result = 0;
                void lvFiles_ItemChecked(Control sender, int index, bool isChecked) { }
                void lvFiles_ColumnClick(Control sender, int column) { }
                void tvAssets_Expanding(Control sender, TreeNodeCancelEventArgs e) => e.Cancel = e.Node.Level > 3;
                void tvAssets_Edit(Control sender, TreeEditEventArgs e) => e.Accept = e.Text.Length > 0;
                void tvAssets_NodeChecked(Control sender, TreeNode node) { }
            }
            """;
        var (gen, compile, code) = Run(cs, ("C:/app/Views.radform.json", json));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("lvFiles.GetItem += lvFiles_GetItem;", code);
        Assert.Contains("tvAssets.Expanding += tvAssets_Expanding;", code);
        Assert.Contains("tvAssets.NodeChecked += tvAssets_NodeChecked;", code);
    }
}
