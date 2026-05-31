using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// Reusable accent-colour picker: the full Windows accent swatch grid (see
/// <see cref="DeviceAccentPalette"/>) plus a "Custom…" toggle that reveals a <see cref="ColorPicker"/>
/// ring/hex for off-palette colours. The wheel is hidden by default so the resting state is
/// swatches-only. Shared by the device customisation flyout and the Settings peak-meter colour
/// picker so the two don't duplicate picker markup.
///
/// <para><see cref="SelectedColour"/> is the chosen colour. When <see cref="AllowAuto"/> is set an
/// "Auto" button is offered; choosing it sets <see cref="SelectedColour"/> to null (the consumer
/// reads that as "fall back to the derived value").</para>
/// </summary>
public sealed partial class ColourSwatchPicker : UserControl
{
    private bool _suppressWheel;

    public ColourSwatchPicker()
    {
        InitializeComponent();
        SwatchHost.ItemsSource = DeviceAccentPalette.Swatches;
        UpdateAutoState();
    }

    /// <summary>The selected colour. Null means "Auto" (only reachable when <see cref="AllowAuto"/>
    /// is set). Two-way by convention.</summary>
    public static readonly DependencyProperty SelectedColourProperty = DependencyProperty.Register(
        nameof(SelectedColour), typeof(Color?), typeof(ColourSwatchPicker),
        new PropertyMetadata(null, OnSelectedColourChanged));

    public Color? SelectedColour
    {
        get => (Color?)GetValue(SelectedColourProperty);
        set => SetValue(SelectedColourProperty, value);
    }

    /// <summary>When true, an "Auto" choice is offered that clears the selection to null.</summary>
    public static readonly DependencyProperty AllowAutoProperty = DependencyProperty.Register(
        nameof(AllowAuto), typeof(bool), typeof(ColourSwatchPicker),
        new PropertyMetadata(false, (d, _) => ((ColourSwatchPicker)d).UpdateAutoState()));

    public bool AllowAuto
    {
        get => (bool)GetValue(AllowAutoProperty);
        set => SetValue(AllowAutoProperty, value);
    }

    private static void OnSelectedColourChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColourSwatchPicker)d;
        picker.UpdateAutoState();
        if (e.NewValue is Color c)
        {
            picker._suppressWheel = true;
            picker.WheelPicker.Color = c;
            picker._suppressWheel = false;
        }
    }

    private void UpdateAutoState()
    {
        // The "Auto" button only makes sense when Auto is allowed AND something is currently set;
        // tapping it clears back to Auto.
        AutoButton.Visibility = AllowAuto && SelectedColour is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Color colour })
        {
            SelectedColour = colour;
        }
    }

    private void OnAutoClicked(object sender, RoutedEventArgs e) => SelectedColour = null;

    private void OnCustomToggled(object sender, RoutedEventArgs e)
    {
        WheelPicker.Visibility = CustomToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWheelColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressWheel) return;
        SelectedColour = args.NewColor;
    }
}
