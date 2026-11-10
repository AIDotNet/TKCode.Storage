using Avalonia;
using Island.UI.Plugins;

namespace Island.UI.Tests;

public sealed class ArcRegistryTests
{
    [Fact]
    public void Every_listed_property_exists_is_writable_and_takes_a_plain_value()
    {
        foreach (var spec in ArcRegistry.All)
        {
            Assert.True(spec.Type.GetConstructor(Type.EmptyTypes) is not null, $"{spec.Name} needs a public parameterless constructor");
            foreach (var name in spec.Props)
            {
                var info = ArcRegistry.FindProperty(spec.Type, name);
                Assert.True(info is { CanWrite: true }, $"{spec.Name}.{name} is missing or read-only");
                var t = Nullable.GetUnderlyingType(info!.PropertyType) ?? info.PropertyType;
                var plain = t == typeof(string) || t == typeof(bool) || t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(DateOnly);
                Assert.True(plain, $"{spec.Name}.{name} is {t.Name}, not a plain value");
            }
            if (spec.ValueProp is { } vp)
            {
                Assert.Contains(vp, spec.Props);
                Assert.NotNull(AvaloniaPropertyRegistry.Instance.GetRegistered(spec.Type).FirstOrDefault(p => p.Name == vp));
            }
        }
    }

    [Fact]
    public void Plain_values_convert_to_property_types()
    {
        Assert.True(ArcRegistry.TryConvert("2026-11-10", typeof(DateOnly?), out var d));
        Assert.Equal(new DateOnly(2026, 11, 10), d);
        Assert.True(ArcRegistry.TryConvert(3.0, typeof(int), out var n));
        Assert.Equal(3, n);
        Assert.True(ArcRegistry.TryConvert("success", typeof(DayOfWeek).Assembly.GetType("System.DayOfWeek")!, out _) == false);
        Assert.True(ArcRegistry.TryConvert("Monday", typeof(DayOfWeek), out var w));
        Assert.Equal(DayOfWeek.Monday, w);
        Assert.False(ArcRegistry.TryConvert("x", typeof(bool), out _));
        Assert.Equal("isOpen", ArcRegistry.CamelCase("IsOpen"));
    }
}
