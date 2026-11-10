using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Island.Plugins;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Primitives;
using ArcBadge = TKCode.Arc.Badge;
using ArcButton = TKCode.Arc.Button;
using ArcProgress = TKCode.Arc.Progress;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Plugins;

/// <summary>
/// Turns plugin <see cref="VNode"/> trees into Arc controls (docs/design/03-plugin-system.md §6). Only whitelisted
/// node types and props are honoured; colours, sizes and spacing come from tokens, never from the plugin.
/// Rendering is a keyed diff: a node in the same slot (type and key) as last time updates its control in place, so
/// Arc's own transitions keep running across re-renders instead of restarting.
/// </summary>
public sealed class VNodeRenderer(Action<int, string?> dispatch)
{
    // The node each control was last rendered from. Handler ids change on every render, so events read them from here
    // at the moment they fire.
    private readonly ConditionalWeakTable<Control, VNode> _nodes = new();

    // Set while props are being applied, so property changes made by the renderer are not reported as user input.
    private bool _applying;

    /// <summary>Renders <paramref name="node"/>, reusing <paramref name="existing"/> when it can be updated in place.</summary>
    public Control? Render(VNode? node, Control? existing)
    {
        if (node is null)
        {
            return null;
        }
        if (existing is not null && _nodes.TryGetValue(existing, out var old) && old.IsSameSlot(node))
        {
            Apply(existing, node);
            return existing;
        }
        var control = Create(node);
        Apply(control, node);
        return control;
    }

    private Control Create(VNode node) => node.Type switch
    {
        "stack" => new Grid(),
        "row" => new Grid(),
        "grid" => new Grid(),
        "item" => Wire(new Island.UI.Controls.PressableRow(), static (r, p) => p.Click += () => r.Fire(p, "onClick", null)),
        "card" => Wire(new Island.UI.Controls.PressableRow { IsCard = true }, static (r, p) => p.Click += () => r.Fire(p, "onClick", null)),
        "wrap" => new WrapPanel { Orientation = Orientation.Horizontal },
        "input" => WireInput(node.GetString("kind") == "search" ? new SearchField() : new TKCode.Arc.Input()),
        "image" => new Image { Stretch = Stretch.UniformToFill },
        "spacer" => new Border(),
        "text" => new TextBlock(),
        "icon" => new LucideIcon(),
        "button" => Wire(new ArcButton(), static (r, b) => b.Click += (s, _) => r.Fire((Control)s!, "onClick", null)),
        "switch" => Wire(new ArcSwitch(), static (r, s) => s.IsCheckedChanged += (c, _) =>
        {
            if (!r._applying)
            {
                r.Fire((Control)c!, "onChange", ((ArcSwitch)c!).IsChecked == true ? "true" : "false");
            }
        }),
        "progress" => new ArcProgress(),
        // Any Arc control from the registry (ui.arc): created by name, only listed properties settable.
        "arc" => CreateArc(node),
        "badge" => new ArcBadge(),
        "mascot" => new MascotHost(),
        "gauge" => new Gauge(),
        "counter" => new AnimatedCounter(),
        "dots" => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 },
        "logo" => new Avalonia.Controls.Shapes.Path { Stretch = Stretch.Uniform },
        // Arc's roll swap: when the child changes identity the old content leaves and the new one rolls in.
        "swap" => new ArcContentSwap
        {
            Mode = ContentSwapMode.Roll,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
            MeasuresExiting = true,
        },
        "iconButton" => Wire(new Island.UI.Controls.GlyphButton(), static (r, b) => b.Click += () => r.Fire(b, "onClick", null)),
        "tabs" => Wire(new Island.UI.Controls.TextTabs(), static (r, t) => t.Picked += value =>
            r.Fire(t, "onChange", System.Text.Json.JsonSerializer.Serialize(value, UiJson.Default.String))),
        "picker" => new DropdownMenu(),
        "segmented" => Wire(new SegmentedControl(), static (r, s) => s.SelectionChanged += (c, _) =>
        {
            if (!r._applying && ((SegmentedControl)c!).SelectedValue is string value)
            {
                r.Fire((Control)c!, "onChange", System.Text.Json.JsonSerializer.Serialize(value, UiJson.Default.String));
            }
        }),
        _ => new TextBlock(),
    };

    private Control CreateArc(VNode node)
    {
        if (ArcRegistry.Find(node.GetString("type")) is not { } spec)
        {
            return new TextBlock { Text = $"（未知组件 {node.GetString("type")}）" };
        }
        var control = spec.Create();
        ArcRegistry.WireChange(control, spec, json => Fire(control, "onChange", json), () => _applying);
        return control;
    }

    private T Wire<T>(T control, Action<VNodeRenderer, T> wire)
    {
        wire(this, control);
        return control;
    }

    private void Fire(Control control, string handler, string? payloadJson)
    {
        if (_nodes.TryGetValue(control, out var node) && node.Handlers.TryGetValue(handler, out var id))
        {
            dispatch(id, payloadJson);
        }
    }

    private void Apply(Control control, VNode node)
    {
        _nodes.AddOrUpdate(control, node);
        // Fixed widths, for columns that must line up (any node; in DIPs, kept within reason).
        if (node.GetNumber("width") is { } width)
        {
            control.Width = Math.Clamp(width, 16, 720);
        }
        else if (node.Type != "gauge")
        {
            control.ClearValue(Layoutable.WidthProperty);
        }
        _applying = true;
        try
        {
            switch (control, node.Type)
            {
                case (Grid grid, "grid"):
                    ApplyGrid(grid, node);
                    break;
                case (Grid grid, "stack" or "row"):
                    ApplyLayout(grid, node, horizontal: node.Type == "row");
                    break;
                case (Island.UI.Controls.PressableRow card, "card"):
                    ApplyLayout(card.Row, node, horizontal: false);
                    card.DragFiles = node.GetString("dragFiles") is { Length: > 0 } cardFiles
                        ? [.. cardFiles.Split('\n').Where(Path.IsPathFullyQualified)]
                        : null;
                    break;
                case (TextBox box, "input"):
                    ApplyInput(box, node);
                    break;
                case (WrapPanel wrap, "wrap"):
                    ApplyWrap(wrap, node);
                    break;
                case (Image image, "image"):
                    ApplyImage(image, node);
                    break;
                case (Island.UI.Controls.PressableRow item, "item"):
                    ApplyLayout(item.Row, node, horizontal: true);
                    item.DragFiles = node.GetString("dragFiles") is { Length: > 0 } dragFiles
                        ? [.. dragFiles.Split('\n').Where(Path.IsPathFullyQualified)]
                        : null;
                    break;
                case (TextBlock text, "text"):
                    ApplyText(text, node);
                    break;
                case (TextBlock text, _):
                    // Unknown node types render as a visible marker rather than silently disappearing.
                    text.Text = $"[{node.Type}?]";
                    text.Foreground = Island("IslandSecondaryBrush");
                    break;
                case (LucideIcon icon, _):
                    ApplyIcon(icon, node);
                    break;
                case (Island.UI.Controls.GlyphButton glyph, _):
                    glyph.Kind = ParseEnum(node.GetString("icon"), LucideIconKind.CircleHelp);
                    glyph.Label = node.GetString("label");
                    glyph.Enabled = node.GetBool("disabled") != true;
                    glyph.Small = node.GetString("size") == "small";
                    break;
                case (ArcButton button, _):
                    button.Content = node.GetString("label");
                    button.Variant = ParseEnum(node.GetString("variant"), ButtonVariant.Secondary);
                    button.Size = ParseEnum(node.GetString("size"), ControlSize.Small);
                    button.IsEnabled = node.GetBool("disabled") != true;
                    break;
                case (ArcSwitch toggle, _):
                    toggle.IsChecked = node.GetBool("checked") == true;
                    toggle.Content = node.GetString("label");
                    break;
                case (ArcProgress progress, _):
                    progress.Maximum = node.GetNumber("max") ?? 100;
                    progress.Value = node.GetNumber("value") ?? 0;
                    progress.Label = node.GetString("label");
                    progress.IsValueVisible = node.GetBool("showValue") == true;
                    break;
                case (ArcBadge badge, _):
                    badge.Content = node.GetString("text");
                    badge.Tone = ParseEnum(node.GetString("tone"), BadgeTone.Neutral);
                    badge.Size = node.GetBool("small") == true ? BadgeSize.Small : BadgeSize.Medium;
                    break;
                case (Gauge gauge, _):
                    ApplyGauge(gauge, node);
                    break;
                case (AnimatedCounter counter, _):
                    counter.Value = node.GetNumber("value") ?? 0;
                    counter.Prefix = node.GetString("prefix");
                    counter.Suffix = node.GetString("suffix");
                    counter.Decimals = (int)Math.Clamp(node.GetNumber("decimals") ?? 0, 0, 6);
                    counter.Label = node.GetString("label");
                    ArcTokens.Bind(counter, Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty, TypeToken(node.GetString("size") ?? "3xl"));
                    break;
                case (_, "arc"):
                    if (ArcRegistry.Find(node.GetString("type")) is { } arc && control.GetType() == arc.Type)
                    {
                        ArcRegistry.Apply(control, arc, node);
                    }
                    break;
                case (SegmentedControl segmented, _):
                    ApplySegmented(segmented, node);
                    break;
                case (Island.UI.Controls.TextTabs tabs, _):
                    tabs.SetItems(Options(node));
                    tabs.Selected = node.GetString("value");
                    break;
                case (DropdownMenu picker, _):
                    ApplyPicker(picker, node);
                    break;
                case (StackPanel dots, "dots"):
                    ApplyDots(dots, node);
                    break;
                case (ArcContentSwap swap, "swap"):
                    ApplySwap(swap, node);
                    break;
                case (Avalonia.Controls.Shapes.Path logo, "logo"):
                    // Only the logos the host knows; anything else draws nothing rather than a guess.
                    logo.Data = BrandLogos.Get(node.GetString("name"));
                    logo.Fill = Island("IslandForegroundBrush");
                    logo.Width = logo.Height = Math.Clamp(node.GetNumber("size") ?? 16, 8, 64);
                    break;
                case (MascotHost mascot, _):
                    // The island's mascot is the O_O mascot the user chose (MascotStyle). A plugin always gets a
                    // definite status, so the mascot never plays a showcase loop.
                    mascot.Status = ParseEnum(node.GetString("status"), AiMascotStatus.Idle);
                    mascot.Size = Math.Clamp(node.GetNumber("size") ?? 24, 24, 160);
                    mascot.Wander = true; // at rest it does a random action now and then; a busy status is left alone
                    break;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyLayout(Grid grid, VNode node, bool horizontal)
    {
        var gap = GapToken(node.GetString("gap"));
        if (horizontal)
        {
            ArcTokens.Bind(grid, Grid.ColumnSpacingProperty, gap);
        }
        else
        {
            ArcTokens.Bind(grid, Grid.RowSpacingProperty, gap);
        }

        var (old, next) = ReconcileChildren(grid, node);

        // Spacers take the free space; everything else sizes to content. Rows centre their children vertically.
        // Spacers, and children marked grow (e.g. a text that should be cut with "…" rather than push others out),
        // share the remaining space.
        var definitions = string.Join(",", node.Children.Select(c => c.Type == "spacer" || c.GetBool("grow") == true ? "*" : "Auto"));
        if (horizontal)
        {
            if (grid.ColumnDefinitions.ToString() != definitions)
            {
                grid.ColumnDefinitions = new ColumnDefinitions(definitions.Length == 0 ? "*" : definitions);
            }
            grid.RowDefinitions.Clear();
        }
        else
        {
            if (grid.RowDefinitions.ToString() != definitions)
            {
                grid.RowDefinitions = new RowDefinitions(definitions.Length == 0 ? "*" : definitions);
            }
            grid.ColumnDefinitions.Clear();
        }
        var align = node.GetString("align");
        for (var i = 0; i < next.Count; i++)
        {
            var c = next[i];
            if (horizontal)
            {
                Grid.SetColumn(c, i);
                Grid.SetRow(c, 0);
                c.VerticalAlignment = ParseVertical(align);
            }
            else
            {
                Grid.SetRow(c, i);
                Grid.SetColumn(c, 0);
                // A page indicator is centred under the content whatever the stack's alignment.
                c.HorizontalAlignment = node.Children[i].Type == "dots" ? HorizontalAlignment.Center : ParseHorizontal(align);
            }
        }
        if (!old.SequenceEqual(next))
        {
            grid.Children.Clear();
            grid.Children.AddRange(next);
        }
    }

    /// <summary>Renders the node's children into controls, reusing existing ones (keyed by key, others by position and type).</summary>
    private (List<Control> Old, List<Control> Next) ReconcileChildren(Grid grid, VNode node)
    {
        // Reconcile children: keyed children match by key, the others by position and type.
        var old = grid.Children.ToList();
        var byKey = new Dictionary<string, Control>(StringComparer.Ordinal);
        foreach (var c in old)
        {
            if (_nodes.TryGetValue(c, out var n) && n.Key is not null)
            {
                byKey.TryAdd(n.Key, c);
            }
        }
        var next = new List<Control>(node.Children.Count);
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            Control? candidate = child.Key is not null
                ? byKey.GetValueOrDefault(child.Key)
                : i < old.Count && _nodes.TryGetValue(old[i], out var n) && n.Key is null ? old[i] : null;
            next.Add(Render(child, candidate)!);
        }
        return (old, next);
    }

    /// <summary>A grid of equal columns (<c>columns</c>, 1–6): children fill it row by row; a child with <c>span</c> takes several columns.</summary>
    private void ApplyGrid(Grid grid, VNode node)
    {
        var columns = (int)Math.Clamp(node.GetNumber("columns") ?? 2, 1, 6);
        var gap = GapToken(node.GetString("gap"));
        ArcTokens.Bind(grid, Grid.ColumnSpacingProperty, gap);
        ArcTokens.Bind(grid, Grid.RowSpacingProperty, gap);
        var (old, next) = ReconcileChildren(grid, node);

        var placements = new List<(int Row, int Column, int Span)>();
        int row = 0, col = 0;
        foreach (var child in node.Children)
        {
            var span = (int)Math.Clamp(child.GetNumber("span") ?? 1, 1, columns);
            if (col + span > columns)
            {
                row++;
                col = 0;
            }
            placements.Add((row, col, span));
            col += span;
            if (col >= columns)
            {
                row++;
                col = 0;
            }
        }
        var rows = placements.Count == 0 ? 1 : placements.Max(p => p.Row) + 1;
        var columnDefinitions = string.Join(",", Enumerable.Repeat("*", columns));
        var rowDefinitions = string.Join(",", Enumerable.Repeat("Auto", rows));
        if (grid.ColumnDefinitions.ToString() != columnDefinitions)
        {
            grid.ColumnDefinitions = new ColumnDefinitions(columnDefinitions);
        }
        if (grid.RowDefinitions.ToString() != rowDefinitions)
        {
            grid.RowDefinitions = new RowDefinitions(rowDefinitions);
        }
        var align = node.GetString("align");
        for (var i = 0; i < next.Count; i++)
        {
            Grid.SetRow(next[i], placements[i].Row);
            Grid.SetColumn(next[i], placements[i].Column);
            Grid.SetColumnSpan(next[i], placements[i].Span);
            next[i].VerticalAlignment = ParseVertical(align);
            next[i].HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        if (!old.SequenceEqual(next))
        {
            grid.Children.Clear();
            grid.Children.AddRange(next);
        }
    }

    private static void ApplyText(TextBlock text, VNode node)
    {
        text.Text = node.GetString("text");
        var size = node.GetString("size") ?? "sm";
        var display = size is "xl" or "2xl" or "3xl" or "4xl";
        ArcTokens.Bind(text, TextBlock.FontSizeProperty, size switch
        {
            "xs" => ArcDesignTokenKind.TypeXS,
            "base" => ArcDesignTokenKind.TypeBase,
            "lg" => ArcDesignTokenKind.TypeLG,
            "xl" => ArcDesignTokenKind.TypeXL,
            "2xl" => ArcDesignTokenKind.Type2XL,
            "3xl" => ArcDesignTokenKind.Type3XL,
            "4xl" => ArcDesignTokenKind.Type4XL,
            _ => ArcDesignTokenKind.TypeSM,
        });
        ArcTokens.Bind(text, TextBlock.FontFamilyProperty,
            display ? ArcDesignTokenKind.DisplayFontFamily : ArcDesignTokenKind.BodyFontFamily);
        text.FontWeight = node.GetString("weight") switch
        {
            "medium" => FontWeight.Medium,
            "semibold" => FontWeight.SemiBold,
            "bold" => FontWeight.Bold,
            _ => display ? FontWeight.Medium : FontWeight.Normal,
        };
        text.FontFeatures = node.GetBool("tabular") == true ? FontFeatureCollection.Parse("+tnum") : null;
        // lines: wrap, but at most this many lines, the last one cut with "…" (file names on a tile).
        var lines = (int)Math.Clamp(node.GetNumber("lines") ?? 0, 0, 6);
        text.TextWrapping = node.GetBool("wrap") == true || lines > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        text.MaxLines = lines;
        text.TextTrimming = node.GetBool("wrap") == true && lines == 0 ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        text.TextAlignment = node.GetString("align") == "center" ? TextAlignment.Center : TextAlignment.Left;
        ApplyTone(text, TextBlock.ForegroundProperty, node.GetString("tone"));
        // An explicit colour (#RRGGBB), e.g. a calendar's own colour on its marker; anything else is ignored.
        var color = node.GetString("color");
        if (color is { Length: 7 } && color[0] == '#' && Color.TryParse(color, out var parsed))
        {
            text.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(parsed));
        }
    }

    /// <summary>
    /// A 270° ring (Arc Gauge) for a value against a limit. Optional bands: from <c>warnAt</c> the ring turns to the
    /// warning colour, from <c>dangerAt</c> to the danger colour; below them it uses the accent.
    /// </summary>
    private static void ApplyGauge(Gauge gauge, VNode node)
    {
        var size = Math.Clamp(node.GetNumber("size") ?? 104, 64, 200);
        gauge.Width = size; // the label and detail sit under the ring, so the height is left to the gauge
        gauge.Maximum = node.GetNumber("max") ?? 100;
        gauge.Value = Math.Clamp(node.GetNumber("value") ?? 0, 0, gauge.Maximum);
        gauge.Label = node.GetString("label");
        gauge.Detail = node.GetString("detail");
        gauge.Thresholds.Clear();
        if (node.GetNumber("warnAt") is { } warn)
        {
            gauge.Thresholds.Add(new GaugeThreshold { From = 0, Tone = GaugeTone.Accent });
            gauge.Thresholds.Add(new GaugeThreshold { From = warn, Tone = GaugeTone.Warning });
        }
        if (node.GetNumber("dangerAt") is { } danger)
        {
            gauge.Thresholds.Add(new GaugeThreshold { From = danger, Tone = GaugeTone.Danger });
        }
    }

    /// <summary>
    /// Tabs (Arc SegmentedControl). Children are <c>option</c> nodes (value, label); <c>value</c> is the selected one;
    /// <c>onChange</c> receives the value the user picked. Options are rebuilt only when they change, so the selection
    /// glides instead of jumping.
    /// </summary>
    private static void ApplySegmented(SegmentedControl segmented, VNode node)
    {
        var options = node.Children.Where(c => c.Type == "option")
            .Select(c => (Value: c.GetString("value") ?? "", Label: c.GetString("label") ?? c.GetString("value") ?? ""))
            .ToList();
        var existing = segmented.Items.OfType<SegmentedOption>().Select(o => (Value: o.Value as string ?? "", Label: o.Label ?? "")).ToList();
        if (!existing.SequenceEqual(options))
        {
            segmented.Items.Clear();
            foreach (var (value, label) in options)
            {
                segmented.Items.Add(new SegmentedOption { Value = value, Label = label });
            }
        }
        segmented.SelectedValue = node.GetString("value");
        segmented.HorizontalAlignment = node.GetString("align") == "center" ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    private static List<(string Value, string Label)> Options(VNode node) =>
        [.. node.Children.Where(c => c.Type == "option")
            .Select(c => (Value: c.GetString("value") ?? "", Label: c.GetString("label") ?? c.GetString("value") ?? ""))];

    /// <summary>
    /// A compact choice behind a trigger that shows the current one (Arc DropdownMenu with radio rows): for lists that
    /// need not be on screen all the time, such as the accounts of a provider.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DropdownMenu, string> s_pickerSignatures = [];

    /// <summary>
    /// A text box on the island. It takes the keyboard only when clicked (<see cref="IslandFocus"/>): the user's app
    /// keeps it otherwise. onChange gets every edit, onSubmit the text on Enter; Esc gives the keyboard back.
    /// </summary>
    private TextBox WireInput(TextBox box)
    {
        box.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => IslandFocus.Begin(box),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        box.GotFocus += (_, _) =>
        {
            if (!IslandFocus.IsEditing)
            {
                IslandFocus.Begin(box);
            }
        };
        box.TextChanged += (_, _) =>
        {
            if (!_applying)
            {
                Fire(box, "onChange", System.Text.Json.JsonSerializer.Serialize(box.Text ?? "", UiJson.Default.String));
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Fire(box, "onSubmit", System.Text.Json.JsonSerializer.Serialize(box.Text ?? "", UiJson.Default.String));
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                IslandFocus.End(restore: true);
                e.Handled = true;
            }
        };
        return box;
    }

    private static void ApplyInput(TextBox box, VNode node)
    {
        box.PlaceholderText = node.GetString("placeholder");
        box.MaxLength = (int)Math.Clamp(node.GetNumber("maxLength") ?? 500, 1, 10_000);
        // The plugin's value wins only while the user is not typing here, so re-renders never move the caret.
        if (!box.IsFocused && node.GetString("value") is { } value && value != box.Text)
        {
            box.Text = value;
        }
        if (node.GetBool("clear") == true && !box.IsFocused)
        {
            box.Text = "";
        }
    }

    /// <summary>Children flow left to right and onto the next line when the island is too narrow (cards).</summary>
    private void ApplyWrap(WrapPanel wrap, VNode node)
    {
        var gap = node.GetString("gap") switch { "xs" => 4.0, "sm" => 8.0, "lg" => 16.0, _ => 12.0 };
        wrap.ItemSpacing = gap;
        wrap.LineSpacing = gap;
        var old = wrap.Children.ToList();
        var byKey = new Dictionary<string, Control>(StringComparer.Ordinal);
        foreach (var c in old)
        {
            if (_nodes.TryGetValue(c, out var n) && n.Key is not null)
            {
                byKey.TryAdd(n.Key, c);
            }
        }
        var next = new List<Control>(node.Children.Count);
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            Control? candidate = child.Key is not null
                ? byKey.GetValueOrDefault(child.Key)
                : i < old.Count && _nodes.TryGetValue(old[i], out var n) && n.Key is null ? old[i] : null;
            next.Add(Render(child, candidate)!);
        }
        if (!old.SequenceEqual(next))
        {
            wrap.Children.Clear();
            wrap.Children.AddRange(next);
        }
    }

    // Thumbnails of local pictures (file shelf): decoded off the UI thread, small, cached by path and change time.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<Avalonia.Media.Imaging.Bitmap?>> s_thumbnails = new();
    private const long MaxImageBytes = 40L * 1024 * 1024;

    /// <summary>
    /// A local picture (<c>path</c>), shown cropped to <c>width</c>×<c>height</c> with rounded corners. Only shows it to
    /// the user — the plugin never gets the pixels — so a missing or unreadable file just shows nothing.
    /// </summary>
    private static void ApplyImage(Image image, VNode node)
    {
        var width = Math.Clamp(node.GetNumber("width") ?? 64, 16, 320);
        var height = Math.Clamp(node.GetNumber("height") ?? width, 16, 320);
        image.Width = width;
        image.Height = height;
        image.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        var path = node.GetString("path");
        if (path is null || !Path.IsPathFullyQualified(path) || !ImageExtensions.Contains(Path.GetExtension(path)))
        {
            image.Source = null;
            return;
        }
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            image.Source = null;
            return;
        }
        if (!info.Exists || info.Length > MaxImageBytes)
        {
            image.Source = null;
            return;
        }
        var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{(int)(width * 2)}";
        if (Equals(image.Tag, key))
        {
            return;
        }
        image.Tag = key;
        var pixels = (int)(Math.Max(width, height) * 2); // sharp on Retina
        var task = s_thumbnails.GetOrAdd(key, _ => Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                return (Avalonia.Media.Imaging.Bitmap?)Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, pixels);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }));
        task.ContinueWith(t =>
        {
            if (Equals(image.Tag, key))
            {
                image.Source = t.Result;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyPicker(DropdownMenu picker, VNode node)
    {
        var options = Options(node);
        var selected = node.GetString("value");
        var label = options.FirstOrDefault(o => o.Value == selected).Label ?? node.GetString("label") ?? "";
        // Plugins re-render often (the focus timer every second): rebuilding the trigger and the menu each time made
        // the picker flicker. Rebuild only when what it shows changes. The rows stay valid: they look up the current
        // onChange handler when clicked.
        var signature = string.Join("\n", options.Select(o => o.Value + "\t" + o.Label)) + "\n" + selected + "\n" + label;
        if (picker.Trigger is ArcButton && s_pickerSignatures.TryGetValue(picker, out var shown) && shown == signature)
        {
            return;
        }
        s_pickerSignatures.AddOrUpdate(picker, signature);
        picker.Label = label;
        // A quiet trigger: the current choice as text with a chevron, no box (the island is the only container).
        if (picker.Trigger is not ArcButton trigger)
        {
            trigger = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Padding = new Thickness(8, 0) };
            trigger.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, trigger.GetResourceObservable("IslandSecondaryBrush"));
            picker.Trigger = trigger;
        }
        trigger.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                new LucideIcon { Kind = LucideIconKind.ChevronDown, Size = 14, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        picker.Items.Clear();
        foreach (var (value, optionLabel) in options)
        {
            var entry = new MenuEntry
            {
                Header = optionLabel,
                ToggleType = MenuEntryToggleType.Radio,
                GroupName = "picker",
                IsChecked = value == selected,
            };
            entry.Click += (_, _) => Fire(picker, "onChange", System.Text.Json.JsonSerializer.Serialize(value, UiJson.Default.String));
            picker.Items.Add(entry);
        }
    }

    /// <summary>
    /// A swap (Arc ArcContentSwap) shows its one child. A new <c>id</c> gives the child a fresh control, which is what
    /// makes the swap animate; the same <c>id</c> updates the child in place, so nothing flickers on a refresh.
    /// </summary>
    private void ApplySwap(ArcContentSwap swap, VNode node)
    {
        var id = node.GetString("id");
        var child = node.Children.FirstOrDefault();
        swap.Direction = node.GetNumber("direction") is < 0 ? -1 : 1;
        var changed = !Equals(swap.Tag, id);
        var control = Render(child, changed ? null : swap.Content as Control);
        swap.Tag = id;
        if (!ReferenceEquals(control, swap.Content))
        {
            swap.Content = control;
        }
    }

    /// <summary>A page indicator: <c>count</c> dots, the one at <c>index</c> in the accent colour.</summary>
    private static void ApplyDots(StackPanel dots, VNode node)
    {
        var count = (int)Math.Clamp(node.GetNumber("count") ?? 0, 0, 20);
        var index = (int)(node.GetNumber("index") ?? 0);
        while (dots.Children.Count > count)
        {
            dots.Children.RemoveAt(dots.Children.Count - 1);
        }
        while (dots.Children.Count < count)
        {
            dots.Children.Add(new Avalonia.Controls.Shapes.Ellipse { VerticalAlignment = VerticalAlignment.Center });
        }
        for (var i = 0; i < count; i++)
        {
            // Round dots: the current page a little larger and in the accent colour.
            var dot = (Avalonia.Controls.Shapes.Ellipse)dots.Children[i];
            dot.Width = dot.Height = i == index ? 8 : 6;
            ArcTokens.Bind(dot, Avalonia.Controls.Shapes.Shape.FillProperty,
                i == index ? ArcDesignTokenKind.Accent : ArcDesignTokenKind.TextMuted);
        }
        dots.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private static ArcDesignTokenKind TypeToken(string size) => size switch
    {
        "xs" => ArcDesignTokenKind.TypeXS,
        "base" => ArcDesignTokenKind.TypeBase,
        "lg" => ArcDesignTokenKind.TypeLG,
        "xl" => ArcDesignTokenKind.TypeXL,
        "2xl" => ArcDesignTokenKind.Type2XL,
        "3xl" => ArcDesignTokenKind.Type3XL,
        "4xl" => ArcDesignTokenKind.Type4XL,
        _ => ArcDesignTokenKind.TypeSM,
    };

    private static void ApplyIcon(LucideIcon icon, VNode node)
    {
        // Icons named in data need every Lucide icon compiled in (LucideIconsIncludeAll in the project file).
        icon.Kind = ParseEnum(node.GetString("name"), LucideIconKind.CircleHelp);
        icon.Size = Math.Clamp(node.GetNumber("size") ?? 16, 8, 64);
        ApplyTone(icon, LucideIcon.ForegroundProperty, node.GetString("tone"));
    }

    private static void ApplyTone(Control control, AvaloniaProperty foreground, string? tone)
    {
        if (tone == "accent")
        {
            ArcTokens.Bind(control, foreground, ArcDesignTokenKind.Accent);
            return;
        }
        control.Bind(foreground, control.GetResourceObservable(
            tone == "secondary" ? "IslandSecondaryBrush" : "IslandForegroundBrush"));
    }

    private static IBrush? Island(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

    private static ArcDesignTokenKind GapToken(string? gap) => gap switch
    {
        "xs" => ArcDesignTokenKind.Space1,
        "md" => ArcDesignTokenKind.Space3,
        "lg" => ArcDesignTokenKind.Space4,
        "xl" => ArcDesignTokenKind.Space6,
        _ => ArcDesignTokenKind.Space2,
    };

    private static VerticalAlignment ParseVertical(string? align) => align switch
    {
        "start" => VerticalAlignment.Top,
        "end" => VerticalAlignment.Bottom,
        "stretch" => VerticalAlignment.Stretch,
        _ => VerticalAlignment.Center,
    };

    private static HorizontalAlignment ParseHorizontal(string? align) => align switch
    {
        "center" => HorizontalAlignment.Center,
        "end" => HorizontalAlignment.Right,
        "stretch" => HorizontalAlignment.Stretch,
        _ => HorizontalAlignment.Left,
    };

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        value is not null && Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class UiJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
