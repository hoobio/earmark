using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// Reusable accent-colour picker: the bright Fluent accent swatch grid (see
/// <see cref="DeviceAccentPalette"/>) with a check on the selected swatch, plus a "Custom…" button
/// whose flyout holds a <see cref="ColorPicker"/> ring/hex for off-palette colours, and an optional
/// "None" button. Shared by the device customisation dialog and the Settings peak-meter picker so
/// the two don't duplicate picker markup.
/// </summary>
public sealed partial class ColourSwatchPicker : UserControl
{
    // 25px so nine swatches (8 gaps of 6) fill the 280-wide picker, matching the glyph grid span.
    private const double SwatchSize = 25;
    private readonly List<(Button Button, FontIcon Check, Color Colour)> _swatches = new();
    private bool _suppressWheel;

    public ColourSwatchPicker()
    {
        InitializeComponent();
        BuildSwatches();
        RefreshSelectionVisual();
    }

    /// <summary>The selected colour. Null highlights no swatch. Two-way by convention.</summary>
    public static readonly DependencyProperty SelectedColourProperty = DependencyProperty.Register(
        nameof(SelectedColour), typeof(Color?), typeof(ColourSwatchPicker),
        new PropertyMetadata(null, OnSelectionChanged));

    public Color? SelectedColour
    {
        get => (Color?)GetValue(SelectedColourProperty);
        set => SetValue(SelectedColourProperty, value);
    }

    /// <summary>True when "None" is the active choice (drives the None button's selected outline).</summary>
    public static readonly DependencyProperty IsNoneSelectedProperty = DependencyProperty.Register(
        nameof(IsNoneSelected), typeof(bool), typeof(ColourSwatchPicker),
        new PropertyMetadata(false, OnSelectionChanged));

    public bool IsNoneSelected
    {
        get => (bool)GetValue(IsNoneSelectedProperty);
        set => SetValue(IsNoneSelectedProperty, value);
    }

    /// <summary>Whether the "None" button is offered (the device picker; off for Settings).</summary>
    public static readonly DependencyProperty ShowNoneProperty = DependencyProperty.Register(
        nameof(ShowNone), typeof(bool), typeof(ColourSwatchPicker),
        new PropertyMetadata(false, (d, e) => ((ColourSwatchPicker)d).NoneButton.Visibility =
            (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public bool ShowNone
    {
        get => (bool)GetValue(ShowNoneProperty);
        set => SetValue(ShowNoneProperty, value);
    }

    /// <summary>Raised when the user clicks "None". The host applies the no-accent state.</summary>
    public event EventHandler? NoneRequested;

    private void BuildSwatches()
    {
        foreach (var colour in DeviceAccentPalette.Swatches)
        {
            var check = new FontIcon
            {
                Glyph = "", // CheckMark
                FontSize = 14,
                Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(ContrastOn(colour)),
            };
            var content = new Grid
            {
                Children =
                {
                    new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(colour),
                    },
                    check,
                },
            };
            var button = new Button
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = content,
                Tag = colour,
            };
            button.Click += OnSwatchClicked;
            SwatchHost.Children.Add(button);
            _swatches.Add((button, check, colour));
        }
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColourSwatchPicker)d;
        if (picker.SelectedColour is Color c)
        {
            picker._suppressWheel = true;
            picker.WheelPicker.Color = c;
            picker._suppressWheel = false;
        }
        picker.RefreshSelectionVisual();
    }

    private void RefreshSelectionVisual()
    {
        var selected = IsNoneSelected ? (Color?)null : SelectedColour;
        foreach (var (_, check, colour) in _swatches)
        {
            check.Visibility = selected is Color c && c == colour ? Visibility.Visible : Visibility.Collapsed;
        }

        // Outline the None button while it's the active choice.
        NoneButton.BorderBrush = IsNoneSelected
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        NoneButton.BorderThickness = new Thickness(IsNoneSelected ? 2 : 1);
    }

    private void OnSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Color colour })
        {
            IsNoneSelected = false;
            SelectedColour = colour;
        }
    }

    private void OnNoneClicked(object sender, RoutedEventArgs e)
    {
        IsNoneSelected = true;
        NoneRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnWheelColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressWheel) return;
        IsNoneSelected = false;
        SelectedColour = args.NewColor;
    }

    // Rec. 601 luma: dark check on bright swatches, white on dark ones.
    private static Color ContrastOn(Color c)
    {
        var luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
        return luma >= 150 ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A) : Colors.White;
    }
}
