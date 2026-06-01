namespace Earmark.Core.Models;

public enum EndpointFlow
{
    Render,
    Capture,
}

public enum EndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

/// <param name="ContainerId">
/// The device's <c>DEVPKEY_Device_ContainerId</c> (lower-case GUID, no braces), or null when the
/// driver exposes none. Stable across a driver reinstall / OS update (unlike <see cref="Id"/>), and
/// for Bluetooth devices it is derived from the MAC address, so it is the basis of the persistent
/// device identity (see <c>Earmark.Core.Models.DeviceIdentity</c>).
/// </param>
/// <param name="IsBluetooth">
/// True when the endpoint belongs to a Bluetooth audio device (its container advertises the
/// Bluetooth audio service / its bus type is Bluetooth). Drives the Bluetooth connect/disconnect
/// affordance; false for everything else.
/// </param>
public sealed record AudioEndpoint(
    string Id,
    string FriendlyName,
    string DeviceDescription,
    EndpointFlow Flow,
    EndpointState State,
    bool IsDefault,
    bool IsDefaultCommunications,
    string? ContainerId = null,
    bool IsBluetooth = false)
{
    public string DisplayName => string.IsNullOrEmpty(DeviceDescription)
        ? FriendlyName
        : $"{FriendlyName} ({DeviceDescription})";

    /// <summary>
    /// Name for pickers / labels that avoids the doubled suffix some drivers produce, where the
    /// friendly name already ends with "(description)" (e.g. Elgato virtual devices, whose friendly
    /// name is "Comms (Elgato Virtual Audio)" and description "Elgato Virtual Audio"). Falls back to
    /// <see cref="DisplayName"/> when the description adds new information (e.g. "Sony Headphones"
    /// + "4-WH-1000XM5" -> "Sony Headphones (4-WH-1000XM5)").
    /// </summary>
    public string PickerName => string.IsNullOrEmpty(DeviceDescription)
        || FriendlyName.Contains(DeviceDescription, StringComparison.OrdinalIgnoreCase)
        ? FriendlyName
        : $"{FriendlyName} ({DeviceDescription})";
}
