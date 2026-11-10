namespace Island.Plugins.Host.Tests;

public class VNodeJsonTests
{
    [Fact]
    public void Parses_props_handlers_keys_and_children()
    {
        var view = VNodeJson.ParsePage("""
            { "status": { "t": "icon", "p": { "name": "Clock" } },
              "expanded": { "t": "row", "k": 7, "p": { "gap": "sm", "on": true, "n": 2.5, "onClick": { "$h": 12 }, "skip": null, "obj": { "a": 1 } },
                            "c": [ { "t": "text", "p": { "text": "hi" } }, 5, { "t": "spacer" } ] } }
            """);
        Assert.Equal("Clock", view.Status!.GetString("name"));
        var row = view.View!;
        Assert.Equal("7", row.Key);
        Assert.Equal("sm", row.GetString("gap"));
        Assert.True(row.GetBool("on"));
        Assert.Equal(2.5, row.GetNumber("n"));
        Assert.Equal(12, row.Handlers["onClick"]);
        Assert.False(row.Props.ContainsKey("skip"));
        Assert.False(row.Props.ContainsKey("obj"));
        Assert.Equal(["text", "spacer"], row.Children.Select(c => c.Type));
    }

    [Fact]
    public void Rejects_nodes_without_a_type_and_trees_that_are_too_deep()
    {
        Assert.Throws<InvalidDataException>(() => VNodeJson.ParseNode("""{ "p": {} }"""));

        var deep = string.Concat(Enumerable.Repeat("""{ "t": "stack", "c": [""", VNodeJson.MaxDepth + 2)) +
                   """{ "t": "text" }""" + new string(']', 0) +
                   string.Concat(Enumerable.Repeat("] }", VNodeJson.MaxDepth + 2));
        Assert.Throws<InvalidDataException>(() => VNodeJson.ParseNode(deep));
    }

    [Fact]
    public void Same_slot_means_same_type_and_key()
    {
        var a = VNodeJson.ParseNode("""{ "t": "text", "k": "x" }""");
        var b = VNodeJson.ParseNode("""{ "t": "text", "k": "x", "p": { "text": "changed" } }""");
        var c = VNodeJson.ParseNode("""{ "t": "text", "k": "y" }""");
        Assert.True(a.IsSameSlot(b));
        Assert.False(a.IsSameSlot(c));
    }
}

public class PluginManifestTests
{
    [Fact]
    public void A_minimal_manifest_gets_every_default()
    {
        var m = PluginManifest.Parse("""{ "id": "x", "name": "X", "version": "1.0.0" }""");
        Assert.Equal("main.js", m.Main);
        Assert.Empty(m.Permissions);
        Assert.Empty(m.Contributes.Activities);
        Assert.Null(m.Contributes.Settings);
        Assert.Equal("^1.0.0", m.Engines.Island);
    }
}
