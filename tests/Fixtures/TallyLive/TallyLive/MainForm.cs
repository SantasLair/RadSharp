using Microsoft.Data.Sqlite;
using RadSharp;
using Tally.Lib;

namespace Tally;

public partial class MainForm : Form
{
    public MainForm() => InitializeComponent();

    void btnTally_Click(Control sender)
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE items (amount INTEGER); INSERT INTO items VALUES (1), (2), (3);";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT SUM(amount) FROM items";
        var total = Convert.ToInt32(command.ExecuteScalar());

        cntTotal.Count = total;
        var settings = new TallySettings(Money.Describe(total), total);
#if TALLY_LIVE
        // JsonSerializer comes from the project's <Using Include="System.Text.Json" />.
        lblResult.Caption = JsonSerializer.Serialize(settings, TallyJson.Default.TallySettings);
#endif
    }
}
