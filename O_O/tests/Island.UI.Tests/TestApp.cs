using Avalonia;
using Avalonia.Headless;
using TKCode.Arc;
using TKCode.Arc.Themes.Corners;

namespace Island.UI.Tests;

public sealed class TestApp : Application
{
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .UseArc(arc => arc.UseArcControls().UseArcSpecial().UseArcBlocks().UseArcCorners());
}

/// <summary>One headless Avalonia session shared by the tests; the UI thread work runs inside <see cref="Run"/>.</summary>
public static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static void Run(Action action) => Session.Value.Dispatch(() => { action(); return 0; }, CancellationToken.None).GetAwaiter().GetResult();
}
