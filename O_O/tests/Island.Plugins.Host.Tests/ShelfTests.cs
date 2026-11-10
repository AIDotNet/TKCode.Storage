namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    [Fact]
    public void Dropped_files_land_on_the_shelf_with_their_size_and_are_marked_when_they_disappear()
    {
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.shelf"), Path.Combine(_root, "com.o_o.shelf"));
        StartWith(null, Services);
        WaitForPage(s => s.PluginId == "com.o_o.shelf" && Texts(s.View.Expanded).Contains("把文件拖到岛上暂存"), "empty shelf");
        WaitForState(PluginState.Running); // the page is drawn during activate(), a moment before the plugin counts as running
        Assert.False(_host!.DropFiles(["relative/x"]));                 // only absolute paths are handed over

        var report = Path.Combine(_data, "report.pdf");
        var photo = Path.Combine(_data, "photo.png");
        File.WriteAllBytes(report, new byte[2048]);
        File.WriteAllBytes(photo, new byte[10]);
        Assert.True(_host.DropFiles([report, photo]));

        var page = WaitForPage(s => s.PluginId == "com.o_o.shelf" && Nodes(s.View.Expanded).Count(n => n.Type == "card") == 2 &&
                                    Texts(s.View.Expanded).Contains("2 KB"), "two files with sizes");
        var rows = Nodes(page.View.Expanded).Where(n => n.Type == "card").ToList();
        Assert.Equal(report, rows.First(r => Texts(r).Contains("report.pdf")).GetString("dragFiles"));
        Assert.Contains(Nodes(rows.First(r => Texts(r).Contains("report.pdf"))), n => n.Type == "icon" && n.GetString("name") == "FileText");
        // A picture shows itself (a thumbnail of the file), not a generic icon.
        Assert.Equal(photo, Nodes(rows.First(r => Texts(r).Contains("photo.png"))).Single(n => n.Type == "image").GetString("path"));
        var chip = WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.shelf/count" && c.View is not null), "count chip");
        Assert.Contains("2", Texts(chip.View).ToList());

        File.Delete(photo);
        _host.DropFiles([report]); // any change makes it look again
        WaitForPage(s => s.PluginId == "com.o_o.shelf" && Texts(s.View.Expanded).Contains("已移动或删除"), "missing file marked");
    }

    [Fact]
    public void Files_capability_needs_its_permission_and_absolute_paths()
    {
        _permissions = "\"files.shelf\"";
        StartWith("""
            import { files } from '@island/api';
            export async function activate() {
              try { await files.info(['x']); } catch (e) { console.info('info: ' + e.message); }
              const [f] = await files.info(['/definitely/not/here']);
              console.info(`exists ${f.exists} name ${f.name}`);
              try { await files.reveal('/definitely/not/here'); } catch (e) { console.info('reveal: ' + e.message); }
            }
            """, Services);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("info: 不是绝对路径")), "relative refused");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "exists false name here"), "missing file info");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "reveal: 文件已不存在。"), "reveal of a missing file refused");
    }
}
