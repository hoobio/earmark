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

    public void PushVisibility(string deviceId, bool prevHidden, bool prevPinned) =>
        Push(new VisibilityUndo(deviceId, prevHidden, prevPinned));

    public void PushVolumeMute(string deviceId, float prevVolume, bool prevMuted) =>
        Push(new VolumeMuteUndo(deviceId, prevVolume, prevMuted));

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
