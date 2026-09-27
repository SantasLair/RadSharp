using RadSharp;

namespace Robustness;

public partial class RobustnessForm : Form
{
    public RobustnessForm()
    {
        Caption = "M11 Robustness Demo";
        Width = 400;
        Height = 300;

        var lblUrl = new Label { Caption = "URL:", Left = 10, Top = 10, Width = 380, Height = 20 };
        var edtUrl = new Edit { Text = "https://api.github.com", Left = 10, Top = 30, Width = 380, Height = 28 };

        var btnFetch = new Button { Caption = "Fetch (async/await)", Left = 10, Top = 70, Width = 120, Height = 28 };
        var lblResult = new Label { Caption = "Result: (waiting)", Left = 10, Top = 110, Width = 380, Height = 40, WordWrap = true };

        var btnThrow = new Button { Caption = "Throw Exception", Left = 140, Top = 70, Width = 120, Height = 28 };
        var btnAsyncModal = new Button { Caption = "Async Modal", Left = 270, Top = 70, Width = 110, Height = 28 };

        var btnAsyncFile = new Button { Caption = "Open File (async)", Left = 10, Top = 160, Width = 120, Height = 28 };
        var lblFile = new Label { Caption = "File: (none)", Left = 10, Top = 200, Width = 380, Height = 40, WordWrap = true };

        Add(lblUrl);
        Add(edtUrl);
        Add(btnFetch);
        Add(lblResult);
        Add(btnThrow);
        Add(btnAsyncModal);
        Add(btnAsyncFile);
        Add(lblFile);

        btnFetch.Click += async (s) =>
        {
            try
            {
                lblResult.Caption = "Fetching...";
                using var client = new HttpClient();
                var response = await client.GetAsync(edtUrl.Text);
                lblResult.Caption = $"Result: {response.StatusCode}";
            }
            catch (Exception ex)
            {
                lblResult.Caption = $"Error: {ex.Message}";
            }
        };

        btnThrow.Click += (s) =>
        {
            throw new InvalidOperationException("This is a test exception from the Click handler");
        };

        btnAsyncModal.Click += async (s) =>
        {
            var result = await Dialogs.MessageDlgAsync("This is an async modal dialog", MessageType.Information, MessageButtons.OkCancel, this);
            lblResult.Caption = $"Modal result: {result}";
        };

        btnAsyncFile.Click += async (s) =>
        {
            var dialog = new OpenDialog { Title = "Open a file" };
            if (await dialog.ExecuteAsync())
            {
                lblFile.Caption = $"File: {dialog.FileName}";
            }
        };
    }
}
