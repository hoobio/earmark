using Earmark.Core.Models;

using Windows.UI;

namespace Earmark.App.ViewModels;

/// <summary>
/// Immutable, UI-thread-free description of a device's current state, produced by
/// <c>HomeViewModel.BuildCards</c> on a background thread and applied to a <see cref="DeviceCard"/>
/// on the UI thread (new instance, or <see cref="DeviceCard.RefreshFrom"/> on a reused one). Keeping
/// the background pass to plain data is what lets surviving card instances be reused across a
/// connect/disconnect rebuild (so the block-slide animates) without touching their observable state
/// off-thread.
/// </summary>
/// <param name="DeviceKey">The stable <see cref="DeviceIdentity"/> key this card is reconciled by.</param>
/// <param name="IsConnected">False for a persisted-but-absent (disconnected) device.</param>
public sealed record DeviceCardSnapshot(
    AudioEndpoint Endpoint,
    string DeviceKey,
    bool IsConnected,
    float Volume,
    bool IsMuted,
    bool VolumeLocked,
    bool MuteLocked,
    bool? RuleMutedTarget,
    string? RuleMutedSource,
    string? RuleVolumeSource,
    IReadOnlyList<RuleSummary> Rules,
    bool IsHiddenByUser,
    bool IsPinnedByUser,
    bool IsVolumeControlsHiddenByUser,
    string? UserGlyphOverride,
    Color? UserAccent,
    bool UserAccentNone);
