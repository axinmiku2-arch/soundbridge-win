using AudioRelay;

const long HnsPerMs = AcousticCalibration.HnsPerMs;
const long Epoch = 123_456_789_000;

static void Require(bool ok, string details)
{
    if (!ok) throw new InvalidOperationException(details);
}

static AcousticCalibration Sample(double[] hardware, long[]? compensations = null,
    bool unstable = false, bool clipped = false, int microphoneRate = 48_000)
{
    var capture = new AcousticCalibration(Epoch, hardware.Length);
    var noise = new Random(4826);
    var packet = new float[(microphoneRate / 100) * 2];
    long frame = 0;
    while (Epoch + frame * 10_000_000 / microphoneRate < capture.End)
    {
        for (var i = 0; i < packet.Length / 2; i++)
        {
            double now = Epoch + (frame + i) * (10_000_000.0 / microphoneRate);
            float sample = (float)((noise.NextDouble() - .5) * .0006);
            for (int route = 0; route < hardware.Length; route++)
            {
                if (!double.IsFinite(hardware[route])) continue;
                // Mitigate clipping and include mild asymmetry / delayed reflections.
                var lag = hardware[route] * HnsPerMs;
                if (unstable && route == 0 && now > Epoch + 1200 * HnsPerMs) lag += 40 * HnsPerMs;
                long delay = compensations?[route] ?? 0;
                var level = route == 0 ? .45f : .27f;
                sample += level * capture.Pulse(route, now - lag, delay);
                sample += level * .16f * capture.Pulse(route, now - lag - 22.1 * HnsPerMs, delay);
            }
            packet[i * 2] = sample;
            packet[i * 2 + 1] = sample * .86f;
        }
        long qpc = Epoch + frame * 10_000_000 / microphoneRate;
        if (!clipped || (qpc < Epoch + 250 * HnsPerMs || qpc > Epoch + 500 * HnsPerMs))
            capture.Capture(qpc, microphoneRate, packet);
        frame += packet.Length / 2;
    }
    return capture;
}

static void TwoPass(double[] hardware, int rate = 48_000)
{
    var first = Sample(hardware, microphoneRate: rate);
    Require(first.TryMeasure(out var delays, out var problem), problem);
    for (var i = 0; i < hardware.Length; i++)
        Require(Math.Abs(delays[i] - hardware[i]) <= 4, $"route {i}: wanted {hardware[i]} ms, got {delays[i]} ms");
    long[] offsets = AcousticCalibration.RelativeCompensations(delays);
    Require(offsets.All(value => value >= 0) && offsets[Array.IndexOf(hardware, hardware.Max())] == 0,
        "Must delay the faster outputs only.");
    var verified = Sample(hardware, offsets, microphoneRate: rate);
    Require(verified.TryMeasure(out var result, out problem), problem);
    Require(result.Max() - result.Min() <= 5, "After correction, synthetic routes must align within 5 ms.");
}

TwoPass([34, 173]);
TwoPass([28.25, 197.75, 63.6, 412.3], 44_100);
TwoPass([86.1, 213.4], 16_000);
Require(!Sample([36, double.PositiveInfinity]).TryMeasure(out _, out _), "Missing/muted route must fail.");
Require(!Sample([26, 170], unstable: true).TryMeasure(out _, out _), "An unstable route must fail.");
Require(!Sample([35, 180], clipped: true).TryMeasure(out _, out _), "Missing microphone packets must fail.");
Console.WriteLine("PASS: two-output / four-output measurements at 48/44.1/16 kHz, verified phase alignment, missing output, instability, and missing packets.");
