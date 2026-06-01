using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Earmark.App.Controls;

/// <summary>
/// Read-only picker dropdown for "Exact" match fields. The closed button shows the chosen value
/// (or placeholder); the flyout carries a search box and a filtered list of <see cref="Candidates"/>.
/// Keyboard focus lives in the flyout's search box, never in the field, so it can't lose focus
/// mid-keystroke the way an editable <c>ComboBox</c> does. Picking an item sets <see cref="Value"/>.
/// </summary>
public sealed partial class PickerComboBox : UserControl
{
    public PickerComboBox()
    {
        InitializeComponent();
    }

    /// <summary>The pool of choices (device / app / mix names). Filtered live by the search box.</summary>
    public static readonly DependencyProperty CandidatesProperty = DependencyProperty.Register(
        nameof(Candidates), typeof(IReadOnlyList<string>), typeof(PickerComboBox),
        new PropertyMetadata(null));

    public IReadOnlyList<string> Candidates
    {
        get => (IReadOnlyList<string>)GetValue(CandidatesProperty);
        set => SetValue(CandidatesProperty, value);
    }

    /// <summary>The selected value (the rule's pattern). Two-way by convention.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(PickerComboBox),
        new PropertyMetadata(string.Empty, OnValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Shown on the closed button when <see cref="Value"/> is empty.</summary>
    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(PickerComboBox),
        new PropertyMetadata("Pick…", OnValueChanged));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Button caption: the value, or the placeholder when nothing is chosen yet.</summary>
    public string DisplayText => string.IsNullOrEmpty(Value) ? PlaceholderText : Value;

    /// <summary>Dim the caption while it's showing the placeholder.</summary>
    public Brush DisplayBrush => (Brush)Application.Current.Resources[
        string.IsNullOrEmpty(Value) ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"];

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (PickerComboBox)d;
        picker.Bindings.Update();
    }

    private void OnFlyoutOpened(object? sender, object e)
    {
        Search.Text = string.Empty;
        ApplyFilter(string.Empty);
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter(sender.Text);
    }

    private void ApplyFilter(string? text)
    {
        var source = Candidates ?? Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            List.ItemsSource = source;
        }
        else
        {
            List.ItemsSource = source
                .Where(c => c.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        List.SelectedItem = Value;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string name)
        {
            Value = name;
            DropFlyout.Hide();
        }
    }
}
