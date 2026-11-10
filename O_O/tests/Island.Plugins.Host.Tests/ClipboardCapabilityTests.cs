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
