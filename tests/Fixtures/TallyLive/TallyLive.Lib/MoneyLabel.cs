using RadSharp;

namespace Tally.Lib;

/// <summary>A control from a library the project references: a palette entry once the project compiles.</summary>
public class MoneyLabel : Label
{
    [Published(Category = "Appearance")] public string Currency { get; set; } = "EUR";
}
