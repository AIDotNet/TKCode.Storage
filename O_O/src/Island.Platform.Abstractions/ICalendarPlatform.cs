namespace Island.Platform;

/// <summary>The native half of the <c>calendar.system</c> capability: the calendars the operating system itself keeps.</summary>
public interface ICalendarPlatform
{
    /// <summary>
    /// Calendars and events as one JSON document: <c>{ "calendars": [{ id, name, account?, color? }], "events": [{ id,
    /// calendarId, title, start, end, allDay, location?, url?, notes? }] }</c> for the range (all-day events as
    /// YYYY-MM-DD, end exclusive), or <c>{ "error": "denied" | "unavailable" | … }</c>. May block while the system asks
    /// the user for access; call off the UI thread.
    /// </summary>
    Task<string> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
