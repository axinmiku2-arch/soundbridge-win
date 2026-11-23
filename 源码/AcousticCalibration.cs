namespace AudioRelay;

public sealed record AcousticCalibrationReading(string Name, int AdditionalDelayMs);

public sealed record AcousticCalibrationResult(
    bool Applied, string Message, IReadOnlyList<AcousticCalibrationReading> Routes, double? VerifiedSpreadMs = null);

/// <summary>Short, local-only coded-pulse measurement. This class never opens a microphone or persists audio.</summary>
internal sealed class AcousticCalibration
{
    internal const long HnsPerMs = 10_000;
    private const long Bin = HnsPerMs;
    private const int BinsPerChip = 4;
    private const long Chip = BinsPerChip * Bin;
    private const long Slot = 1_150 * HnsPerMs;
    private const long Guard = 300 * HnsPerMs;
    private const long After = 1_050 * HnsPerMs;
    private const long SearchBefore = 70 * HnsPerMs;
    private const long SearchAfter = 810 * HnsPerMs;
    private const double MaxRepeatDifferenceMs = 12;
    internal const double MaxVerifiedSpreadMs = 12;
    private static readonly int[] Pattern = [1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 1, 0, 1, 0, 0, 1];
    private static readonly int ActiveChips = Pattern.Sum();
    private static readonly double PatternMean = Pattern.Average();
    private static readonly double PatternVariance = Pattern.Sum(p => (p - PatternMean) * (p - PatternMean));
    private readonly long[] times;
    private readonly long phaseStart;
    private readonly double[] powers;
    private readonly int[] counts;

    public AcousticCalibration(long now, int routes)
    {
        if (routes is < 2 or > 4) throw new ArgumentOutOfRangeException(nameof(routes));
        phaseStart = now;
        times = Enumerable.Range(0, routes * 2).Select(i => now + Guard + i * Slot).ToArray();
        End = times[^1] + After;
        powers = new double[(int)((End - phaseStart) / Bin) + 1];
        counts = new int[powers.Length];
    }

    public long End { get; }

    public float Pulse(int route, double qpc, long compensationHns)
    {
        if (route < 0 || route >= times.Length / 2) return 0;
        // Delay is relative to the slot time, never a negative advance.
        var first = qpc - times[route * 2] - compensationHns;
        return PulseAt(first) + PulseAt(first - Slot);
    }

    private static float PulseAt(double delta)
    {
        if (delta < 0 || delta >= Pattern.Length * Chip) return 0;
        var chip = (int)(delta / Chip);
        if (Pattern[chip] == 0) return 0;
        var t = delta / 10_000_000.0;
        var within = (delta - chip * Chip) / Chip;
        var envelope = Math.Sin(Math.PI * within);
        // -24 dBFS peak, before the existing route and system volume multipliers.
        return (float)(.0625 * envelope * Math.Sin(2 * Math.PI * (900 * t + 15_000 * t * t)));
    }

    public void Capture(long firstQpc, int sampleRate, ReadOnlySpan<float> stereo)
    {
        if (sampleRate is < 8_000 or > 384_000) return;
        double relative = firstQpc - phaseStart;
        double step = 10_000_000.0 / sampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2, relative += step)
        {
            if (relative < 0) continue;
            int index = (int)(relative / Bin);
            if (index >= powers.Length) break;
            double l = stereo[i], r = stereo[i + 1];
            if (!double.IsFinite(l) || !double.IsFinite(r)) continue;
            powers[index] += .5 * (l * l + r * r);
            counts[index]++;
        }
    }

    /// <summary>Measure each route's delay relative to its own scheduled slot in milliseconds.</summary>
    public bool TryMeasure(out double[] delays, out string problem)
    {
        delays = new double[times.Length / 2];
        var energy = new double[powers.Length];
        for (int i = 0; i < energy.Length; ++i)
            energy[i] = counts[i] == 0 ? 0 : Math.Sqrt(powers[i] / counts[i]);
        for (int i = 0; i < delays.Length; ++i)
        {
            if (!TryLocate(energy, times[i * 2], out var a) || !TryLocate(energy, times[i * 2 + 1], out var b))
            {
                problem = $"设备 {i + 1} 的两次测试音未可靠识别。请在安静环境中，把麦克风放在正常听音位置，确认输出未静音。";
                return false;
            }
            if (Math.Abs(a - b) > MaxRepeatDifferenceMs)
            {
                problem = $"设备 {i + 1} 的两次测量差异过大（{Math.Abs(a - b):F0} 毫秒），未采用该结果。";
                return false;
            }
            delays[i] = (a + b) / 2;
        }
        problem = "";
        return true;
    }

    public static long[] RelativeCompensations(IReadOnlyList<double> delays)
    {
        if (delays.Count == 0 || delays.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("Each route requires a finite delay.", nameof(delays));
        double slowest = delays.Max();
        return delays.Select(ms => (long)Math.Round((slowest - ms) * HnsPerMs)).ToArray();
    }

    private bool TryLocate(double[] energy, long qpc, out double delayMs)
    {
        delayMs = 0;
        int start = (int)((qpc - SearchBefore - phaseStart) / Bin);
        int width = Pattern.Length * BinsPerChip;
        int stop = Math.Min(energy.Length - width - 1, (int)((qpc + SearchAfter - phaseStart) / Bin));
        double best = 0;
        int bestAt = -1;
        var points = new double[stop - start + 1];
        var noise = new List<double>(60);
        for (int i = Math.Max(0, start - 60); i < start; ++i)
            if (counts[i] > 0) noise.Add(energy[i]);
        if (noise.Count < 24) return false;
        noise.Sort();
        double minimum = Math.Max(.00015, noise[noise.Count / 2] * 1.45);
        for (int i = start; i <= stop; i++)
        {
            double sum = 0, sum2 = 0, numerator = 0, on = 0, off = 0;
            int covered = 0;
            for (int j = 0; j < Pattern.Length; j++)
            {
                int k = i + j * BinsPerChip;
                double e = 0;
                for (int b = 0; b < BinsPerChip; b++)
                {
                    e += energy[k + b];
                    if (counts[k + b] > 0) covered++;
                }
                e /= BinsPerChip;
                sum += e;
                sum2 += e * e;
                numerator += e * (Pattern[j] - PatternMean);
                if (Pattern[j] == 1) on += e;
                else off += e;
            }
            double active = on / ActiveChips;
            double quiet = off / (Pattern.Length - ActiveChips);
            double spread = sum2 - sum * sum / Pattern.Length;
            if (covered < width - BinsPerChip || active < minimum || active < quiet * 1.32 || spread <= 1e-12) continue;
            double score = numerator / Math.Sqrt(spread * PatternVariance);
            if (score < .67) continue;
            points[i - start] = score;
            if (score > best) { best = score; bestAt = i; }
        }
        if (bestAt < 0) return false;
        for (int i = start; i <= stop; i++)
            if (Math.Abs(i - bestAt) >= 22 && points[i - start] >= best - .035) return false;
        delayMs = (phaseStart + bestAt * Bin - qpc) / (double)HnsPerMs;
        return true;
    }
}
