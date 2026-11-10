using System.Text.RegularExpressions;

namespace Island.Platform.Linux;

/// <summary>One <c>org.freedesktop.Notifications.Notify</c> call as <c>dbus-monitor</c> prints it.</summary>
internal sealed record DbusNotify(string AppName, string AppIcon, string Summary, string Body, string? DesktopEntry);

/// <summary>
/// Reads the text <c>dbus-monitor</c> prints for <c>Notify</c> method calls, line by line. The arguments are, in order:
/// app_name, replaces_id (uint32), app_icon, summary, body, actions (array), hints (array of dict entries), timeout (int32).
/// A call is complete at its <c>int32</c> line, so a notification is reported as soon as it is posted.
/// </summary>
internal sealed partial class DbusNotifyParser
{
    private bool _inCall;
    private int _depth;
    private readonly List<string> _strings = [];
    private string? _desktopEntry;
    private bool _wantEntryValue;
    private System.Text.StringBuilder? _open; // a top-level string that continues on the next line

    /// <summary>Feeds one output line; returns a notification when this line completes one.</summary>
    public DbusNotify? Feed(string line)
    {
        if (_open is not null)
        {
            // Inside a string that spans lines: its continuation lines start at column 0, like a header would.
            _open.Append('\n').Append(line);
            if (line.EndsWith('"'))
            {
                _strings.Add(_open.ToString()[..^1]);
                _open = null;
            }
            return null;
        }
        if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
        {
            // A new message header.
            Reset();
            _inCall = line.StartsWith("method call", StringComparison.Ordinal) &&
                      line.Contains("interface=org.freedesktop.Notifications", StringComparison.Ordinal) &&
                      line.Contains("member=Notify", StringComparison.Ordinal);
            return null;
        }
        if (!_inCall)
        {
            return null;
        }
        var t = line.Trim();
        if (_depth == 0)
        {
            if (t.StartsWith("string \"", StringComparison.Ordinal))
            {
                var text = t["string \"".Length..];
                if (text.EndsWith('"') && text.Length > 0)
                {
                    _strings.Add(text[..^1]);
                }
                else
                {
                    _open = new System.Text.StringBuilder(text);
                }
            }
            else if (t.StartsWith("array [", StringComparison.Ordinal) && !t.EndsWith(']'))
            {
                _depth++;
            }
            else if (t.StartsWith("int32", StringComparison.Ordinal))
            {
                var result = _strings.Count >= 4
                    ? new DbusNotify(_strings[0], _strings[1], _strings[2], _strings.Count > 3 ? _strings[3] : "", _desktopEntry)
                    : null;
                Reset();
                return result;
            }
            return null;
        }
        // Inside an array (actions or hints).
        if (_wantEntryValue)
        {
            var m = VariantString().Match(t);
            if (m.Success)
            {
                _desktopEntry = m.Groups[1].Value;
            }
            _wantEntryValue = false;
        }
        else if (t == "string \"desktop-entry\"")
        {
            _wantEntryValue = true;
        }
        if (t.StartsWith("dict entry(", StringComparison.Ordinal) || (t.StartsWith("array [", StringComparison.Ordinal) && !t.EndsWith(']')))
        {
            _depth++;
        }
        else if (t is "]" or ")")
        {
            _depth--;
        }
        return null;
    }

    private void Reset()
    {
        _inCall = false;
        _depth = 0;
        _strings.Clear();
        _desktopEntry = null;
        _wantEntryValue = false;
        _open = null;
    }

    [GeneratedRegex("^variant\\s+string \"(.*)\"$")]
    private static partial Regex VariantString();
}
