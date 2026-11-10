using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    private sealed class FakeClipboard : IPluginClipboard
    {
        public ClipboardEntry? Content;
        public readonly List<ClipboardEntry> Pasted = [];
        public string? PasteRefusal;
        public string? Folder;

        public string? ImageFolder => Folder;

        public event Action<ClipboardEntry>? Changed;

        public void Copy(ClipboardEntry entry)
        {
            Content = entry;
            Changed?.Invoke(entry);
        }

        public Task<ClipboardEntry?> ReadAsync(CancellationToken ct) => Task.FromResult(Content);

        public Task WriteAsync(ClipboardEntry entry, CancellationToken ct)
        {
            Content = entry;
            return Task.CompletedTask;
        }

        public Task<(bool, string?)> PasteAsync(ClipboardEntry entry, CancellationToken ct)
        {
            Content = entry;
            if (PasteRefusal is null)
            {
                Pasted.Add(entry);
            }
            return Task.FromResult((PasteRefusal is null, PasteRefusal));
        }
    }

    [Fact]
    public void Clipboard_entries_reach_watchers_and_writes_and_pastes_go_through_with_their_permissions()
    {
        var clipboard = new FakeClipboard { PasteRefusal = null };
        _permissions = "\"clipboard.watch\", \"clipboard.write\", \"clipboard.paste\"";
        StartWith("""
            import { clipboard } from '@island/api';
            export function activate() {
              clipboard.onChange(async (e) => {
                console.info(`seen ${e.kind} ${e.text ?? e.files.join(',')}`);
                if (e.text === 'paste me') {
                  const r = await clipboard.paste({ text: 'pasted!' });
                  console.info('paste ' + r.pasted);
                }
              });
              clipboard.read().then(() => console.info('read allowed'), (e) => console.info('read refused: ' + e.message));
              clipboard.write({ files: ['relative/path'] }).catch((e) => console.info('bad write: ' + e.message));
            }
            """, Services with { Clipboard = clipboard });

        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("read refused") && l.Message.Contains("clipboard.read")), "read needs its permission");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("bad write") && l.Message.Contains("绝对路径")), "files must be absolute");

        clipboard.Copy(ClipboardEntry.FromText("https://example.com/a", "Safari", DateTimeOffset.Now));
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "seen url https://example.com/a"), "url entry");
        clipboard.Copy(ClipboardEntry.FromFiles(["/tmp/a.txt", "/tmp/b.png"], null, DateTimeOffset.Now));
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "seen files /tmp/a.txt,/tmp/b.png"), "files entry");

        clipboard.Copy(ClipboardEntry.FromText("paste me", null, DateTimeOffset.Now));
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "paste true"), "paste");
        Assert.Equal("pasted!", Assert.Single(clipboard.Pasted).Text);
    }

    [Fact]
    public void Without_clipboard_permissions_there_is_no_clipboard_and_no_events()
    {
        var clipboard = new FakeClipboard();
        StartWith("""
            import { clipboard } from '@island/api';
            export function activate() { console.info('clipboard ' + typeof clipboard); }
            """, Services with { Clipboard = clipboard });
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "clipboard undefined"), "no export");
        clipboard.Copy(ClipboardEntry.FromText("secret", null, DateTimeOffset.Now)); // nobody may see it
        Thread.Sleep(200);
        Assert.Equal(PluginState.Running, _host!.GetState("test.plugin"));
    }

    [Fact]
    public void Only_recorded_images_may_be_put_back_on_the_clipboard()
    {
        var folder = Path.Combine(_data, "imgs");
        Directory.CreateDirectory(folder);
        var inside = Path.Combine(folder, "a.png");
        File.WriteAllText(inside, "x");
        var outside = Path.Combine(_data, "secret.png");
        File.WriteAllText(outside, "x");
        Assert.Equal(inside, ClipboardEntry.Parse($$"""{ "image": "{{inside.Replace("\\", "\\\\")}}" }""", folder).Image);
        Assert.Throws<PluginCapabilityException>(() => ClipboardEntry.Parse($$"""{ "image": "{{outside.Replace("\\", "\\\\")}}" }""", folder));
        Assert.Throws<PluginCapabilityException>(() => ClipboardEntry.Parse($$"""{ "image": "{{Path.Combine(folder, "..", "secret.png").Replace("\\", "\\\\")}}" }""", folder));
        Assert.Contains("\"width\":1280", ClipboardEntry.FromImage(inside, 1280, 720, null, default).ToJson());
    }

    [Fact]
    public void A_lone_web_address_is_a_url_and_long_text_is_cut()
    {
        Assert.Equal("url", ClipboardEntry.FromText(" https://x.dev/p?q=1 ", null, default).Kind);
        Assert.Equal("text", ClipboardEntry.FromText("see https://x.dev", null, default).Kind);
        Assert.Equal("text", ClipboardEntry.FromText("ftp://x", null, default).Kind);
        Assert.Equal(ClipboardEntry.MaxTextLength, ClipboardEntry.FromText(new string('a', ClipboardEntry.MaxTextLength + 5), null, default).Text!.Length);
    }
}

public sealed partial class PluginHostTests
{
    private IEnumerable<VNode> HistoryRows(ActivitySnapshot page) => Nodes(page.View.Expanded).Where(n => n.Type == "item");

    [Fact]
    public void The_clipboard_history_records_dedupes_trims_and_pastes_on_click()
    {
        var clipboard = new FakeClipboard();
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.clipboard"), Path.Combine(_root, "com.o_o.clipboard"));
        StartWith(null, Services with { Clipboard = clipboard });
        WaitForPage(s => s.PluginId == "com.o_o.clipboard" && Texts(s.View.Expanded).Contains("复制的内容会出现在这里。"), "empty history");

        var now = DateTimeOffset.Now;
        clipboard.Copy(ClipboardEntry.FromText("first\nsecond line", "Notes", now));
        clipboard.Copy(ClipboardEntry.FromText("https://example.com", "Safari", now));
        clipboard.Copy(ClipboardEntry.FromFiles(["/tmp/report.pdf"], "Finder", now));
        clipboard.Copy(ClipboardEntry.FromText("first\nsecond line", "Notes", now)); // again: moves to the top
        var page = WaitForPage(s => s.PluginId == "com.o_o.clipboard" && HistoryRows(s).Count() == 3 &&
                                    Texts(HistoryRows(s).First()).Contains("first ⏎ second line"), "three rows, newest first");
        var texts = HistoryRows(page).Select(r => string.Join("|", Texts(r))).ToList();
        Assert.Contains("report.pdf", texts[1]);
        Assert.Contains("https://example.com", texts[2]);

        _host!.Dispatch("com.o_o.clipboard", HistoryRows(page).ElementAt(2).Handlers["onClick"]);
        WaitFor(() => clipboard.Pasted.Count == 1 ? "ok" : null, "pasted on click");
        Assert.Equal("https://example.com", clipboard.Pasted[0].Text);

        // A copied image: a thumbnail row, and clicking it pastes that same image.
        clipboard.Folder = Path.Combine(_data, "clipboard-images");
        Directory.CreateDirectory(clipboard.Folder);
        var png = Path.Combine(clipboard.Folder, "abc.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
        clipboard.Copy(ClipboardEntry.FromImage(png, 1280, 720, "Preview", now));
        page = WaitForPage(s => s.PluginId == "com.o_o.clipboard" && Texts(HistoryRows(s).FirstOrDefault()).Contains("图片 1280×720"), "image row");
        Assert.Equal(png, Nodes(HistoryRows(page).First()).Single(n => n.Type == "image").GetString("path"));
        _host.Dispatch("com.o_o.clipboard", HistoryRows(page).First().Handlers["onClick"]);
        WaitFor(() => clipboard.Pasted.Count == 2 ? "ok" : null, "image pasted");
        Assert.Equal(png, clipboard.Pasted[1].Image);
    }
}

public sealed partial class PluginHostTests
{
    [Fact]
    public void Links_open_only_right_after_a_click_and_only_safe_kinds()
    {
        var opened = new System.Collections.Concurrent.ConcurrentQueue<Uri>();
        _permissions = "\"shell.openUrl\"";
        _contributes = """{ "pages": [{ "id": "p" }] }""";
        StartWith("""
            import { island, ui, shell } from '@island/api';
            export async function activate() {
              try { await shell.openUrl('https://example.com/a'); } catch (e) { console.info('no click: ' + e.message); }
              island.page('p').update(ui.button('go', { onClick: async () => {
                await shell.openUrl('https://example.com/b');
                try { await shell.openUrl('file:///etc/passwd'); } catch (e) { console.info('file: ' + e.message); }
              } }));
            }
            """, Services with { OpenBrowser = u => { opened.Enqueue(u); return Task.CompletedTask; } });
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("no click:") && l.Message.Contains("clicks")), "refused without a click");
        var page = WaitFor(() => _pages.LastOrDefault(), "page");
        _host!.Dispatch("test.plugin", page.View!.Handlers["onClick"]);
        WaitFor(() => opened.Count == 1 ? "ok" : null, "opened after the click");
        Assert.Equal("https://example.com/b", opened.Single().AbsoluteUri);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("file:") && l.Message.Contains("http(s)")), "file: refused");
    }
}

public sealed partial class PluginHostTests
{
    [Fact]
    public void Clipboard_search_filters_as_you_type_and_enter_pastes_the_best_match()
    {
        var clipboard = new FakeClipboard();
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.clipboard"), Path.Combine(_root, "com.o_o.clipboard"));
        StartWith(null, Services with { Clipboard = clipboard });
        WaitForPage(s => s.PluginId == "com.o_o.clipboard", "history");
        var now = DateTimeOffset.Now;
        foreach (var text in new[] { "alpha one", "beta two", "alpha three" })
        {
            clipboard.Copy(ClipboardEntry.FromText(text, null, now));
        }
        var page = WaitForPage(s => s.PluginId == "com.o_o.clipboard" && HistoryRows(s).Count() == 3, "three entries");
        var search = Nodes(page.View.Expanded).Single(n => n.Type == "input");
        Assert.Equal("search", search.GetString("kind"));

        _host!.Dispatch("com.o_o.clipboard", search.Handlers["onChange"], "\"alpha\"");
        page = WaitForPage(s => s.PluginId == "com.o_o.clipboard" && HistoryRows(s).Count() == 2 &&
                                HistoryRows(s).All(r => Texts(r).Any(t => t.StartsWith("alpha"))), "filtered to alpha");
        Assert.Equal("alpha three", Texts(HistoryRows(page).First()).First(t => t.StartsWith("alpha")));
        search = Nodes(page.View.Expanded).Single(n => n.Type == "input");
        _host.Dispatch("com.o_o.clipboard", search.Handlers["onSubmit"], "\"alpha\"");
        WaitFor(() => clipboard.Pasted.Count == 1 ? "ok" : null, "enter pastes");
        Assert.Equal("alpha three", clipboard.Pasted[0].Text);

        Thread.Sleep(300); // pasting re-rendered the page: use the latest box's handlers
        search = Nodes(_pageViews.Last(s => s.PluginId == "com.o_o.clipboard").View.Expanded).Single(n => n.Type == "input");
        _host.Dispatch("com.o_o.clipboard", search.Handlers["onChange"], "\"zzz\"");
        WaitForPage(s => s.PluginId == "com.o_o.clipboard" && Texts(s.View.Expanded).Contains("没有包含\"zzz\"的内容。"), "no match");
    }

    [Fact]
    public void A_to_do_typed_on_the_island_is_added_with_its_date()
    {
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.todo"), Path.Combine(_root, "com.o_o.todo"));
        StartWith(null, Services);
        WaitForPage(s => s.PluginId == "com.o_o.todo" && Nodes(s.View.Expanded).Any(n => n.Type == "input"), "to-do page with a box");
        Thread.Sleep(300); // handlers are renewed on every render: use the latest view's
        var page = _pageViews.Last(s => s.PluginId == "com.o_o.todo");
        var box = Nodes(page.View.Expanded).First(n => n.Type == "input");
        _host!.Dispatch("com.o_o.todo", box.Handlers["onSubmit"], "\"!明天 15:30 交周报\"");
        page = WaitForPage(s => s.PluginId == "com.o_o.todo" && Texts(s.View.Expanded).Any(t => t.Contains("交周报")), "task added");
        Assert.NotEqual(box.Key, Nodes(page.View.Expanded).First(n => n.Type == "input").Key); // a fresh, empty box
    }
}

public sealed partial class PluginHostTests
{
    [Fact]
    public void Notifications_carry_the_plugins_name_and_are_rate_limited()
    {
        _permissions = "\"notifications\"";
        StartWith("""
            import { notify } from '@island/api';
            export async function activate() {
              await notify.show({ title: '番茄完成', body: '休息\u0007一下' });
              try { await notify.show({ title: 'again' }); } catch (e) { console.info('second: ' + e.message); }
            }
            """, Services);
        WaitFor(() => _notifier.Shown.FirstOrDefault() is { Title: not null } n ? "ok" : null, "shown");
        Assert.Equal(("Test · 番茄完成", "休息一下"), _notifier.Shown.Single());          // name first, control chars dropped
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("second:") && l.Message.Contains("10 秒")), "rate limited");
    }

    [Fact]
    public void Focus_sends_a_system_notification_when_a_pomodoro_ends()
    {
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.focus"), Path.Combine(_root, "com.o_o.focus"));
        // A pomodoro that ended while O_O was closed finishes on start (see the focus tests): it notifies.
        Directory.CreateDirectory(Path.Combine(_data, "com.o_o.focus"));
        File.WriteAllText(Path.Combine(_data, "com.o_o.focus", "storage.json"),
            $$"""{ "state": { "phase": "work", "running": true, "endsAt": {{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()}}, "cycle": 0 } }""");
        StartWith(null, Services);
        WaitFor(() => _notifier.Shown.FirstOrDefault(n => n.Title.Contains("完成一个番茄")) is { Title: not null } ? "ok" : null, "notified");
    }
}
