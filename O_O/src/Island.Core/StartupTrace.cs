using System.Diagnostics;

namespace Island.Core;

/// <summary>Development aid: with <c>O_O_DEBUG=1</c>, prints milliseconds since process start at named points.</summary>
public static class StartupTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("O_O_DEBUG") == "1";
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void Mark(string point)
    {
        if (Enabled)
        {
            Console.Error.WriteLine($"[trace] {(DateTime.UtcNow - ProcessStart).TotalMilliseconds,7:0}ms {point}");
        }
    }
}
