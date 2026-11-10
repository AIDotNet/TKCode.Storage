using Island.Core.Activities;

namespace Island.Core.Tests;

public sealed class ActivityArbiterTests
{
    private static ActivityEntry A(string key, IslandPriority p, long updated = 0, string? kind = null) => new(key, kind ?? key, p, updated);

    private static readonly ActivityPreferences None = new();

    [Fact]
    public void Nothing_running_is_empty()
    {
        Assert.True(ActivityArbiter.Arrange([], None).IsEmpty);
    }

    [Fact]
    public void Two_at_most_are_shown_by_priority_then_recency()
    {
        var r = ActivityArbiter.Arrange(
        [
            A("music", IslandPriority.Normal, updated: 1),
            A("unread", IslandPriority.Passive, updated: 9),
            A("meeting", IslandPriority.High, updated: 2),
            A("focus", IslandPriority.Normal, updated: 5),
        ], None);
        Assert.Equal("meeting", r.Primary!.Key);
        Assert.Equal("focus", r.Secondary!.Key);                    // same level as music, updated later
        Assert.Equal(["music", "unread"], r.Queued.Select(a => a.Key));
    }

    [Fact]
    public void The_users_order_comes_before_recency_within_a_level()
    {
        var prefs = new ActivityPreferences { Order = ["music"] };
        var r = ActivityArbiter.Arrange([A("music", IslandPriority.Normal, 1), A("focus", IslandPriority.Normal, 5)], prefs);
        Assert.Equal("music", r.Primary!.Key);
    }

    [Fact]
    public void Pinned_goes_first_and_policies_leave_activities_out()
    {
        var prefs = new ActivityPreferences
        {
            Pinned = "unread",
            Policies = new Dictionary<string, ActivityPolicy> { ["meeting"] = ActivityPolicy.AlertOnly, ["focus"] = ActivityPolicy.Off },
        };
        var r = ActivityArbiter.Arrange(
            [A("meeting", IslandPriority.High), A("focus", IslandPriority.Normal), A("unread", IslandPriority.Passive), A("music", IslandPriority.Normal)], prefs);
        Assert.Equal("unread", r.Primary!.Key);
        Assert.Equal("music", r.Secondary!.Key);
        Assert.Empty(r.Queued);
        Assert.True(prefs.MayAlert("meeting"));
        Assert.False(prefs.MayAlert("focus"));
    }

    [Fact]
    public void Instances_share_their_kinds_preferences()
    {
        var prefs = new ActivityPreferences { Policies = new Dictionary<string, ActivityPolicy> { ["com.o_o.ai-usage/account"] = ActivityPolicy.Off } };
        var r = ActivityArbiter.Arrange(
            [A("com.o_o.ai-usage/account#a", IslandPriority.High, kind: "com.o_o.ai-usage/account"),
             A("com.o_o.ai-usage/account#b", IslandPriority.High, kind: "com.o_o.ai-usage/account")], prefs);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void The_front_takes_the_compact_place_unless_something_critical_runs()
    {
        var running = new[] { A("music", IslandPriority.Normal), A("unread", IslandPriority.Passive) };
        Assert.Equal("unread", ActivityArbiter.Arrange(running, None, front: "unread").Primary!.Key);
        Assert.Equal("music", ActivityArbiter.Arrange(running, None, front: "unread").Secondary!.Key);

        var call = running.Append(A("call", IslandPriority.Critical)).ToArray();
        Assert.Equal("call", ActivityArbiter.Arrange(call, None, front: "unread").Primary!.Key);
        Assert.Equal("music", ActivityArbiter.Arrange(call, None, front: "missing").Secondary!.Key);
    }

    [Fact]
    public void Set_aside_activities_go_to_the_end()
    {
        var r = ActivityArbiter.Arrange(
            [A("meeting", IslandPriority.High), A("music", IslandPriority.Normal), A("unread", IslandPriority.Passive)], None,
            setAside: new HashSet<string> { "meeting" });
        Assert.Equal(["music", "unread", "meeting"], r.All.Select(a => a.Key));
    }

    [Fact]
    public void Preferences_are_saved_and_read_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-activities-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ActivityPreferencesStore(path);
            store.Set(new ActivityPreferences { Order = ["b", "a"], Pinned = "a" }.WithPolicy("c", ActivityPolicy.AlertOnly).WithPolicy("d", ActivityPolicy.Off));
            var again = new ActivityPreferencesStore(path).Current;
            Assert.Equal(["b", "a"], again.Order);
            Assert.Equal("a", again.Pinned);
            Assert.Equal(ActivityPolicy.AlertOnly, again.PolicyOf("c"));
            Assert.Equal(ActivityPolicy.Off, again.PolicyOf("d"));
            Assert.Equal(ActivityPolicy.Allow, again.PolicyOf("e"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class AlertQueueTests
{
    private static readonly TimeSpan S = TimeSpan.FromSeconds(1);

    private static IslandAlert Alert(string key, IslandPriority p = IslandPriority.Normal, int seconds = 3) =>
        new(key, key, p, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Alerts_show_one_at_a_time_in_priority_then_arrival_order()
    {
        var q = new AlertQueue();
        q.Enqueue(Alert("a"), TimeSpan.Zero);
        q.Enqueue(Alert("b"), TimeSpan.Zero);
        q.Enqueue(Alert("c", IslandPriority.High), TimeSpan.Zero);
        Assert.Equal("a", q.Current!.Key);
        q.Advance(2 * S);
        Assert.Equal("a", q.Current!.Key);
        q.Advance(3 * S);
        Assert.Equal("c", q.Current!.Key);                      // higher priority before the earlier "b"
        q.Advance(6 * S);
        Assert.Equal("b", q.Current!.Key);
        q.Advance(9 * S);
        Assert.Null(q.Current);
    }

    [Fact]
    public void A_critical_alert_interrupts_and_the_interrupted_one_shows_again()
    {
        var q = new AlertQueue();
        var seen = new List<string?>();
        q.CurrentChanged += a => seen.Add(a?.Key);
        q.Enqueue(Alert("msg"), TimeSpan.Zero);
        q.Enqueue(Alert("call", IslandPriority.Critical), S);
        Assert.Equal("call", q.Current!.Key);
        q.Advance(4 * S);
        Assert.Equal("msg", q.Current!.Key);
        q.Advance(7 * S);
        Assert.Equal(["msg", "call", "msg", null], seen);
    }

    [Fact]
    public void The_same_key_replaces_and_restarts_and_removing_moves_on()
    {
        var q = new AlertQueue();
        q.Enqueue(Alert("msg"), TimeSpan.Zero);
        q.Enqueue(Alert("other"), TimeSpan.Zero);
        q.Enqueue(Alert("msg"), 2 * S);                          // a second message: shown for 3 s from now
        q.Advance(4 * S);
        Assert.Equal("msg", q.Current!.Key);
        q.Remove("msg", 4 * S);
        Assert.Equal("other", q.Current!.Key);
        Assert.Equal(AlertQueue.MaxDuration, Enqueued(new IslandAlert("x", "x", IslandPriority.Normal, TimeSpan.FromMinutes(5))));
    }

    private static TimeSpan Enqueued(IslandAlert alert)
    {
        var q = new AlertQueue();
        q.Enqueue(alert, TimeSpan.Zero);
        return q.Current!.Duration;
    }
}

public sealed class IslandPresentationTests
{
    private static readonly ActivityEntry Music = new("music", "music", IslandPriority.Normal, 0);
    private static readonly ActivityEntry Unread = new("unread", "unread", IslandPriority.Passive, 0);
    private static readonly IslandAlert Msg = new("msg", "im", IslandPriority.Normal, TimeSpan.FromSeconds(3));

    [Fact]
    public void Resting_is_idle_without_activities_and_compact_with_them()
    {
        var idle = IslandPresentation.Decide(ActivityArrangement.Empty, null, IslandIntent.None, hasAmbient: true);
        Assert.Equal(PresentationKind.Idle, idle.Kind);
        Assert.True(idle.ShowAmbient);

        var two = IslandPresentation.Decide(new ActivityArrangement(Music, Unread, []), null, IslandIntent.None, hasAmbient: true);
        Assert.Equal(PresentationKind.Compact, two.Kind);
        Assert.Equal(Music, two.Activity);
        Assert.Equal(Unread, two.Bubble);
        Assert.False(two.ShowAmbient);                          // ambient items make way
    }

    [Fact]
    public void A_click_opens_the_activity_or_home_and_an_alert_shows_over_the_resting_state()
    {
        var one = new ActivityArrangement(Music, null, []);
        Assert.Equal(PresentationKind.Expanded, IslandPresentation.Decide(one, null, IslandIntent.Open, false).Kind);
        Assert.Equal(PresentationKind.Home, IslandPresentation.Decide(ActivityArrangement.Empty, null, IslandIntent.Open, false).Kind);
        Assert.Equal(PresentationKind.Home, IslandPresentation.Decide(one, null, IslandIntent.Home, false).Kind);

        Assert.Equal(PresentationKind.Alert, IslandPresentation.Decide(one, Msg, IslandIntent.None, false).Kind);
        Assert.Equal(PresentationKind.Expanded, IslandPresentation.Decide(one, Msg, IslandIntent.Open, false).Kind); // what the user opened stays
    }
}
