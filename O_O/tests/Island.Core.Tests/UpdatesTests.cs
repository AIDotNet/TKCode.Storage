using Island.Core.Updates;

namespace Island.Core.Tests;

public class UpdatesTests
{
    [Theory]
    [InlineData("0.1.1000", 0, 1, 1000)]
    [InlineData("v0.1.1001+abc123", 0, 1, 1001)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("0.1.1002-beta", 0, 1, 1002)]
    public void Versions_parse_major_minor_build(string text, int major, int minor, int build) =>
        Assert.Equal(new AppVersion(major, minor, build), AppVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.-2.3")]
    public void Not_versions(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_as_numbers_part_by_part()
    {
        Assert.True(AppVersion.Parse("0.1.1001") > AppVersion.Parse("0.1.1000"));
        Assert.True(AppVersion.Parse("0.1.1000") > AppVersion.Parse("0.1.999"));    // not as text
        Assert.True(AppVersion.Parse("0.2.0") > AppVersion.Parse("0.1.9999"));
        Assert.True(AppVersion.Parse("1.0") > AppVersion.Parse("0.99.99999"));
        Assert.Equal(AppVersion.Parse("0.1.1000"), AppVersion.Parse("0.1.1000+deadbeef"));
    }

    [Fact]
    public void The_version_answer_is_read_and_checked_against_the_current_version()
    {
        var info = ServiceContract.ParseVersion("""
            { "version": "0.1.1001", "publishedAt": "2026-11-10T08:00:00Z", "notes": "修复若干问题",
              "downloads": { "macos-arm64": "https://example.com/O_O-0.1.1001-macos-arm64.dmg" },
              "download": "https://example.com/O_O-0.1.1001-macos-arm64.dmg", "updateAvailable": true, "extra": 1 }
            """);
        Assert.Equal("0.1.1001", info.Version);
        Assert.Equal("修复若干问题", info.Notes);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), info.PublishedAt);
        Assert.Single(info.Downloads);
        Assert.True(info.IsNewerThan(AppVersion.Parse("0.1.1000")));
        Assert.False(info.IsNewerThan(AppVersion.Parse("0.1.1001")));     // the service's flag is not trusted alone
        Assert.Null(ServiceContract.ParseVersion("""{ "version": "0.1.1000", "download": null }""").Download);
    }

    [Fact]
    public void The_catalog_answer_is_read()
    {
        var catalog = ServiceContract.ParseCatalog("""
            { "plugins": [ { "id": "com.o_o.cal.ics", "name": "日历订阅", "version": "0.1.0", "permissions": ["network:@url1"],
              "requires": ["com.o_o.calendar"], "platforms": [], "size": 1234, "sha256": "ab", "download": "https://x/p.zip" },
              { "id": "com.o_o.cal.system", "name": "系统日历", "version": "0.1.0", "platforms": ["macos"] } ] }
            """);
        Assert.Equal(2, catalog.Plugins.Count);
        Assert.Equal(["com.o_o.calendar"], catalog.Plugins[0].Requires);
        Assert.True(catalog.Plugins[0].RunsOn("windows"));
        Assert.False(catalog.Plugins[1].RunsOn("windows"));
        Assert.True(catalog.Plugins[1].RunsOn("macos"));
    }

    [Fact]
    public void Update_preferences_survive_a_round_trip_and_default_to_checking()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-updates-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new UpdatePreferences(path);
            Assert.True(prefs.AutoCheck);
            Assert.Null(prefs.AnnouncedVersion);
            prefs.SetAutoCheck(false);
            prefs.SetAnnounced("0.1.1001");
            var back = new UpdatePreferences(path);
            Assert.False(back.AutoCheck);
            Assert.Equal("0.1.1001", back.AnnouncedVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Only_https_or_a_local_development_service_is_allowed()
    {
        Assert.True(ServiceEndpoints.IsAllowed(new Uri("https://island.astra-gate.si/api/version")));
        Assert.True(ServiceEndpoints.IsAllowed(new Uri("http://localhost:5080/api/plugins")));
        Assert.True(ServiceEndpoints.IsAllowed(new Uri("http://127.0.0.1:5080/plugins/a.zip")));
        Assert.False(ServiceEndpoints.IsAllowed(new Uri("http://example.com/a.zip")));
        Assert.False(ServiceEndpoints.IsAllowed(new Uri("ftp://example.com/a.zip")));
        Assert.Matches("^(macos|windows|linux)-(arm64|x64)$", ServiceEndpoints.BuildPlatform);
    }
}
