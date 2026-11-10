using Island.Core.State;

namespace Island.Core.Tests;

public class IslandStateMachineTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Expands_after_the_pointer_rests_and_collapses_after_it_leaves()
    {
        var sm = new IslandStateMachine(new HoverOptions());
        sm.SetRestingState(IslandState.Compact);

        sm.Update(true, Ms(0));
        sm.Update(true, Ms(200));
        Assert.Equal(IslandState.Compact, sm.State);
        sm.Update(true, Ms(260));
        Assert.Equal(IslandState.Expanded, sm.State);

        sm.Update(false, Ms(300));
        sm.Update(false, Ms(850));
        Assert.Equal(IslandState.Expanded, sm.State);
        sm.Update(false, Ms(910));
        Assert.Equal(IslandState.Compact, sm.State);
    }

    [Fact]
    public void A_brief_pass_over_the_island_does_not_expand_it()
    {
        var sm = new IslandStateMachine(new HoverOptions());
        sm.Update(true, Ms(0));
        sm.Update(false, Ms(100));
        sm.Update(true, Ms(150));
        sm.Update(true, Ms(350));
        Assert.Equal(IslandState.Idle, sm.State);
    }

    [Fact]
    public void Click_only_mode_ignores_hover_and_pin_holds_it_open()
    {
        var sm = new IslandStateMachine(new HoverOptions { ExpandDelay = null });
        sm.Update(true, Ms(0));
        sm.Update(true, Ms(5000));
        Assert.Equal(IslandState.Idle, sm.State);

        sm.Click();
        sm.Pin(true);
        sm.Update(false, Ms(6000));
        sm.Update(false, Ms(9000));
        Assert.Equal(IslandState.Expanded, sm.State);

        sm.Collapse();
        Assert.Equal(IslandState.Idle, sm.State);
    }

    [Fact]
    public void Raises_state_changed_once_per_change()
    {
        var sm = new IslandStateMachine(new HoverOptions());
        var seen = new List<IslandState>();
        sm.StateChanged += seen.Add;
        sm.SetRestingState(IslandState.Compact);
        sm.SetRestingState(IslandState.Compact);
        sm.Click();
        sm.Click();
        Assert.Equal([IslandState.Compact, IslandState.Expanded], seen);
    }
}
