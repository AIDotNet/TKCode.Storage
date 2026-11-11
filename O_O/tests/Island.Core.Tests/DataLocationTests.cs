using Island.Core.Storage;

namespace Island.Core.Tests;

/// <summary>Moving O_O's data to a folder of the user's choosing (关于 → 数据目录).</summary>
public sealed class DataLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("o_o-data-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string DefaultRoot()
    {
        var root = Path.Combine(_root, "AppData", "O_O");
        Directory.CreateDirectory(Path.Combine(root, "plugin-data", "com.x"));
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(root, "O_O-main-me.lock"), "");
        File.WriteAllText(Path.Combine(root, "chips.json"), "chips");
        File.WriteAllText(Path.Combine(root, "plugin-data", "com.x", "storage.json"), "data");
        return root;
    }

    [Fact]
    public void Moving_out_of_the_default_folder_leaves_settings_locks_and_updates_behind()
    {
        var root = DefaultRoot();
        var target = Path.Combine(_root, "D", "O_O-data");
        Assert.Null(DataLocation.Problem(root, target, root));
        Assert.Null(DataLocation.Copy(root, target, root));
        DataLocation.DeleteOld(root, root);

        Assert.Equal("data", File.ReadAllText(Path.Combine(target, "plugin-data", "com.x", "storage.json")));
        Assert.Equal("chips", File.ReadAllText(Path.Combine(target, "chips.json")));
        Assert.False(File.Exists(Path.Combine(target, "settings.json")));
        Assert.Equal(
            ["O_O-main-me.lock", "settings.json", "updates"],
            Directory.EnumerateFileSystemEntries(root).Select(e => Path.GetFileName(e)).Order().ToArray());
    }

    [Fact]
    public void Moving_back_to_the_default_folder_and_between_custom_folders_works()
    {
        var root = DefaultRoot();
        var first = Path.Combine(_root, "first");
        Assert.Null(DataLocation.Copy(root, first, root));
        DataLocation.DeleteOld(root, root);

        var second = Path.Combine(_root, "second");
        Assert.Null(DataLocation.Problem(first, second, root));
        Assert.Null(DataLocation.Copy(first, second, root));
        DataLocation.DeleteOld(first, root);
        Assert.False(Directory.Exists(first));

        // The default folder still holds the settings: it may be the target all the same.
        Assert.Null(DataLocation.Problem(second, root, root));
        Assert.Null(DataLocation.Copy(second, root, root));
        DataLocation.DeleteOld(second, root);
        Assert.Equal("data", File.ReadAllText(Path.Combine(root, "plugin-data", "com.x", "storage.json")));
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public void A_folder_with_other_files_gets_an_O_O_folder_inside()
    {
        var root = DefaultRoot();
        var drive = Path.Combine(_root, "D");
        Directory.CreateDirectory(drive);
        Assert.Equal(drive, DataLocation.Target(drive, root));
        File.WriteAllText(Path.Combine(drive, "other.txt"), "");
        Assert.Equal(Path.Combine(drive, "O_O"), DataLocation.Target(drive, root));
        Assert.Equal(root, DataLocation.Target(root, root));
    }

    [Fact]
    public void Unsafe_targets_are_refused()
    {
        var root = DefaultRoot();
        Assert.NotNull(DataLocation.Problem(root, root, root));                                  // already there
        Assert.NotNull(DataLocation.Problem(root, Path.Combine(root, "plugin-data", "x"), root)); // inside itself
        Assert.NotNull(DataLocation.Problem(root, "relative", root));
        var used = Path.Combine(_root, "used");
        Directory.CreateDirectory(used);
        File.WriteAllText(Path.Combine(used, "a"), "");
        Assert.NotNull(DataLocation.Problem(root, used, root));                                  // not empty
        var custom = Path.Combine(_root, "custom", "inner");
        Assert.NotNull(DataLocation.Problem(custom, Path.Combine(_root, "custom"), root));        // contains the data
    }

    [Fact]
    public void A_failed_copy_removes_what_it_copied_and_keeps_the_original()
    {
        var root = DefaultRoot();
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(target, "plugin-data")); // a clash part-way
        Assert.NotNull(DataLocation.Copy(root, target, root));
        Assert.True(File.Exists(Path.Combine(root, "chips.json")));
        Assert.False(File.Exists(Path.Combine(target, "chips.json")));
    }
}
