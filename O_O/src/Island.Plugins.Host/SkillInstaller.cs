using System.Reflection;

namespace Island.Plugins.Host;

/// <summary>
/// Installs the "o_o-plugin" agent skill (how to write, test and package an O_O plugin) into the shared
/// <c>~/.agents/skills</c> folder that coding agents read, so the user can ask their agent to write a plugin.
/// The skill's files are embedded in the app; installing again overwrites them (an update).
/// </summary>
public static class SkillInstaller
{
    public const string SkillName = "o_o-plugin";

    private const string ResourcePrefix = "Island.Skills." + SkillName + "/";

    /// <summary><c>~/.agents/skills</c>.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");

    /// <summary>Writes the skill to <c>&lt;root&gt;/o_o-plugin</c> and returns that folder.</summary>
    public static string Install(string? root = null)
    {
        var target = Path.Combine(root ?? DefaultRoot, SkillName);
        var assembly = typeof(SkillInstaller).Assembly;
        var written = 0;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var relative = resource[ResourcePrefix.Length..].Replace('\\', '/');
            var file = Path.GetFullPath(Path.Combine(target, relative));
            if (!file.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var input = assembly.GetManifestResourceStream(resource)!;
            using var output = File.Create(file);
            input.CopyTo(output);
            written++;
        }
        // The plugin API doc rides along so an agent outside this repo can read it.
        if (written > 0 && assembly.GetManifestResourceStream("Island.Plugins.Host.Runtime.api.js") is { } api)
        {
            using (api)
            using (var output = File.Create(Path.Combine(target, "api.js")))
            {
                api.CopyTo(output);
            }
        }
        if (written == 0)
        {
            throw new InvalidOperationException("The plugin skill is missing from this build.");
        }
        return target;
    }

    /// <summary>A prompt the user pastes into their agent to get a plugin written with the installed skill.</summary>
    public static string Prompt(string skillFolder) =>
        $"请使用 {SkillName} 技能（已安装在 {skillFolder}，先阅读其中的 SKILL.md）为 O_O 灵动岛写一个插件。\n" +
        "我想要的插件：<在这里描述功能，例如：在灵动岛上显示当前城市的天气>\n" +
        "请先和我确认需求和需要的权限，再按技能里的流程编写、校验，并用 O_O --pack-plugin 打包成 .zip，最后告诉我 .zip 的路径，我会在 O_O 控制台 → 插件 → 添加插件 里安装。";
}
