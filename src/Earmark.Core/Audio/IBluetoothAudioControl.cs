namespace Earmark.Core.Audio;

/// <summary>
/// Connects / disconnects a Bluetooth audio device the same way Windows' Quick Settings does: via
/// the kernel-streaming one-shot reconnect / disconnect property on the endpoint's Bluetooth KS
/// filter (<c>KSPROPSETID_BtAudio</c> / <c>KSPROPERTY_ONESHOT_RECONNECT|DISCONNECT</c>).
/// <para>
/// The request is <b>fire-and-attempt</b>: a success return means the driver tried, not that the
/// link is up. The card's connected state must come from the device-arrival/removal events
/// (<c>IMMNotificationClient</c> -&gt; <c>EndpointsChanged</c>), never from the call's return.
/// </para>
/// </summary>
public interface IBluetoothAudioControl
{
    /// <summary>Whether this render endpoint belongs to a Bluetooth device (one of its topology
    /// connectors terminates in a Bluetooth KS filter). Drives the connect/disconnect affordance.
    /// Cheap after the first call per endpoint (cached). Never throws.</summary>
    bool IsBluetooth(string endpointId);

    /// <summary>Asks the driver to (re)connect the Bluetooth device behind this endpoint. No-op if
    /// the endpoint isn't Bluetooth. Safe to call from any thread; does its own COM work.</summary>
    void RequestConnect(string endpointId);

    /// <summary>Asks the driver to disconnect the Bluetooth device behind this endpoint. No-op if the
    /// endpoint isn't Bluetooth.</summary>
    void RequestDisconnect(string endpointId);
}
