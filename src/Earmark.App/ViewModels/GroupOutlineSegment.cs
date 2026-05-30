using CommunityToolkit.Mvvm.ComponentModel;

namespace Earmark.App.ViewModels;

/// <summary>
/// One row segment of a group's dotted outline. A single-row group has one; a wrapped group has one
/// per row so the outline hugs each row instead of a bounding box over empty cells. Position/size
/// are absolute layout (scroll-content) coordinates, updated in place by the page each layout pass.
/// </summary>
public partial class GroupOutlineSegment : ObservableObject
{
    public string GroupId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    [ObservableProperty]
    public partial double Width { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    /// <summary>Outline shown only while a drag is in progress.</summary>
    [ObservableProperty]
    public partial bool ShowOutline { get; set; }

    /// <summary>This segment's group is the current join drop target (accent outline).</summary>
    [ObservableProperty]
    public partial bool IsJoinTarget { get; set; }

    /// <summary>Normal (tertiary, thin) outline: shown while dragging but not the join target. Only
    /// one of normal / join is ever visible, so their dashes never overlap.</summary>
    public bool ShowNormalOutline => ShowOutline && !IsJoinTarget;

    partial void OnShowOutlineChanged(bool value) => OnPropertyChanged(nameof(ShowNormalOutline));
    partial void OnIsJoinTargetChanged(bool value) => OnPropertyChanged(nameof(ShowNormalOutline));
}
