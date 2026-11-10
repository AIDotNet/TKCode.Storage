namespace Island.Plugins.Host.Tests;

/// <summary>The shipped AI usage plugins (plugins/com.o_o.ai-*), end to end, without touching the network.</summary>
public sealed partial class PluginHostTests
{
    /// <summary>The repository's O_O folder (where plugins/ and catalog/plugins/ live).</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "plugins", "com.o_o.ai-usage")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("plugins/ not found above the test output directory.");
    }

    /// <summary>A first-party plugin's source: built in (plugins/) or from the O_O plugin catalog (catalog/plugins/).</summary>
    private static string RepoPlugin(string id)
    {
        foreach (var folder in new[] { "plugins", Path.Combine("catalog", "plugins") })
        {
            var candidate = Path.Combine(RepoRoot(), folder, id);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException($"{id} is neither in plugins/ nor in catalog/plugins/.");
    }

    private static void CopyPlugin(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static IEnumerable<VNode> Nodes(VNode? node)
    {
        if (node is null)
        {
            yield break;
        }
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var n in Nodes(child))
            {
                yield return n;
            }
        }
    }

    /// <summary>The aggregator's latest view: handler ids change on every render, so events go to this one.</summary>
    private ActivitySnapshot LatestUsage()
    {
        WaitForPage(s => s.PluginId == "com.o_o.ai-usage", "usage page");
        Thread.Sleep(150); // let a render already in flight land, so its handlers are the current ones
        return _pageViews.Last(s => s.PluginId == "com.o_o.ai-usage");
    }

    private static string? Shown(ActivitySnapshot s, string type) =>
        Nodes(s.View.Expanded).FirstOrDefault(n => n.Type == type)?.GetString(type == "swap" ? "id" : "value");

    /// <summary>
    /// Steps through the providers with the ‹ › arrows, as a click would, until <paramref name="providerId"/> is shown
    /// and <paramref name="ready"/>; returns its view. The provider tabs are not shown, so the arrows are the way.
    /// </summary>
    private ActivitySnapshot PickProvider(string providerId, Func<ActivitySnapshot, bool> ready)
    {
        for (var step = 0; step < 12; step++)
        {
            var view = LatestUsage();
            if (Shown(view, "swap") == providerId && ready(view))
            {
                return view;
            }
            var from = Shown(view, "swap");
            var next = Nodes(view.View.Expanded).Where(n => n.Type == "iconButton").Last();
            _host!.Dispatch("com.o_o.ai-usage", next.Handlers["onClick"]);
            WaitForPage(s => s.PluginId == "com.o_o.ai-usage" && Shown(s, "swap") != from, $"step from {from}");
        }
        throw new TimeoutException($"never reached provider {providerId}");
    }

    /// <summary>Every text and progress label in a view, in order.</summary>
    private static IEnumerable<string> Texts(VNode? node)
    {
        if (node is null)
        {
            yield break;
        }
        if (node.GetString("text") is { } text)
        {
            yield return text;
        }
        if (node.Type is "progress" or "gauge")
        {
            yield return $"[{node.GetNumber("value")}%] {node.GetString("label")} · {node.GetString("detail")}";
        }
        foreach (var child in node.Children)
        {
            foreach (var t in Texts(child))
            {
                yield return t;
            }
        }
    }

    [Fact]
    public void The_aggregator_shows_providers_errors_and_providers_waiting_for_consent()
    {
        foreach (var id in new[] { "com.o_o.ai-usage", "com.o_o.ai.codex", "com.o_o.ai.claude" })
        {
            CopyPlugin(RepoPlugin(id), Path.Combine(_root, id));
        }
        WritePluginWithManifest("test.fakeai",
            """, "contributes": { "providers": [{ "interface": "ai-usage", "id": "fake", "title": "Fake" }] }""", "", """
            import { island } from '@island/api';
            export function activate() {
              island.provide('ai-usage', 'fake', {
                accounts: () => [{ handle: 'h', label: 'me@example.com', plan: 'Pro' }],
                usage: () => ({ windows: [{ name: '5 小时', usedPercent: 62.4, resetsAt: new Date(Date.now() + 2 * 3600e3 + 60e3).toISOString() }] }),
              });
            }
            """);
        StartWith(null, Services with { LocalAuthSources = [new FakeSource(DateTimeOffset.UtcNow.AddHours(1))] });

        // Codex: the user allows its local login; Claude: left undecided.
        WaitFor(() => _host!.GetState("com.o_o.ai.codex") == PluginState.AwaitingConsent ? "ok" : null, "codex consent");
        _host!.SetGranted("com.o_o.ai.codex", ["localAuth:codex-cli"]);

        // One provider at a time, switched with the arrows (no tab row); each shows its own state when reached.
        WaitForPage(s => s.PluginId == "com.o_o.ai-usage" && Shown(s, "swap") != null, "provider shown");

        var fake = Texts(PickProvider("fake", s => Texts(s.View.Expanded).Contains("me@example.com")).View.Expanded).ToList();
        Assert.Contains("me@example.com", fake);
        Assert.Contains(fake, t => t.StartsWith("[62%] 5 小时 · 2 小时后重置"));

        // The real Codex provider tried chatgpt.com; the (fake) login may only go to 127.0.0.1, so it was refused
        // before any connection was made.
        var codex = Texts(PickProvider("codex", s => Texts(s.View.Expanded).Contains("fake@example.com")).View.Expanded).ToList();
        Assert.Contains(codex, t => t.Contains("只能发送到 127.0.0.1，不能发送到 'chatgpt.com'"));

        var claude = Texts(PickProvider("claude", s => Texts(s.View.Expanded).Any(t => t.Contains("还没有获得授权"))).View.Expanded).ToList();
        Assert.Contains("Claude 还没有获得授权", claude);
        // Beside the idle island: the busiest quota, and an item per account (named after it).
        var chip = WaitFor(() => _ambientSeen.LastOrDefault(c => c.Key == "com.o_o.ai-usage/summary"), "summary item");
        Assert.Contains("62%", Texts(chip.View).ToList());
    }
}

public sealed partial class PluginHostTests
{
    private void StartAggregatorWithFake(string usedPercent)
    {
        CopyPlugin(RepoPlugin("com.o_o.ai-usage"), Path.Combine(_root, "com.o_o.ai-usage"));
        if (usedPercent.Length > 0)
        {
            WritePluginWithManifest("test.fakeai",
                """, "contributes": { "providers": [{ "interface": "ai-usage", "id": "fake", "title": "Fake" }] }""", "", $$"""
                import { island } from '@island/api';
                export function activate() {
                  island.provide('ai-usage', 'fake', {
                    accounts: () => [{ handle: 'h', label: 'me' }],
                    usage: () => ({ windows: [{ name: '每周', usedPercent: {{usedPercent}} }] }),
                  });
                }
                """);
        }
        StartDiscovered();
    }

    [Fact]
    public void The_aggregator_stays_off_the_collapsed_island_while_it_has_nothing_to_show()
    {
        StartAggregatorWithFake("");
        WaitFor(() => _host!.GetState("com.o_o.ai-usage") == PluginState.Running ? "ok" : null, "aggregator running");
        Thread.Sleep(500);
        Assert.DoesNotContain(_host!.LiveActivities, a => a.PluginId == "com.o_o.ai-usage");
        Assert.DoesNotContain(_ambientSeen, a => a.PluginId == "com.o_o.ai-usage" && a.View is not null);
        // Its page is there all the same, saying what to do.
        WaitForPage(s => Texts(s.View.Expanded).Contains("还没有可用的 AI 提供商"), "empty page");
    }

    [Fact]
    public void The_aggregator_shows_a_limit_past_the_threshold_as_an_activity_and_alerts_once()
    {
        StartAggregatorWithFake("95");
        var activity = WaitFor(() => _host!.LiveActivities.FirstOrDefault(a => a.Kind == "com.o_o.ai-usage/usage"), "limit activity");
        Assert.Equal(LivePriority.High, activity.Priority);
        Assert.Equal("95%", Text(activity.View.Compact.Trailing));
        Assert.Contains("AI 额度已用 95%", Texts(activity.View.Expanded.Center!));
        var account = WaitFor(() => _ambientSeen.LastOrDefault(a => a.Key == "com.o_o.ai-usage/account#fake:me" && a.View is not null), "account item");
        Assert.Equal("Fake · me", account.Title);
        Assert.False(account.EnabledByDefault);                     // per-account items wait to be switched on
        var alert = WaitFor(() => _liveAlerts.FirstOrDefault(), "alert");
        Assert.Equal("com.o_o.ai-usage/usage", alert.Kind);
        Assert.Equal("TriangleAlert", alert.View.Leading!.GetString("name"));
        Assert.Contains("95%", Text(alert.View.Center));
        Thread.Sleep(300);
        Assert.Single(_liveAlerts);

        _host!.Dispose();
        _updates.Clear();
        Directory.Delete(_root, true);
        Directory.CreateDirectory(_root);
        StartAggregatorWithFake("40");
        WaitFor(() => _ambientSeen.LastOrDefault(c => Texts(c.View).Contains("40%")), "item at 40%");
        Assert.DoesNotContain(_host!.LiveActivities, a => a.PluginId == "com.o_o.ai-usage"); // nothing urgent below the threshold
    }
}

public sealed partial class PluginHostTests
{
    [Fact]
    public void Claude_usage_flows_from_the_status_line_through_the_shipped_plugins_without_any_token()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // A signed-in Claude Code configuration (credential file only proves the login; it is never read).
        var configDir = Path.Combine(_data, "claude-config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, ".claude.json"), """{ "oauthAccount": { "emailAddress": "me@example.com" } }""");
        File.WriteAllText(Path.Combine(configDir, ".credentials.json"), """{ "claudeAiOauth": { "accessToken": "never-read" } }""");
        var claude = new Services.ClaudeCodeSource(new Island.Platform.InMemorySecretStore(), [configDir], useKeychain: false,
            home: _data, scan: false, planLookup: (_, _) => "Max 20x");
        var services = Services with { LocalAuthSources = [claude] };

        // Connect the status line, then run it exactly as Claude Code would, with documented sample data.
        services.ClaudeStatusLine.Install(configDir);
        var command = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(configDir, "settings.json")))!
            ["statusLine"]!["command"]!.GetValue<string>();
        var info = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        using (var sh = System.Diagnostics.Process.Start(info)!)
        {
            sh.StandardInput.Write("""{ "rate_limits": { "five_hour": { "used_percentage": 23, "resets_at": 4102444800 }, "seven_day": { "used_percentage": 41 } } }""");
            sh.StandardInput.Close();
            sh.WaitForExit();
        }

        foreach (var id in new[] { "com.o_o.ai-usage", "com.o_o.ai.claude" })
        {
            CopyPlugin(RepoPlugin(id), Path.Combine(_root, id));
        }
        StartWith(null, services);
        WaitFor(() => _host!.GetState("com.o_o.ai.claude") == PluginState.AwaitingConsent ? "ok" : null, "consent");
        _host!.SetGranted("com.o_o.ai.claude", ["localAuth:claude-code"]);

        var shown = WaitForPage(s => s.PluginId == "com.o_o.ai-usage" &&
                                       Texts(s.View.Expanded).Any(t => t.StartsWith("[41%]")), "claude gauges");
        var texts = Texts(shown.View.Expanded).ToList();
        Assert.Contains(Nodes(shown.View.Expanded), n => n.Type == "logo" && n.GetString("name") == "claude");
        Assert.Contains("me@example.com", texts);
        Assert.Contains("Max 20x", texts);
        Assert.Contains(texts, t => t.StartsWith("[41%] 每周"));       // the busiest window is the big ring
        Assert.Contains(texts, t => t.StartsWith("[23%] 5 小时"));     // the other one a progress row
        Assert.Contains(texts, t => t.StartsWith("更新于"));
        // The shipped Claude plugin asks for no network permission at all.
        Assert.DoesNotContain(_host.GetPermissions("com.o_o.ai.claude").Declared, p => p.StartsWith("network:"));
    }
}

public sealed partial class PluginHostTests
{
    [Fact]
    public void A_balance_provider_shows_its_amount_and_accounts_can_be_switched()
    {
        CopyPlugin(RepoPlugin("com.o_o.ai-usage"), Path.Combine(_root, "com.o_o.ai-usage"));
        WritePluginWithManifest("test.fakeai",
            """, "contributes": { "providers": [{ "interface": "ai-usage", "id": "quota", "title": "Quota" }, { "interface": "ai-usage", "id": "money", "title": "Money" }] }""", "", """
            import { island } from '@island/api';
            export function activate() {
              island.provide('ai-usage', 'quota', {
                accounts: () => [{ handle: 'a', label: 'a@example.com' }, { handle: 'b', label: 'b@example.com' }],
                usage: (acc) => ({ windows: [{ name: '每周', usedPercent: acc.handle === 'a' ? 10 : 70 }] }),
              });
              island.provide('ai-usage', 'money', {
                accounts: () => [{ handle: 'k', label: 'sk-1' }],
                usage: () => ({ balance: { amount: 4.18, currency: 'CNY', details: [{ label: '赠送', amount: 1 }] } }),
              });
            }
            """);
        StartDiscovered();

        // Accounts of the current provider are a picker; switching shows that account.
        var quota = PickProvider("quota", s => Texts(s.View.Expanded).Any(t => t.StartsWith("[10%]")));
        Assert.Contains(Texts(quota.View.Expanded), t => t.StartsWith("[10%]"));
        var picker = Nodes(LatestUsage().View.Expanded).First(n => n.Type == "picker");
        _host!.Dispatch("com.o_o.ai-usage", picker.Handlers["onChange"], "\"1\"");
        WaitForPage(s => Texts(s.View.Expanded).Any(t => t.StartsWith("[70%]")), "second account");

        // A top-up provider: a rolling balance with its breakdown, no ring.
        var money = PickProvider("money", s => Nodes(s.View.Expanded).Any(n => n.Type == "counter"));
        var counter = Nodes(money.View.Expanded).First(n => n.Type == "counter");
        Assert.Equal(4.18, counter.GetNumber("value"));
        Assert.Equal("¥", counter.GetString("prefix"));
        Assert.Contains("¥1.00", Texts(money.View.Expanded));
        Assert.DoesNotContain(Nodes(money.View.Expanded), n => n.Type == "gauge");

        // The arrows go round: from the last provider, "next" returns to the first.
        var next = Nodes(LatestUsage().View.Expanded).Where(n => n.Type == "iconButton").Last();
        _host.Dispatch("com.o_o.ai-usage", next.Handlers["onClick"]);
        WaitForPage(s => Shown(s, "swap") == "quota", "wrapped to first");
    }
}
