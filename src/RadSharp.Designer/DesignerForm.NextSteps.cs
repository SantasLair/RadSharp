namespace RadSharp.Designer;

/// <summary>Error paths that end in an action rather than a dead end (I9): what to do when dotnet is missing or a run does not compile.</summary>
public partial class DesignerForm
{
    const string NL = "\n";
    const string DotnetDownloadUrl = "https://dotnet.microsoft.com/download";

    /// <summary>Build, Run and Publish need the dotnet CLI: say so, offer the download page, and tell how to retry.</summary>
    void ReportMissingDotnet(string detail)
    {
        Output($"Could not run dotnet: {detail}");
        sbMain.SimpleText = "Install the .NET 10 SDK to build and run apps";
        NextStep("The .NET SDK is missing", $"Building needs the .NET 10 SDK, but dotnet could not be started.\n{detail}\nInstall it, restart RADSharp, then build again (Ctrl+Shift+B).",
            ("Open the download page", () => { if (PlatformHost.Current?.OpenUrl(DotnetDownloadUrl) != true) Output($"Download the SDK from {DotnetDownloadUrl}"); }));
    }
}
