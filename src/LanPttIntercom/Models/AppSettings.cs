using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LanPttIntercom.Models;

/// <summary>
/// A user-saved remote endpoint (a peer on the LAN).
/// </summary>
public sealed class SavedEndpoint
{
    /// <summary>Stable identifier (GUID string). Used as a key in the UI list.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-editable display name / note for this peer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Target IPv4 address as a string, e.g. "192.168.1.20".</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Last time this entry was updated, in UTC ticks.</summary>
    public long UpdatedUtcTicks { get; set; } = DateTime.UtcNow.Ticks;
}

/// <summary>
/// Persisted user settings: saved endpoints + which one is the default + listen port.
/// </summary>
public sealed class AppSettings
{
    /// <summary>UDP port used to send and receive voice. Default 41000.</summary>
    public int ListenPort { get; set; } = 41000;

    /// <summary>Id of the endpoint auto-loaded after startup (null = none).</summary>
    public string? DefaultEndpointId { get; set; }

    /// <summary>Saved endpoints list.</summary>
    public List<SavedEndpoint> Endpoints { get; set; } = new();

    /// <summary>Audio configuration.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>UI / interaction preferences.</summary>
    public UiSettings Ui { get; set; } = new();
}

public sealed class AudioSettings
{
    public int SampleRate { get; set; } = 16000;
    public int BitsPerSample { get; set; } = 16;
    public int Channels { get; set; } = 1;

    /// <summary>Frame size in milliseconds. 20 ms is a good low-latency default for voice.</summary>
    public int FrameMilliseconds { get; set; } = 20;

    /// <summary>Selected input device id. -1 = system default.</summary>
    public int InputDeviceId { get; set; } = -1;

    /// <summary>Selected output device id. -1 = system default.</summary>
    public int OutputDeviceId { get; set; } = -1;

    [JsonIgnore]
    public int FrameSamples => SampleRate * FrameMilliseconds / 1000;

    [JsonIgnore]
    public int FrameBytes => FrameSamples * Channels * (BitsPerSample / 8);
}

public sealed class UiSettings
{
    /// <summary>Whether the PTT key (Space) is currently enabled. Can be toggled from UI.</summary>
    public bool PttKeyEnabled { get; set; } = true;

    /// <summary>Output volume 0..100.</summary>
    public int OutputVolume { get; set; } = 80;
}
