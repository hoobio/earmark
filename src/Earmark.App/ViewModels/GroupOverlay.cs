using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Controls;

namespace Earmark.App.ViewModels;

/// <summary>
/// Positions one group's editable title in the overlay layer above the device grid. The title sits
/// in the reserved band directly above the group's first row segment. One per group, reused across
/// layout passes so an in-progress title edit keeps focus. The dotted outline is drawn separately
/// per row segment (see <see cref="GroupOutlineSegment"/>).
/// </summary>
public partial class GroupOverlay : ObservableObject
{
    public GroupOverlay(DeviceGroupInfo group) => Group = group;

    /// <summary>The group whose title this chrome edits.</summary>
    public DeviceGroupInfo Group { get; }

    /// <summary>Left edge of the group's first row segment.</summary>
    [ObservableProperty]
    public partial double X { get; set; }

    /// <summary>Top of the title band (first segment's top minus <see cref="BandHeight"/>).</summary>
    [ObservableProperty]
    public partial double Y { get; set; }

    /// <summary>Width of the group's first row segment.</summary>
    [ObservableProperty]
    public partial double Width { get; set; }

    /// <summary>True while the title is being edited: the read-only label swaps to a text box. The
    /// label is the group's drag handle, so editing is entered by double-tap / context menu instead
    /// of a single click.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    public bool ShowLabel => !IsEditing;

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(ShowLabel));

    public double BandHeight { get; } = WrapByRowLayout.TitleBandHeight;
}
