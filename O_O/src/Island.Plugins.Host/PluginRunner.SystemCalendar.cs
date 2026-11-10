using System.Text.Json;
using Island.Platform;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host;

/// <summary>The <c>calendar.system</c> capability: events of the calendars the operating system keeps.</summary>
internal sealed partial class PluginRunner
{
    private static Func<CancellationToken, Task<string>> SystemCalendarRead(ICalendarPlatform calendar, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        DateTimeOffset Time(string name) =>
            doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? t
                : throw new PluginCapabilityException($"calendar.system.read needs '{name}' as an ISO date.");
        var from = Time("from");
        var to = Time("to");
        if (to <= from || to - from > TimeSpan.FromDays(400))
        {
            throw new PluginCapabilityException("calendar.system.read: the range must be positive and at most 400 days.");
        }
        return ct => calendar.ReadAsync(from, to, ct);
    }
}
