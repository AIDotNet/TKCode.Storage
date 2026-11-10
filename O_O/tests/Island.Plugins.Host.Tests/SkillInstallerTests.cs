using Island.Plugins.Host;
using Xunit;

namespace Island.Plugins.Host.Tests;

public sealed class SkillInstallerTests
{
    [Fact]
    public void Install_writes_the_skill_with_its_template_and_api_reference()
    {
        var root = Path.Combine(Path.GetTempPath(), "o_o-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = SkillInstaller.Install(root);
            Assert.Equal(Path.Combine(root, "o_o-plugin"), folder);
            Assert.StartsWith("---", File.ReadAllText(Path.Combine(folder, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(folder, "template", "plugin.json")));
            Assert.True(File.Exists(Path.Combine(folder, "template", "main.js")));
            Assert.True(File.Exists(Path.Combine(folder, "api.js")));
            SkillInstaller.Install(root); // again: an update, not an error
            Assert.Contains(folder, SkillInstaller.Prompt(folder));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
