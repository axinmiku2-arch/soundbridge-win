namespace AudioRelay;

/// <summary>
/// A bounded stereo PCM timeline shared by scheduled outputs. Frame addresses
/// refer to the capture's absolute frame index, not to a device-local buffer.
/// Callers map each render frame's scheduled time to a source position.
/// </summary>
internal sealed class SynchronizedTimeline
{
    internal const long MaximumAbsoluteFrame = 1L << 50;
    private readonly object gate = new();
    private readonly float[] audio;
    private readonly long[] stamps;
    private long first, end;

    public SynchronizedTimeline(int sourceRate, int capacityMs = 2000)
    {
        if (sourceRate is < 8_000 or > 384_000)
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (capacityMs is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(capacityMs));

        SampleRate = sourceRate;
        CapacityFrames = Math.Max(2, (int)(((long)sourceRate * capacityMs + 999) / 1000));
        audio = new float[checked(CapacityFrames * 2)];
        stamps = new long[CapacityFrames];
        Array.Fill(stamps, long.MinValue);
    }

    public int SampleRate { get; }
    public int CapacityFrames { get; }
    public long EndFrame { get { lock (gate) return end; } }
    public long OldestFrame { get { lock (gate) return OldestUnsafe; } }

    private long OldestUnsafe => Math.Max(first, end - CapacityFrames);

    /// <summary>Discard and scrub all buffered audio, abandoning prior frame addresses.</summary>
    public void Reset(long atFrame = 0)
    {
        CheckFrame(atFrame, nameof(atFrame));
        lock (gate)
        {
            Array.Clear(audio);
            Array.Fill(stamps, long.MinValue);
            first = end = atFrame;
        }
    }

    /// <summary>
    /// Insert complete interleaved stereo frames. Future writes advance the history
    /// window. Gaps stay empty even on ring wrap; late data outside the window is ignored.
    /// </summary>
    public void Write(long firstFrame, ReadOnlySpan<float> stereo)
    {
        CheckFrame(firstFrame, nameof(firstFrame));
        if ((stereo.Length & 1) != 0)
            throw new ArgumentException("Stereo writes must contain whole frames.", nameof(stereo));
        int count = stereo.Length / 2;
        long writeEnd = firstFrame + count;
        CheckFrame(writeEnd, nameof(stereo));
        if (count == 0) return;
        lock (gate)
        {
            end = Math.Max(end, writeEnd);
            long start = Math.Max(firstFrame, OldestUnsafe);
            for (long frame = start; frame < writeEnd; frame++)
            {
                int target = Slot(frame);
                int input = checked((int)(frame - firstFrame) * 2);
                audio[target * 2] = FinitePcm(stereo[input]);
                audio[target * 2 + 1] = FinitePcm(stereo[input + 1]);
                stamps[target] = frame;
            }
        }
    }

    /// <summary>
    /// Resample at the supplied source position and slope. The full destination
    /// is written, with absent samples or unavailable interpolation endpoints
    /// replaced by silence. Returns the count of fully covered output frames.
    /// </summary>
    public int Read(double firstFrame, double sourceFramesPerOutputFrame,
        Span<float> destination, int channels, float gain = 1)
    {
        if (!double.IsFinite(firstFrame))
            throw new ArgumentOutOfRangeException(nameof(firstFrame));
        if (!double.IsFinite(sourceFramesPerOutputFrame) || sourceFramesPerOutputFrame <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFramesPerOutputFrame));
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (destination.Length % channels != 0)
            throw new ArgumentException("The output must contain whole frames.", nameof(destination));
        if (!float.IsFinite(gain) || gain is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(gain));

        destination.Clear();
        int covered = 0;
        lock (gate)
        {
            long earliest = OldestUnsafe;
            for (int i = 0, frames = destination.Length / channels; i < frames; i++)
            {
                double position = firstFrame + i * sourceFramesPerOutputFrame;
                if (position < earliest || position >= end) continue;
                long index = (long)Math.Floor(position);
                int left = Slot(index);
                if (stamps[left] != index) continue;

                double fraction = position - index;
                var l = audio[left * 2];
                var r = audio[left * 2 + 1];
                if (fraction > 0)
                {
                    long nextIndex = index + 1;
                    int right = left + 1 == CapacityFrames ? 0 : left + 1;
                    if (nextIndex >= end || stamps[right] != nextIndex) continue;
                    l += (float)(fraction * (audio[right * 2] - l));
                    r += (float)(fraction * (audio[right * 2 + 1] - r));
                }
                if (channels == 1) destination[i] = (l + r) * (.5f * gain);
                else
                {
                    destination[i * 2] = l * gain;
                    destination[i * 2 + 1] = r * gain;
                }
                covered++;
            }
        }
        return covered;
    }

    /// <summary>Map a clock position into the timeline using a source clock anchor.</summary>
    public static double PositionAtQpc(long qpc, long anchorQpc, double anchorSourceFrame,
        int sourceRate, long qpcTicksPerSecond = 10_000_000)
    {
        if (!double.IsFinite(anchorSourceFrame)) throw new ArgumentOutOfRangeException(nameof(anchorSourceFrame));
        if (sourceRate is < 8_000 or > 384_000) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (qpcTicksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(qpcTicksPerSecond));
        // Compute the signed delta before conversion to avoid loss of a small
        // difference between two large clock values, or overflow on subtraction.
        return anchorSourceFrame + (double)((Int128)qpc - anchorQpc) * sourceRate / qpcTicksPerSecond;
    }

    private int Slot(long frame)
    {
        int remainder = (int)(frame % CapacityFrames);
        return remainder < 0 ? remainder + CapacityFrames : remainder;
    }

    private static float FinitePcm(float sample) => float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0;

    private static void CheckFrame(long frame, string parameter)
    {
        if (frame is < -MaximumAbsoluteFrame or > MaximumAbsoluteFrame)
            throw new ArgumentOutOfRangeException(parameter);
    }
}
