using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioRelay;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record RelayTarget(string Id, int Volume = 100, int DelayMs = 0);

/// <summary>Lists active Windows render endpoints without opening audio streams or changing routing.</summary>
public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDevice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = endpoint.ID;
        }
        catch (COMException) { /* No multimedia default, e.g. all devices unplugged. */ }

        var found = new List<AudioDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                try
                {
                    var id = endpoint.ID;
                    found.Add(new(id, DeviceName(endpoint), StringComparer.OrdinalIgnoreCase.Equals(id, defaultId)));
                }
                catch (COMException) { /* The endpoint disappeared while enumerating. */ }
            }
        }
        return found.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string DeviceName(MMDevice endpoint)
    {
        var label = endpoint.FriendlyName;
        return string.IsNullOrWhiteSpace(label) || label == "Unknown" ? "未命名的播放设备" : label;
    }
}
