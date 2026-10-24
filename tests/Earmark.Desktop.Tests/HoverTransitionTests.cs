using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Desktop.Controls;
using Earmark.Desktop.ViewModels.Rules;
using Earmark.Desktop.Views;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Tests;

public sealed class HoverTransitionTests(AvaloniaSession session)
{
    private static readonly Color Ground = Color.Parse("#FF181C26");
    private static readonly string[] Items = ["One", "Two"];
    private static readonly string[] Segments = ["All", "Render", "Capture"];
    private static readonly string[] Pages = ["Rules", "Sessions", "Settings"];

    [Fact]
    public void No_hover_or_press_fade_passes_through_a_lighter_colour() => session.Run(() =>
    {
        using var services = session.NewServices();
        var flashes = new SortedSet<string>(StringComparer.Ordinal);
        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1280;
        window.Height = 820;
        window.Show();
        Pump();

        window.Navigate("Devices");
        AvaloniaSession.WaitFor(() => Has<Button>(window, "RuleChip") && Has<Button>(window, "Tile") && Has<Button>(window, "Transport") && Has<Border>(window, "Chip") && Has<DeviceGroupView>(window, null), "the Devices page renders sample cards, a group, app chips, rule chips and now playing");
        Probe(window, flashes);

        var options = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "OptionsButton");
        options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ProbeAndClosePopups(window, flashes);
        foreach (var target in new Control[] { window.GetVisualDescendants().OfType<DeviceCard>().First(), First<Border>(window, "Chip"), First<Button>(window, "RuleChip") })
        {
            target.RaiseEvent(new ContextRequestedEventArgs());
            ProbeAndClosePopups(window, flashes);
        }

        services.GetRequiredService<RulesViewModel>().Items.ToList().ForEach(row => row.IsExpanded = true);
        foreach (var page in Pages)
        {
            window.Navigate(page);
            Pump();
            Probe(window, flashes);
            foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>().Where(combo => combo.IsEffectivelyVisible).Take(2).ToList())
            {
                combo.IsDropDownOpen = true;
                Pump();
                ProbePopups(window, flashes);
                combo.IsDropDownOpen = false;
            }
        }

        var blocks = new StackPanel();
        blocks.Children.AddRange(services.GetRequiredKeyedService<Func<IReadOnlyList<Control>>>("QuickControlsBlocks")());
        var quick = new Window { Width = 420, Height = 900, Content = blocks };
        quick.Show();
        AvaloniaSession.WaitFor(() => Has<Button>(quick, "Tile"), "Quick Controls renders the pinned cards");
        Probe(quick, flashes);
        quick.Close();

        var gallery = new Window { Width = 800, Height = 1400, Content = new ScrollViewer { Content = Gallery() } };
        gallery.Show();
        Pump();
        Probe(gallery, flashes);
        var menuHost = gallery.GetVisualDescendants().OfType<Button>().First(button => button.Flyout is MenuFlyout);
        menuHost.Flyout!.ShowAt(menuHost);
        ProbeAndClosePopups(gallery, flashes);
        var contextHost = gallery.GetVisualDescendants().OfType<Border>().First(border => border.ContextMenu is not null);
        contextHost.ContextMenu!.Open(contextHost);
        ProbeAndClosePopups(gallery, flashes);
        var galleryCombo = gallery.GetVisualDescendants().OfType<ComboBox>().First();
        galleryCombo.IsDropDownOpen = true;
        Pump();
        ProbePopups(gallery, flashes);
        galleryCombo.IsDropDownOpen = false;
        gallery.Close();
        window.Hide();

        Assert.True(flashes.Count == 0, string.Join(Environment.NewLine, flashes));
    });

    private static bool Has<T>(Visual root, string? className) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Any(control => className is null || control.Classes.Contains(className));

    private static T First<T>(Visual root, string className) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => control.Classes.Contains(className));

    private static StackPanel Gallery()
    {
        Button Themed(string key) => new() { Content = key, Theme = (ControlTheme)Application.Current!.FindResource(key)! };

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Item" });
        menu.Items.Add(new MenuItem { Header = "Checked", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true });
        menu.Items.Add(new MenuItem { Header = "Parent", Items = { new MenuItem { Header = "Child" } } });
        var context = new ContextMenu();
        context.Items.Add(new MenuItem { Header = "Context item" });
        context.Items.Add(new MenuItem { Header = "Critical", Classes = { "Critical" } });
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Button { Content = "Default" },
                Themed("DefaultButtonStyle"),
                Themed("AccentButtonStyle"),
                Themed("CriticalButtonStyle"),
                Themed("CautionButtonStyle"),
                Themed("SubtleButtonStyle"),
                Themed("MenuRowButtonStyle"),
                new Button { Content = "Card icon", Classes = { "CardIcon" } },
                new Button { Content = "Transport", Classes = { "Transport" } },
                new Button { Content = "Play", Classes = { "Transport", "Play" } },
                new Button { Content = "Menu", Flyout = menu },
                new Border { Width = 40, Height = 20, Background = Brushes.Transparent, ContextMenu = context },
                new RepeatButton { Content = "Repeat" },
                new ToggleButton { Content = "Toggle" },
                new ToggleButton { Content = "Toggled", IsChecked = true },
                new ToggleButton { Content = "G", Classes = { "GlyphPick" } },
                new ToggleButton { Content = "S", Classes = { "Swatch" }, Background = new SolidColorBrush(Color.Parse("#FF409FFF")) },
                new ToggleButton { Content = "S", Classes = { "Swatch" }, IsChecked = true, Background = new SolidColorBrush(Color.Parse("#FFF28779")) },
                new HyperlinkButton { Content = "Link" },
                new RadioButton { Content = "Radio" },
                new RadioButton { Content = "Radio on", IsChecked = true, GroupName = "on" },
                new CheckBox { Content = "Check" },
                new CheckBox { Content = "Checked", IsChecked = true },
                new ToggleSwitch(),
                new ToggleSwitch { IsChecked = true },
                new ComboBox { ItemsSource = Items, SelectedIndex = 0 },
                new TextBox { Text = "Text" },
                new AutoCompleteBox { Text = "Auto" },
                new NumericUpDown { Value = 3 },
                new Slider { Classes = { "Volume" }, Value = 40, Maximum = 100 },
                new Slider { Value = 40, Maximum = 100 },
                new ListBox { ItemsSource = Items, SelectedIndex = 0 },
                new ListBox { ItemsSource = Segments, SelectedIndex = 0, Theme = (ControlTheme)Application.Current!.FindResource("SegmentedStyle")! },
                new SplitButton { Content = "Split" },
                new DropDownButton { Content = "Drop" },
                new FAInfoBar { IsOpen = true, IsClosable = true, Message = "Info" },
                new SettingsCard { Header = "Card", IsClickEnabled = true },
            },
        };
    }

    private static void ProbeAndClosePopups(TopLevel top, SortedSet<string> flashes)
    {
        Pump();
        Assert.NotEmpty(top.OpenedPopups);
        ProbePopups(top, flashes);
        foreach (var popup in top.OpenedPopups.ToList())
        {
            popup.Close();
        }

        Pump();
    }

    private static void ProbePopups(TopLevel top, SortedSet<string> flashes)
    {
        foreach (var popup in top.OpenedPopups)
        {
            if (popup.Child is { } child)
            {
                Probe(child, flashes);
            }
        }
    }

    private static void Probe(Visual root, SortedSet<string> flashes)
    {
        foreach (var control in root.GetSelfAndVisualDescendants().OfType<TemplatedControl>().Where(control => control.IsEffectivelyVisible).ToList())
        {
            var pseudo = (IPseudoClasses)control.Classes;
            var targets = Targets(control);
            pseudo.Add(":pointerover");
            targets = [.. targets.Union(Targets(control))];
            pseudo.Remove(":pointerover");
            if (targets.Count == 0)
            {
                continue;
            }

            var rest = Read(targets);
            pseudo.Add(":pointerover");
            var over = Read(targets);
            pseudo.Add(":pressed");
            var pressed = Read(targets);
            pseudo.Remove(":pressed");
            pseudo.Remove(":pointerover");

            for (var i = 0; i < targets.Count; i++)
            {
                var (element, property) = targets[i];
                var where = $"{control.GetType().Name}[{string.Join(' ', control.Classes)}] {element.GetType().Name}#{(element as StyledElement)?.Name}.{property.Name}";
                Check(flashes, where, "rest", rest[i], "pointerover", over[i]);
                Check(flashes, where, "pointerover", over[i], "pressed", pressed[i]);
                Check(flashes, where, "rest", rest[i], "pressed", pressed[i]);
            }
        }
    }

    private static List<(Animatable Element, AvaloniaProperty Property)> Targets(TemplatedControl control) =>
        [.. Subtree(control).SelectMany(element => (element.Transitions ?? []).OfType<BrushTransition>().Select(transition => (element, transition.Property!)))];

    private static IEnumerable<Animatable> Subtree(TemplatedControl control)
    {
        var pending = new Stack<Visual>([control]);
        while (pending.TryPop(out var visual))
        {
            if (visual is Animatable animatable)
            {
                yield return animatable;
            }

            foreach (var child in visual.GetVisualChildren())
            {
                if (child is not TemplatedControl)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static Color?[] Read(List<(Animatable Element, AvaloniaProperty Property)> targets) =>
        [.. targets.Select(target => (target.Element.GetBaseValue(target.Property) as ISolidColorBrush)?.Color)];

    private static void Check(SortedSet<string> flashes, string where, string fromState, Color? from, string toState, Color? to)
    {
        if (from is not { } a || to is not { } b || a == b)
        {
            return;
        }

        var ends = Math.Max(Luma(a), Luma(b));
        for (var step = 1; step < 20; step++)
        {
            if (Luma(Interpolate(step / 20d, a, b)) > ends + 2)
            {
                flashes.Add($"{where}: {fromState} {a} to {toState} {b}");
                return;
            }
        }
    }

    private static double Luma(Color color)
    {
        var alpha = color.A / 255d;
        double Over(byte channel, byte ground) => channel * alpha + ground * (1 - alpha);
        return 0.2126 * Over(color.R, Ground.R) + 0.7152 * Over(color.G, Ground.G) + 0.0722 * Over(color.B, Ground.B);
    }

    // Mirrors Avalonia's ColorAnimator: channels lerp in linear light with straight, not premultiplied, alpha.
    private static Color Interpolate(double progress, Color from, Color to)
    {
        static double Linear(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static byte Srgb(double linear) => (byte)Math.Round((linear <= 0.0031308 ? linear * 12.92 : Math.Pow(linear, 5d / 12d) * 1.055 - 0.055) * 255);
        byte Channel(byte a, byte b) => Srgb(Linear(a) + progress * (Linear(b) - Linear(a)));
        return new Color((byte)Math.Round(from.A + progress * (to.A - from.A)), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static void Pump()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
