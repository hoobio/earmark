using System.Diagnostics.CodeAnalysis;

namespace Earmark.App.ViewModels;

/// <summary>
/// Capped LIFO of reversible Devices-page actions. Entries reference a device by id (not by
/// <see cref="DeviceCard"/> instance) so the undo trail survives rebuilds of the card list.
/// When the cap is hit the oldest entry is dropped in O(1) via <see cref="LinkedList{T}"/>.
/// </summary>
internal sealed class DeviceUndoStack
{
    private const int Limit = 32;
    private readonly LinkedList<UndoAction> _entries = new();

    /// <summary>Base type for reversible actions. Each variant carries the prior state.</summary>
    public abstract record UndoAction(string DeviceId);

    public sealed record VisibilityUndo(string DeviceId, bool PrevHidden, bool PrevPinned)
        : UndoAction(DeviceId);

    public sealed record VolumeMuteUndo(string DeviceId, float PrevVolume, bool PrevMuted)
        : UndoAction(DeviceId);

    /// <summary>Snapshot of the whole Devices layout (block order, groups, hidden apps) captured
    /// before a structural change - a chip hide, a block / member reorder, or any group create / join /
    /// leave / disband. Undo restores the three lists wholesale and resyncs, which is far simpler than
    /// reversing each operation and handles them all uniformly. Not keyed to a single device, so its
    /// <see cref="UndoAction.DeviceId"/> is empty.</summary>
    public sealed record LayoutUndo(
        List<string> Order,
        List<GroupSnapshot> Groups,
        List<HiddenAppSnapshot> HiddenApps)
        : UndoAction(string.Empty);

    public sealed record GroupSnapshot(string Id, string Title, List<string> MemberIds);

    public sealed record HiddenAppSnapshot(string Key, string? Name);

    public void PushVisibility(string deviceId, bool prevHidden, bool prevPinned) =>
        Push(new VisibilityUndo(deviceId, prevHidden, prevPinned));

    public void PushVolumeMute(string deviceId, float prevVolume, bool prevMuted) =>
        Push(new VolumeMuteUndo(deviceId, prevVolume, prevMuted));

    public void PushLayout(List<string> order, List<GroupSnapshot> groups, List<HiddenAppSnapshot> hiddenApps) =>
        Push(new LayoutUndo(order, groups, hiddenApps));

    private void Push(UndoAction action)
    {
        _entries.AddLast(action);
        if (_entries.Count > Limit)
        {
            _entries.RemoveFirst();
        }
    }

    public bool TryPop([NotNullWhen(true)] out UndoAction? action)
    {
        if (_entries.Last is null)
        {
            action = null;
            return false;
        }
        action = _entries.Last.Value;
        _entries.RemoveLast();
        return true;
    }
}
