using System.Text.Json.Serialization;

namespace Tally;

public sealed record TallySettings(string Label, int Total);

/// <summary>Only compiles if System.Text.Json's source generator ran (it implements the context).</summary>
[JsonSerializable(typeof(TallySettings))]
internal sealed partial class TallyJson : JsonSerializerContext
{
}
