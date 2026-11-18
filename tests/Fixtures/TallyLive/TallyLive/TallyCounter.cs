using RadSharp;

namespace Tally;

/// <summary>A control the application defines itself: unknown to the designer until the project compiles.</summary>
public class TallyCounter : Label
{
    int _count;

    [Published(Category = "Behavior")]
    public int Count
    {
        get => _count;
        set { _count = value; Caption = $"{value} item(s)"; }
    }
}
