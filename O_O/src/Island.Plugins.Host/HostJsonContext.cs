using System.Text.Json.Serialization;

namespace Island.Plugins.Host;

/// <summary>Source-generated serialisers for the few plain values the host writes (AOT safe).</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class HostJsonContext : JsonSerializerContext
{
}
