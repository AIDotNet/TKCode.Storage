using Jaunt;

namespace Island.Plugins.Host;

/// <summary>
/// Module resolution for one plugin (docs/design/03-plugin-system.md §5.4): <c>@island/api</c> is the host's virtual
/// module; relative specifiers resolve inside the plugin folder and never outside it; bare package names are refused
/// (plugins bundle their dependencies).
/// </summary>
internal sealed class PluginModuleResolver(string pluginDirectory) : IJsModuleResolver
{
    public const string ApiSpecifier = "@island/api";

    private readonly string _root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;

    /// <summary>
    /// The compiled <c>@island/api</c>. Every engine must get this very object: Jaunt binds a module id to one compiled
    /// template per engine, and the host pre-loads it before the plugin's own import resolves to it.
    /// </summary>
    public static JsModule ApiModule => Api.Value;

    // Compiled bytecode is immutable and can be shared by every plugin engine.
    private static readonly Lazy<JsModule> Api = new(() =>
        JsCompiler.CompileModule(EmbeddedRuntime.Read("api.js"), new JsCompileOptions { SourceName = ApiSpecifier }, default));

    public JsModuleSource? Resolve(string specifier, string? referrerId) => Resolve(specifier, referrerId, null);

    public JsModuleSource? Resolve(string specifier, string? referrerId, IReadOnlyDictionary<string, string>? attributes)
    {
        if (specifier == ApiSpecifier)
        {
            return new JsModuleSource(ApiSpecifier, Api.Value);
        }
        if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot import '{specifier}': plugins may import '{ApiSpecifier}' and their own files only; bundle other packages.");
        }
        var baseDir = referrerId is not null && referrerId.StartsWith(_root, StringComparison.Ordinal)
            ? Path.GetDirectoryName(referrerId)!
            : _root;
        var path = Path.GetFullPath(Path.Combine(baseDir, specifier));
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot import '{specifier}': it is outside the plugin folder.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Cannot import '{specifier}': file not found.", path);
        }
        var text = File.ReadAllText(path);
        var options = new JsCompileOptions { SourceName = path };
        var type = attributes is not null && attributes.TryGetValue("type", out var t) ? t : null;
        var module = type switch
        {
            "json" => JsCompiler.CompileJsonModule(text, options),
            "text" => JsCompiler.CreateTextModule(text, options),
            null => JsCompiler.CompileModule(text, options, default),
            _ => throw new InvalidOperationException($"Unsupported import type '{type}'."),
        };
        return new JsModuleSource(path, module);
    }
}

/// <summary>The JS files the host injects into every plugin realm, embedded in this assembly.</summary>
internal static class EmbeddedRuntime
{
    public static string Read(string name)
    {
        var assembly = typeof(EmbeddedRuntime).Assembly;
        using var stream = assembly.GetManifestResourceStream($"Island.Plugins.Host.Runtime.{name}")
                           ?? throw new InvalidOperationException($"Missing embedded runtime file '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
