namespace AudioRelay;

internal sealed record MasterVolumeSnapshot(string? EndpointId, float Gain, bool IsAvailable)
{
    internal static MasterVolumeSnapshot Unavailable { get; } = new(null, 0, false);

    internal static MasterVolumeSnapshot FromEndpoint(string id, float decibels, float fullScaleDecibels, bool mute)
    {
        if (!float.IsFinite(decibels) || !float.IsFinite(fullScaleDecibels)) return new(id, 0, false);
        // Windows slider values are audio-tapered, not linear PCM gains.
        // Normalize to the selected endpoint's own 100%, without amplification.
        float gain = mute ? 0 : MathF.Pow(10, MathF.Min(0, decibels - fullScaleDecibels) / 20);
        return new(id, gain, true);
    }

    internal float GainFor(string peerId)
    {
        // If an active output becomes the Windows default, Windows itself
        // already applies the user's volume and mute to that output.
        return StringComparer.OrdinalIgnoreCase.Equals(peerId, EndpointId) ? 1 : Gain;
    }
}
