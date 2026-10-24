using Earmark.Core.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed partial class DevicesViewModel
{
    public bool CanUndo => _undo.Count > 0;

    public void Undo()
    {
        if (_undo.TryPop(out var restore))
        {
            restore();
            Raise(nameof(CanUndo));
        }
    }

    public void RecordVolumeUndo(DeviceCardViewModel card, double volume, bool muted)
    {
        if (Math.Abs(card.Volume - volume) >= 0.5 || card.IsMuted != muted)
        {
            Push(() => card.SetVolumeAndMute(volume, muted));
        }
    }

    public void ToggleMute(DeviceCardViewModel card)
    {
        if (!card.IsMuteEditable)
        {
            return;
        }

        var (volume, muted) = (card.Volume, card.IsMuted);
        card.SetMuted(!card.IsMuted, write: true);
        RecordVolumeUndo(card, volume, muted);
    }

    public void ToggleVisibility(DeviceCardViewModel card)
    {
        var before = DevicesViewModel.Clone(card.Config);
        var cfg = DevicesViewModel.Clone(card.Config);
        var hide = !card.IsEffectivelyHidden;
        cfg.Hidden = hide ? true : null;
        cfg.Pinned = hide ? null : true;
        if (hide)
        {
            cfg.PinnedToQuickControls = null;
            RemoveFromGroupRecord(card.Key);
        }

        Push(() => UpdateConfig(card, before));
        UpdateConfig(card, cfg);
    }

    public void ToggleQuickPin(DeviceCardViewModel card)
    {
        if (!card.CanQuickPin)
        {
            return;
        }

        var cfg = DevicesViewModel.Clone(card.Config);
        cfg.PinnedToQuickControls = card.IsQuickPinned ? null : true;
        if (cfg.PinnedToQuickControls == true)
        {
            cfg.Hidden = null;
        }

        UpdateConfig(card, cfg);
    }

    public void UpdateConfig(DeviceCardViewModel card, DeviceConfig cfg)
    {
        card.ApplyConfig(cfg);
        SaveConfig(card);
        QueueSave();
        SyncBlocks();
        QueueApps();
    }

    public void ForgetDevice(DeviceCardViewModel card)
    {
        if (card.IsConnected)
        {
            return;
        }

        Settings.KnownDevices.RemoveAll(d => SameId(d.Key, card.Key));
        PurgeKey(card.Key);
        _allCards.Remove(card);
        _logger.LogInformation("Forgot device: {Key} '{Name}'", card.Key, card.Endpoint.FriendlyName);
        QueueSave();
        SyncBlocks();
    }

    public DeviceGroupViewModel? GroupViewOf(DeviceCardViewModel card) =>
        GroupOf(card.Key) is { } g && _groups.TryGetValue(g.Id, out var view) ? view : null;

    public int GroupMemberCount(string groupId) => Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, groupId))?.MemberIds.Count ?? 0;

    public void RenameGroup(string groupId, string title)
    {
        if (Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, groupId)) is { } record)
        {
            PushLayoutUndo();
            record.Title = title;
            QueueSave();
        }
    }

    public void ReorderBlock(string blockId, string? beforeId)
    {
        PushLayoutUndo();
        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => SameId(id, blockId));
        var at = beforeId is null ? -1 : full.FindIndex(id => SameId(id, beforeId));
        full.Insert(at < 0 ? full.Count : at, blockId);
        CommitOrder(full);
    }

    public void CreateGroup(string sourceKey, string targetKey)
    {
        if (SameId(sourceKey, targetKey) || GroupOf(sourceKey) is not null || GroupOf(targetKey) is not null)
        {
            return;
        }

        PushLayoutUndo();
        var full = ComputeOrderedBlockIds(out _);
        var group = new DeviceGroup { Id = Guid.NewGuid().ToString("N"), Title = "New group", MemberIds = [targetKey, sourceKey] };
        Settings.DeviceGroups.Add(group);
        var at = full.FindIndex(id => SameId(id, targetKey));
        if (at >= 0)
        {
            full[at] = group.Id;
        }
        else
        {
            full.Add(group.Id);
        }

        full.RemoveAll(id => SameId(id, sourceKey));
        CommitOrder(full);
    }

    public void JoinGroup(string sourceKey, string groupId, string? beforeKey)
    {
        if (Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, groupId)) is not { } target)
        {
            return;
        }

        PushLayoutUndo();
        var source = GroupOf(sourceKey);
        if (source is not null && !ReferenceEquals(source, target))
        {
            RemoveFromGroupRecord(sourceKey);
        }

        target.MemberIds.RemoveAll(id => SameId(id, sourceKey));
        var at = beforeKey is null ? -1 : target.MemberIds.FindIndex(id => SameId(id, beforeKey));
        target.MemberIds.Insert(at < 0 ? target.MemberIds.Count : at, sourceKey);
        CommitOrder(ComputeOrderedBlockIds(out _));
    }

    public void LeaveGroup(string key, string? beforeBlockId)
    {
        if (GroupOf(key) is null)
        {
            ReorderBlock(key, beforeBlockId);
            return;
        }

        PushLayoutUndo();
        RemoveFromGroupRecord(key);
        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => SameId(id, key));
        var at = beforeBlockId is null ? -1 : full.FindIndex(id => SameId(id, beforeBlockId));
        full.Insert(at < 0 ? full.Count : at, key);
        CommitOrder(full);
    }

    public void UngroupDevice(DeviceCardViewModel card)
    {
        if (GroupOf(card.Key) is not { } group)
        {
            return;
        }

        PushLayoutUndo();
        var remaining = group.MemberIds.Where(id => !SameId(id, card.Key)).ToList();
        var after = remaining.Count >= 2 ? group.Id : remaining.LastOrDefault();
        RemoveFromGroupRecord(card.Key);
        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => SameId(id, card.Key));
        var at = after is null ? -1 : full.FindIndex(id => SameId(id, after));
        full.Insert(at < 0 ? full.Count : at + 1, card.Key);
        CommitOrder(full);
    }

    public void DeleteGroup(string groupId)
    {
        if (Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, groupId)) is not { } group)
        {
            return;
        }

        PushLayoutUndo();
        Settings.DeviceGroups.Remove(group);
        var at = Settings.DeviceOrder.FindIndex(id => SameId(id, groupId));
        if (at >= 0)
        {
            Settings.DeviceOrder.RemoveAt(at);
            Settings.DeviceOrder.InsertRange(at, group.MemberIds);
        }
        else
        {
            Settings.DeviceOrder.AddRange(group.MemberIds);
        }

        QueueSave();
        SyncBlocks();
    }

    public bool DropWouldDisband(DeviceCardViewModel source, DeviceCardViewModel target, int region)
    {
        var from = GroupOf(source.Key);
        var to = region == 0 && GroupOf(target.Key) is null ? null : GroupOf(target.Key);
        return from is not null && !ReferenceEquals(from, to) && from.MemberIds.Count <= 2;
    }

    public void DropCard(DeviceCardViewModel source, DeviceCardViewModel target, int region)
    {
        if (source == target || LockLayout)
        {
            return;
        }

        var targetGroup = GroupOf(target.Key);
        if (targetGroup is not null)
        {
            var at = targetGroup.MemberIds.FindIndex(id => SameId(id, target.Key));
            var anchor = region < 0 ? target.Key : at + 1 < targetGroup.MemberIds.Count ? targetGroup.MemberIds[at + 1] : null;
            JoinGroup(source.Key, targetGroup.Id, anchor == source.Key ? null : anchor);
        }
        else if (region == 0)
        {
            if (GroupOf(source.Key) is not null)
            {
                RemoveFromGroupRecord(source.Key);
            }

            CreateGroup(source.Key, target.Key);
        }
        else
        {
            LeaveGroup(source.Key, region < 0 ? target.Key : NextBlockAfter(target.Key, source.Key));
        }
    }

    public void DropGroup(string groupId, DeviceCardViewModel target, int region)
    {
        if (LockLayout || GroupOf(target.Key) is not null)
        {
            return;
        }

        ReorderBlock(groupId, region < 0 ? target.Key : NextBlockAfter(target.Key, groupId));
    }

    public void DropOnGroupHeader(DeviceCardViewModel source, string groupId)
    {
        if (!LockLayout)
        {
            JoinGroup(source.Key, groupId, null);
        }
    }

    public void DropGroupOnGroup(string groupId, string targetGroupId)
    {
        if (!LockLayout && !SameId(groupId, targetGroupId))
        {
            ReorderBlock(groupId, targetGroupId);
        }
    }

    private string? NextBlockAfter(string blockId, string excluding)
    {
        var full = ComputeOrderedBlockIds(out _).Where(id => !SameId(id, excluding)).ToList();
        var at = full.FindIndex(id => SameId(id, blockId));
        return at >= 0 && at + 1 < full.Count ? full[at + 1] : null;
    }

    private void CommitOrder(List<string> order)
    {
        Settings.DeviceOrder = order;
        QueueSave();
        SyncBlocks();
    }

    private void PushLayoutUndo()
    {
        var order = Settings.DeviceOrder.ToList();
        var groups = Settings.DeviceGroups.Select(g => new DeviceGroup { Id = g.Id, Title = g.Title, MemberIds = [.. g.MemberIds] }).ToList();
        var hidden = Settings.HiddenApps.Select(h => new HiddenApp { Key = h.Key, Name = h.Name }).ToList();
        var hiddenOnDevice = Settings.HiddenAppsOnDevice.Select(h => new HiddenAppOnDevice { Key = h.Key, EndpointId = h.EndpointId, Name = h.Name, DeviceName = h.DeviceName }).ToList();
        Push(() =>
        {
            Settings.DeviceOrder = order;
            Settings.DeviceGroups = groups;
            Settings.HiddenApps = hidden;
            Settings.HiddenAppsOnDevice = hiddenOnDevice;
            RefreshHiddenApps();
            _appsSignature = AppsSignature();
            QueueSave();
            SyncBlocks();
            QueueApps();
        });
    }

    private void Push(Action restore)
    {
        _undo.Push(restore);
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            keep.ForEach(_undo.Push);
        }

        Raise(nameof(CanUndo));
    }
}
