using CommunityToolkit.Mvvm.ComponentModel;

namespace Earmark.App.ViewModels;

/// <summary>
/// View-model for a device group's chrome: the editable title and the dedicated-row flag. One per
/// group, owned by <see cref="HomeViewModel"/> and reused across card rebuilds so an in-progress
/// title edit survives. User edits raise the change callback so the owner can persist them; a
/// <see cref="SyncFrom"/> from the persisted record does not.
/// </summary>
public partial class DeviceGroupInfo : ObservableObject
{
    private readonly Action<DeviceGroupInfo>? _onChanged;
    private bool _suppressChanged;

    public DeviceGroupInfo(string id, string title, bool dedicatedRow, Action<DeviceGroupInfo>? onChanged)
    {
        Id = id;
        _suppressChanged = true;
        Title = title;
        DedicatedRow = dedicatedRow;
        _suppressChanged = false;
        _onChanged = onChanged;
    }

    /// <summary>Stable group id (matches <c>DeviceGroup.Id</c> and the cards' <c>GroupId</c>).</summary>
    public string Id { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool DedicatedRow { get; set; }

    /// <summary>Refreshes from the persisted record without firing the change callback (used when
    /// reconciling existing group infos on a card rebuild).</summary>
    public void SyncFrom(string title, bool dedicatedRow)
    {
        _suppressChanged = true;
        Title = title;
        DedicatedRow = dedicatedRow;
        _suppressChanged = false;
    }

    partial void OnTitleChanged(string value)
    {
        if (!_suppressChanged) _onChanged?.Invoke(this);
    }

    partial void OnDedicatedRowChanged(bool value)
    {
        if (!_suppressChanged) _onChanged?.Invoke(this);
    }
}
