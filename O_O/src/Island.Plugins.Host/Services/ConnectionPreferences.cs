using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// Host-level connection settings the user edits in the console (never plugins): for now the command that runs
/// Claude Code. Kept in <c>&lt;data&gt;/connections.json</c>.
/// </summary>
public sealed class ConnectionPreferences(string dataDirectory)
{
    private string Path_ => Path.Combine(dataDirectory, "connections.json");

    /// <summary>The user's Claude Code command, or null for the default.</summary>
    public string? ClaudeCommand
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(Path_));
                return doc.RootElement.TryGetProperty("claudeCommand", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    w.WriteString("claudeCommand", value.Trim());
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(Path_ + ".tmp", buffer.ToArray());
            File.Move(Path_ + ".tmp", Path_, overwrite: true);
        }
    }
}
