using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioRelay;

/// <summary>
/// Read-only monitor of the Windows default playback volume. No COM calls
/// happen on the playback worker and no endpoint's volume/mute is changed.
/// </summary>
internal sealed class SystemMasterVolume : IDisposable
{
    private const int PollMs = 50;
    private const int RecheckDefaultMs = 300;
    private const int StaleAfterMs = 500;
    private readonly Thread worker;
    private int stopping;
    private long publishedAt = -StaleAfterMs;
    private MasterVolumeSnapshot snapshot = MasterVolumeSnapshot.Unavailable;

    internal SystemMasterVolume()
    {
        worker = new Thread(Monitor) { IsBackground = true, Name = "AudioRelay Windows master volume" };
        worker.Start();
    }

    internal MasterVolumeSnapshot Read() =>
        Environment.TickCount64 - Volatile.Read(ref publishedAt) <= StaleAfterMs
            ? Volatile.Read(ref snapshot)
            : MasterVolumeSnapshot.Unavailable;

    public void Dispose()
    {
        // Do not wait on audio service/driver calls from the playback worker.
        Interlocked.Exchange(ref stopping, 1);
    }

    private void Monitor()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? endpoint = null;
        string? endpointId = null;
        long recheckAt = 0;
        try
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                long now = Environment.TickCount64;
                try
                {
                    enumerator ??= new MMDeviceEnumerator();
                    if (endpoint == null || now >= recheckAt)
                    {
                        var candidate = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        try
                        {
                            var candidateId = candidate.ID;
                            if (!StringComparer.OrdinalIgnoreCase.Equals(candidateId, endpointId))
                            {
                                var previous = endpoint;
                                endpoint = candidate;
                                endpointId = candidateId;
                                candidate = previous;
                            }
                            recheckAt = now + RecheckDefaultMs;
                        }
                        finally { Release(candidate); }
                    }

                    var volume = endpoint!.AudioEndpointVolume;
                    var next = MasterVolumeSnapshot.FromEndpoint(
                        endpointId!, volume.MasterVolumeLevel, volume.VolumeRange.MaxDecibels,
                        volume.Mute || volume.MasterVolumeLevelScalar <= 0);
                    if (next != Volatile.Read(ref snapshot)) Volatile.Write(ref snapshot, next);
                    Volatile.Write(ref publishedAt, Environment.TickCount64);
                }
                catch (Exception error) when (IsAudioServiceError(error))
                {
                    // A restart or device switch must not make the mirrors jump
                    // to full volume. Mute the relay pending a fresh read.
                    Volatile.Write(ref snapshot, MasterVolumeSnapshot.Unavailable);
                    Release(endpoint);
                    endpoint = null;
                    endpointId = null;
                    Release(enumerator);
                    enumerator = null;
                    Thread.Sleep(RecheckDefaultMs);
                }
                Thread.Sleep(PollMs);
            }
        }
        finally
        {
            Release(endpoint);
            Release(enumerator);
        }
    }

    private static bool IsAudioServiceError(Exception error) =>
        error is COMException or InvalidComObjectException or InvalidOperationException or UnauthorizedAccessException;

    private static void Release(IDisposable? item)
    {
        try { item?.Dispose(); }
        catch (Exception error) when (IsAudioServiceError(error)) { /* Device disappeared during release. */ }
    }
}
