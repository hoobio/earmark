using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Controls;

using Microsoft.UI.Xaml;

namespace Earmark.App.ViewModels;

/// <summary>
/// A group block on the Devices page: one atomic container that owns its member cards. Replaces the
/// flat model's per-card <c>GroupId</c> + overlay chrome - membership is now structural (a card is
/// in this group iff it's in <see cref="Members"/>). Reused across rebuilds so an in-progress title
/// edit and the members' instances survive. Title / dedicated-row edits raise the change callback so
/// <see cref="HomeViewModel"/> persists them; a <see cref="SyncFrom"/> from the persisted record does
/// not.
/// </summary>
public partial class DeviceGroupCard : ObservableObject, IBlockLayoutInfo
{
    private readonly Action<DeviceGroupCard>? _onChanged;
    private bool _suppressChanged;

    public DeviceGroupCard(string id, string title, Action<DeviceGroupCard>? onChanged)
    {
        Id = id;
        _suppressChanged = true;
        Title = title;
        _suppressChanged = false;
        _onChanged = onChanged;
    }

    /// <summary>Stable group id (matches <c>DeviceGroup.Id</c> and the group's slot in the block order).</summary>
    public string Id { get; }

    /// <summary>The member cards, in left-to-right member order. Mutated in place (Remove/Insert,
    /// never Move - <see cref="WrapByRowLayout"/> ignores Move) so instances are preserved.</summary>
    public ObservableCollection<DeviceCard> Members { get; } = new();

    // ---- IBlockLayoutInfo (top-level block placement) ----

    /// <summary>A group is a full-width section on its own row band; the nested layout wraps the
    /// members across that width at the shared column width.</summary>
    int IBlockLayoutInfo.ColumnSpan(int availableColumns) => availableColumns;

    /// <summary>Always true: a group section forces a row break before and after, so ungrouped cards
    /// never share its row (they bump to the rows above / below).</summary>
    bool IBlockLayoutInfo.BreaksRow => true;

    /// <summary>A group section keeps its own height (title + member rows); it never stretches to a
    /// lone card's row baseline.</summary>
    bool IBlockLayoutInfo.StretchToRowHeight => false;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool IsQuickPinned { get; set; }

    public string QuickPinToggleLabel => IsQuickPinned ? "Unpin from Quick Controls" : "Pin to Quick Controls";
    public string QuickPinToggleGlyph => IsQuickPinned ? new string((char)0xE840, 1) : new string((char)0xE718, 1);

    [ObservableProperty]
    public partial bool IsPointerOver { get; set; }

    public bool ShowQuickPinAffordance => IsPointerOver && !IsEditingTitle;

    /// <summary>True while the title is being edited (double-tap / Rename): the read-only label swaps
    /// to a text box. The label is the group's drag handle, so editing is entered explicitly.</summary>
    [ObservableProperty]
    public partial bool IsEditingTitle { get; set; }

    public bool ShowTitleLabel => !IsEditingTitle;

    partial void OnIsEditingTitleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTitleLabel));
        OnPropertyChanged(nameof(ShowQuickPinAffordance));
    }

    partial void OnIsPointerOverChanged(bool value) => OnPropertyChanged(nameof(ShowQuickPinAffordance));

    /// <summary>Shows the container's dotted outline. The page flips this on every group while a drag
    /// is in flight, so groups read as transparent at rest and reveal their bounds only while dragging.</summary>
    [ObservableProperty]
    public partial bool ShowOutline { get; set; }

    /// <summary>True while this group is the current join drop target (accent outline instead of the
    /// neutral dotted one). Only one of the two outlines is ever shown.</summary>
    [ObservableProperty]
    public partial bool IsJoinTarget { get; set; }

    /// <summary>Invisible while the whole group is the reorder drag source (its slot is the drop gap).</summary>
    [ObservableProperty]
    public partial bool IsBeingDragged { get; set; }

    public double Opacity => IsBeingDragged ? 0.0 : 1.0;

    partial void OnIsBeingDraggedChanged(bool value) => OnPropertyChanged(nameof(Opacity));

    public bool ShowNeutralOutline => ShowOutline && !IsJoinTarget;

    /// <summary>Inset applied to the members while a drag is in flight, so the dotted outline (drawn
    /// at the group-box bounds) has breathing room around the cards instead of hugging them. Left /
    /// right / bottom only - the title band already supplies the top gap. Zero at rest.</summary>
    public Thickness ContentPadding => ShowOutline ? new Thickness(8, 0, 8, 8) : new Thickness(0);

    partial void OnShowOutlineChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNeutralOutline));
        OnPropertyChanged(nameof(ContentPadding));
    }

    partial void OnIsJoinTargetChanged(bool value) => OnPropertyChanged(nameof(ShowNeutralOutline));

    /// <summary>Refreshes the title from the persisted record without firing the change callback
    /// (used when reconciling existing group cards on a rebuild).</summary>
    public void SyncFrom(string title, bool isQuickPinned)
    {
        _suppressChanged = true;
        Title = title;
        IsQuickPinned = isQuickPinned;
        _suppressChanged = false;
    }

    partial void OnTitleChanged(string value)
    {
        if (!_suppressChanged) _onChanged?.Invoke(this);
    }

    partial void OnIsQuickPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(QuickPinToggleLabel));
        OnPropertyChanged(nameof(QuickPinToggleGlyph));
        if (!_suppressChanged) _onChanged?.Invoke(this);
    }
}
