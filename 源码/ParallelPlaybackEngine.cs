using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRelay;

/// <summary>Host-side diagnostics only. A driver clock is not an acoustic measurement.</summary>
public sealed record ParallelRouteDiagnostics(
    string TargetId, string Name, bool HasDeviceClock, double ClockDriftPpm, double BufferedMs, long GapFrames);

/// <summary>
/// Captures one explicitly selected, inaudible render endpoint. Every audible
/// destination is a sibling that reads a single timeline on the host QPC clock.
/// Does not reroute Windows or suppress the source. Ordinary playback does not
/// open the microphone; it is used only by an explicit acoustic calibration.
/// </summary>
public sealed class ParallelPlaybackEngine : IDisposable
{
    private const long TicksPerSecond = 10_000_000;
    private const long TicksPerMs = 10_000;
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private readonly object gate = new();
    private Session? session;
    private bool disposed;

    public event Action<string>? Status;
    public bool IsRunning
    {
        get { lock (gate) return session is { Running: true, Cancelled: false }; }
    }

    /// <summary>
    /// Scheduling lead in milliseconds, 0 before any source data. This is not
    /// the end-to-end latency or a verification of acoustic synchronization.
    /// </summary>
    public int CommonLeadMs
    {
        get { lock (gate) return session?.LeadMs ?? 0; }
    }

    public IReadOnlyList<ParallelRouteDiagnostics> GetDiagnostics()
    {
        lock (gate) return session?.Snapshot ?? Array.Empty<ParallelRouteDiagnostics>();
    }

    /// <summary>
    /// Starts a brief, explicitly requested acoustic test using the current Windows default recording
    /// endpoint. Test samples are reduced to in-memory energy bins and are never retained or sent.
    /// </summary>
    public Task<AcousticCalibrationResult> CalibrateAsync()
    {
        lock (gate)
        {
            if (session is not { Running: true, Cancelled: false } current)
                return Task.FromResult(new AcousticCalibrationResult(false, "请先启动双设备同步。", []));
            var pending = current.Calibration;
            if (pending != null) return pending.Task;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            current.Calibration = pending;
            return pending.Task;
        }
    }

    public void Start(string silentSourceId, IEnumerable<RelayTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var requested = targets.ToArray();
        AudioRoutePolicy.RequireTargets(silentSourceId, requested.Select(t => t.Id));
        var routes = requested.Select(t => new Route(t)).ToArray();

        Stop();
        Session current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session != null) throw new InvalidOperationException("上一次并行播放仍在停止，请稍后重试。");
            current = new Session(silentSourceId, routes, Run);
            session = current;
            try { current.Thread.Start(); }
            catch { session = null; current.Wake.Dispose(); throw; }
        }
        try { current.Started.Task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            Cancel(current);
            throw new InvalidOperationException("并行播放打开设备超时。请检查连接和设备独占状态。");
        }
    }

    public void SetVolume(string targetId, int percent)
    {
        lock (gate)
        {
            var route = session?.Routes.FirstOrDefault(r => Ids.Equals(r.Id, targetId));
            if (route != null) route.Volume = Math.Clamp(percent, 0, 100);
        }
    }

    public void Stop()
    {
        Session? current;
        lock (gate) current = session;
        if (current == null) return;
        Cancel(current);
        if (current.Thread != Thread.CurrentThread) current.Thread.Join(3000);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Stop();
    }

    private void Cancel(Session current)
    {
        lock (gate)
        {
            if (session != current) return;
            current.Cancelled = true;
            current.Wake.Set();
        }
    }

    private void Run(Session current)
    {
        StreamRegistration? capture = null;
        MicTest? calibration = null;
        var sinks = new List<Peer>();
        var warnings = new List<string>();
        Exception? startupError = null;
        bool started = false;
        var sourceName = "无声来源";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var masterVolume = new SystemMasterVolume();
            // Check the entire pin-only route before opening any stream. The individual state
            // checks below also cover a device disappearing between enumeration and activation.
            var online = new List<string>();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (endpoint)
                {
                    try { online.Add(endpoint.ID); }
                    catch (COMException) { }
                }
            }
            if (!AudioRoutePolicy.AllActive(current.SourceId, current.Routes.Select(r => r.Id), online))
                throw new InvalidOperationException("已选来源或目标离线或无效。不会改用其他设备。");
            using var source = enumerator.GetDevice(current.SourceId);
            EnsureActive(source);
            sourceName = Name(source);
            WaveFormat sourceMix;
            using (var inspect = source.AudioClient) sourceMix = inspect.MixFormat;
            var decoder = new LoopbackDecoder(sourceMix);
            capture = StreamRegistration.Open(source, sourceMix, AudioClientStreamFlags.Loopback, fallbackMs: 35);
            var reader = capture.Client.AudioCaptureClient;
            var sourceClock = new SourceClock(sourceMix.SampleRate);
            var timeline = new SynchronizedTimeline(sourceMix.SampleRate, 2700);
            var raw = new byte[checked(capture.Client.BufferSize * sourceMix.BlockAlign)];
            var stereo = new float[checked(capture.Client.BufferSize * 2)];
            if (capture.Event == null) warnings.Add($"{sourceName}使用定时兼容模式。");

            foreach (var route in current.Routes)
            {
                if (current.Cancelled) break;
                var name = "所选输出";
                try
                {
                    using var endpoint = enumerator.GetDevice(route.Id);
                    EnsureActive(endpoint);
                    name = Name(endpoint);
                    var peer = new Peer(endpoint, route, name);
                    sinks.Add(peer);
                    if (peer.EventHandle == null) warnings.Add($"{name}使用定时兼容模式。");
                }
                catch (Exception error) { warnings.Add($"{name}未打开：{Explain(error)}"); }
            }
            if (current.Cancelled) throw new OperationCanceledException();
            if (sinks.Count != current.Routes.Length)
                throw new InvalidOperationException("未能打开全部所选输出，已取消启动。" + string.Join(" ", warnings));

            // Each stream is warmed with silence. Its later non-silent frames
            // are chosen by timestamp, not by the order of Start() calls.
            capture.Client.Start();
            for (int i = sinks.Count - 1; i >= 0; i--)
            {
                try { sinks[i].Start(); }
                catch (Exception error)
                {
                    warnings.Add($"{sinks[i].Name}未启动：{Explain(error)}");
                    sinks[i].Dispose();
                    sinks.RemoveAt(i);
                }
            }
            if (current.Cancelled) throw new OperationCanceledException();
            if (sinks.Count != current.Routes.Length)
                throw new InvalidOperationException("未能启动全部所选输出，已取消启动。" + string.Join(" ", warnings));

            current.Snapshot = sinks.Select(s => s.Snapshot()).ToArray();
            started = current.Running = true;
            current.Started.TrySetResult(true);
            foreach (var warning in warnings) Report(warning);
            var wait = WaitHandles(current, capture, sinks);
            int waitMs = WaitMillis(capture, sinks);
            long lastTrustedSourceAt = Now(), diagnosticsAt = 0;
            bool warnedSourceClock = false;
            int goodSourceRun = 0, badSourceRun = 0;

            while (!current.Cancelled && sinks.Count > 0)
            {
                var request = current.Calibration;
                if (request != null && calibration == null)
                {
                    try
                    {
                        if (sinks.Count is < 2 or > 4)
                            throw new InvalidOperationException("麦克风校准仅支持当前正在播放的 2–4 路设备。");
                        calibration = new MicTest(enumerator, sinks);
                        Report("正在用麦克风测量各设备的实际出声时间。正常播放会短暂停顿。");
                    }
                    catch (Exception error) { FinishCalibration(current, request, false, $"无法开始麦克风校准：{Explain(error)}"); }
                }
                for (int packets = 0; packets < 16 && !current.Cancelled && reader.GetNextPacketSize() > 0; packets++)
                {
                    var ptr = reader.GetBuffer(out var frames, out var flags, out long device, out long qpc);
                    if (frames <= 0) { reader.ReleaseBuffer(frames); break; }
                    try
                    {
                        int samples = checked(frames * 2);
                        if (stereo.Length < samples) stereo = new float[samples];
                        if ((flags & AudioClientBufferFlags.Silent) != 0 || ptr == IntPtr.Zero)
                            Array.Clear(stereo, 0, samples);
                        else
                        {
                            int bytes = checked(frames * sourceMix.BlockAlign);
                            if (raw.Length < bytes) raw = new byte[bytes];
                            Marshal.Copy(ptr, raw, 0, bytes);
                            decoder.Decode(raw.AsSpan(0, bytes), stereo.AsSpan(0, samples));
                        }
                        long hostNow = Now();
                        bool timestamp = (flags & AudioClientBufferFlags.TimestampError) == 0 &&
                            qpc > 0 && qpc >= hostNow - 300 * TicksPerMs && qpc <= hostNow + 25 * TicksPerMs;
                        if (timestamp)
                        {
                            lastTrustedSourceAt = hostNow;
                            if (goodSourceRun < 12) goodSourceRun++;
                            badSourceRun = 0;
                        }
                        else
                        {
                            if (badSourceRun < 12) badSourceRun++;
                            goodSourceRun = 0;
                        }
                        long at = sourceClock.Place(frames, device, qpc, hostNow, timestamp,
                            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                        timeline.Write(at, stereo.AsSpan(0, samples));
                        current.LeadMs = (int)(sourceClock.LeadHns / TicksPerMs);
                    }
                    finally { reader.ReleaseBuffer(frames); }
                }

                bool changed = false;
                var master = masterVolume.Read();
                for (int i = sinks.Count - 1; i >= 0 && !current.Cancelled; i--)
                {
                    var peer = sinks[i];
                    try
                    {
                        var warning = peer.Pump(timeline, sourceClock, master.GainFor(peer.Id), calibration);
                        if (warning != null) Report(warning);
                    }
                    catch (Exception error)
                    {
                        sinks.RemoveAt(i);
                        peer.Dispose();
                        changed = true;
                        Report($"{peer.Name}已退出并行播放：{Explain(error)}");
                    }
                }
                long now = Now();
                if (calibration != null && request != null)
                {
                    try
                    {
                        if (changed) throw new InvalidOperationException("校准期间有设备断开。");
                        calibration.ReadMicrophone();
                        if (now >= calibration.End)
                        {
                            if (calibration.Advance(now) is { } completed)
                            {
                                calibration.Dispose();
                                calibration = null;
                                foreach (var peer in sinks) peer.ResetFollower();
                                FinishCalibration(current, request, completed.Applied, completed.Message,
                                    completed.Routes, completed.VerifiedSpreadMs);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        calibration?.Dispose();
                        calibration = null;
                        foreach (var peer in sinks) peer.ResetFollower();
                        FinishCalibration(current, request, false, $"麦克风校准未采用：{Explain(error)}");
                    }
                }
                if (!warnedSourceClock && badSourceRun > 3 && now - lastTrustedSourceAt > TicksPerSecond)
                {
                    warnedSourceClock = true;
                    Report($"{sourceName}未提供可信的采集时间戳，正在用主机到包时间估算，不能确认同步。");
                }
                else if (warnedSourceClock && goodSourceRun > 10)
                {
                    warnedSourceClock = false;
                    Report($"{sourceName}采集时间戳已恢复。此状态不是对实际出声的测量。");
                }
                if (changed || now >= diagnosticsAt)
                {
                    current.Snapshot = sinks.Select(s => s.Snapshot()).ToArray();
                    diagnosticsAt = now + 300 * TicksPerMs;
                }
                if (changed)
                {
                    wait = WaitHandles(current, capture, sinks);
                    waitMs = WaitMillis(capture, sinks);
                    if (sinks.Count == 1) Report("只剩一路输出。并行同步已降级，剩余设备继续播放。");
                }
                if (!current.Cancelled && sinks.Count > 0)
                    WaitHandle.WaitAny(wait, calibration != null ? Math.Min(waitMs, 5) : waitMs);
            }
            if (!current.Cancelled) Report("没有仍在运行的并行输出，请检查设备后重新开始。");
        }
        catch (Exception error)
        {
            if (!started)
                startupError = error is OperationCanceledException ? error : new InvalidOperationException($"{sourceName}：{Explain(error)}", error);
            else if (!current.Cancelled)
                Report($"并行播放已停止：{Explain(error)}");
        }
        finally
        {
            current.Running = false;
            calibration?.Dispose();
            if (current.Calibration is { } pending)
                FinishCalibration(current, pending, false, "播放已停止，未应用校准。");
            capture?.Dispose();
            foreach (var peer in sinks) peer.Dispose();
            lock (gate)
            {
                if (session == current) session = null;
                current.Wake.Dispose();
            }
            if (!started) current.Started.TrySetException(startupError ?? new InvalidOperationException("并行播放未启动。"));
        }
    }

    private void FinishCalibration(Session current, TaskCompletionSource<AcousticCalibrationResult> request,
        bool applied, string message, IReadOnlyList<AcousticCalibrationReading>? routes = null, double? spread = null)
    {
        lock (gate)
            if (current.Calibration == request) current.Calibration = null;
        request.TrySetResult(new(applied, message, routes ?? [], spread));
        Report(message);
    }

    private static WaitHandle[] WaitHandles(Session current, StreamRegistration capture, List<Peer> sinks)
    {
        var list = new List<WaitHandle>(sinks.Count + 2) { current.Wake };
        if (capture.Event != null) list.Add(capture.Event);
        foreach (var sink in sinks) if (sink.EventHandle != null) list.Add(sink.EventHandle);
        return list.ToArray();
    }

    private static int WaitMillis(StreamRegistration capture, List<Peer> sinks) =>
        capture.Event == null || sinks.Any(s => s.EventHandle == null) ? 4 : 25; // event watchdog also detects removal

    private void Report(string text)
    {
        try { Status?.Invoke(text); } catch { }
    }

    private static long Now() => (long)(Stopwatch.GetTimestamp() * ((double)TicksPerSecond / Stopwatch.Frequency));

    private static void EnsureActive(MMDevice endpoint)
    {
        if (endpoint.DataFlow != DataFlow.Render || (endpoint.State & DeviceState.Active) == 0)
            throw new InvalidOperationException("设备未连接或已停用。");
    }

    private static string Name(MMDevice endpoint) =>
        endpoint.FriendlyName is { Length: > 0 } name and not "Unknown" ? name : "未命名设备";

    private static string Explain(Exception e) => (uint)e.HResult switch
    {
        0x88890004 or 0x88890026 or 0x80070490 => "设备已断开、停用或更改。请刷新设备列表。",
        0x88890008 => "设备不接受当前音频格式。可尝试在 Windows 声音设置选择 44.1 kHz 或 48 kHz。",
        0x8889000A or 0x8889000E => "无法共享此设备，可能被其他程序独占。",
        _ => e is COMException ? $"Windows 音频服务返回错误（0x{e.HResult:X8}）。" : e.Message
    };

    private sealed class Route
    {
        public Route(RelayTarget target)
        {
            Id = target.Id;
            Volume = Math.Clamp(target.Volume, 0, 100);
            DelayHns = Math.Clamp(target.DelayMs, 0, 1000) * TicksPerMs;
        }
        public readonly string Id;
        public readonly long DelayHns;
        public volatile int Volume;
    }

    private sealed class Session
    {
        public Session(string source, Route[] routes, Action<Session> work)
        {
            SourceId = source;
            Routes = routes;
            Thread = new Thread(() => work(this)) { Name = "AudioRelay Parallel WASAPI", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            Thread.SetApartmentState(ApartmentState.MTA);
        }
        public readonly string SourceId;
        public readonly Route[] Routes;
        public readonly Thread Thread;
        public readonly ManualResetEvent Wake = new(false);
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile TaskCompletionSource<AcousticCalibrationResult>? Calibration;
        public volatile bool Cancelled, Running;
        public volatile int LeadMs;
        public volatile ParallelRouteDiagnostics[] Snapshot = [];
    }

    private sealed class StreamRegistration(AudioClient client, AutoResetEvent? signal) : IDisposable
    {
        public AudioClient Client { get; } = client;
        public AutoResetEvent? Event { get; } = signal;

        public static StreamRegistration Open(MMDevice endpoint, WaveFormat format, AudioClientStreamFlags flags, int fallbackMs)
        {
            var client = endpoint.AudioClient;
            var signal = new AutoResetEvent(false);
            try
            {
                client.Initialize(AudioClientShareMode.Shared, flags | AudioClientStreamFlags.EventCallback, 0, 0, format, Guid.Empty);
                client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
                return new(client, signal);
            }
            catch
            {
                try { client.Dispose(); } catch { }
                signal.Dispose();
            }
            var fallback = endpoint.AudioClient; // a failed Initialize consumes the prior COM client
            try
            {
                fallback.Initialize(AudioClientShareMode.Shared, flags, fallbackMs * TicksPerMs, 0, format, Guid.Empty);
                return new(fallback, null);
            }
            catch { fallback.Dispose(); throw; }
        }

        public void Dispose()
        {
            try { Client.Stop(); } catch { }
            try { Client.Dispose(); } catch { }
            Event?.Dispose();
        }
    }

    /// <summary>Mapping between the capture sample number and the common QPC timebase.</summary>
    internal sealed class SourceClock(int nominalRate)
    {
        private long anchorTime, deviceEpoch, deviceTimeline, surveyTime, surveyFrame, lastCaptureHost, lastTimestamp;
        private long nextFrame;
        private double anchorFrame;
        public bool Ready { get; private set; }
        public double FramesPerSecond { get; private set; } = nominalRate;
        public long LeadHns { get; private set; } = 85 * TicksPerMs;

        public double At(double qpc) => anchorFrame + (qpc - anchorTime) * FramesPerSecond / TicksPerSecond;

        public long Place(int frames, long device, long qpc, long host, bool valid, bool discontinuity)
        {
            if (!Ready)
            {
                anchorTime = valid ? qpc : host - (long)(frames * (double)TicksPerSecond / nominalRate);
                anchorFrame = 0;
                deviceEpoch = device;
                deviceTimeline = 0;
                if (valid) surveyTime = lastTimestamp = qpc;
                LeadHns = Math.Clamp(host - anchorTime + 35 * TicksPerMs, 85 * TicksPerMs, 210 * TicksPerMs);
                nextFrame = frames;
                lastCaptureHost = host;
                Ready = true;
                return 0;
            }

            long start = nextFrame;
            double predicted = valid ? At(qpc) : At(host - frames * (double)TicksPerSecond / nominalRate);
            long byDevice = device >= deviceEpoch ? deviceTimeline + (device - deviceEpoch) : -1;
            bool coherent = byDevice >= 0 && Math.Abs(byDevice - predicted) <= nominalRate * .04;
            if (valid && coherent && byDevice >= start) start = byDevice;
            else if (predicted > start + nominalRate * .022 &&
                     (valid || host - lastCaptureHost > 30 * TicksPerMs))
                start = (long)Math.Round(predicted);

            if (discontinuity || !coherent)
            {
                deviceEpoch = device;
                deviceTimeline = start;
            }
            if (valid && qpc > lastTimestamp)
            {
                if (surveyTime == 0 || discontinuity)
                {
                    surveyTime = qpc;
                    surveyFrame = start;
                }
                else if (qpc - surveyTime >= 2 * TicksPerSecond)
                {
                    double observed = (start - surveyFrame) * (double)TicksPerSecond / (qpc - surveyTime);
                    if (Math.Abs(observed / nominalRate - 1) <= .005)
                        FramesPerSecond += (observed - FramesPerSecond) * .18;
                    surveyTime = qpc;
                    surveyFrame = start;
                }
                // Smooth bounded correction for capture packet timestamp jitter.
                anchorFrame = predicted + Math.Clamp((start - predicted) * .06, -nominalRate * .0004, nominalRate * .0004);
                anchorTime = qpc;
                lastTimestamp = qpc;
            }
            nextFrame = start + frames;
            lastCaptureHost = host;
            return start;
        }
    }

    /// <summary>Render timeline follower; keeps finite slew rather than jumping every WASAPI event.</summary>
    internal sealed class ReadFollower(int outputRate)
    {
        private double nextFrame;
        private bool primed;
        public void Reset() => primed = false;
        public (double first, double step) Next(double desired, int outputFrames, double sourceRate, double deviceRate)
        {
            double baseStep = sourceRate / deviceRate;
            if (!primed || Math.Abs(desired - nextFrame) > sourceRate * .060)
            {
                nextFrame = desired;
                primed = true;
            }
            // Recover small phase error over about 450ms, at no more than 0.8%.
            double error = desired - nextFrame;
            double correction = Math.Clamp(error / (outputRate * .45), -baseStep * .008, baseStep * .008);
            var result = (nextFrame, baseStep + correction);
            nextFrame += outputFrames * result.Item2;
            return result;
        }
    }

    private sealed class Peer : IDisposable
    {
        private readonly Route route;
        private readonly StreamRegistration stream;
        private readonly AudioRenderClient writer;
        private readonly AudioClockClient? clock;
        private readonly double frequency;
        private readonly int outputRate, channels, capacity;
        private readonly float[] samples;
        private readonly ReadFollower follower;
        private double observedRate, rateAnchorFrame;
        private long rateAnchorQpc, startedAt, submitted, lastGoodClock, gaps, lastStartupOrDegrade;
        private double bufferedMs;
        private bool clockCredible, warnedClock;
        private long? calibratedDelayHns;
        public string Name { get; }
        public string Id => route.Id;
        public WaitHandle? EventHandle => stream.Event;

        public Peer(MMDevice device, Route config, string name)
        {
            route = config;
            Name = name;
            WaveFormat mix;
            using (var inspect = device.AudioClient) mix = inspect.MixFormat;
            outputRate = mix.SampleRate;
            if (outputRate is < 8000 or > 384000) throw new NotSupportedException("目标设备采样率不受支持。");
            channels = mix.Channels == 1 ? 1 : 2;
            follower = new ReadFollower(outputRate);
            observedRate = outputRate;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, channels);
            stream = StreamRegistration.Open(device, format,
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality | AudioClientStreamFlags.NoPersist,
                fallbackMs: 25);
            try
            {
                capacity = stream.Client.BufferSize;
                samples = new float[checked(capacity * channels)];
                writer = stream.Client.AudioRenderClient;
                try { clock = stream.Client.AudioClockClient; frequency = clock.Frequency; }
                catch (COMException) { clock = null; frequency = 0; }
                writer.GetBuffer(capacity);
                writer.ReleaseBuffer(capacity, AudioClientBufferFlags.Silent);
                submitted = capacity;
            }
            catch { stream.Dispose(); throw; }
        }

        public void Start()
        {
            stream.Client.Start();
            startedAt = lastStartupOrDegrade = Now();
        }

        public ParallelRouteDiagnostics Snapshot() => new(route.Id, Name, clockCredible,
            clockCredible ? (observedRate / outputRate - 1) * 1e6 : 0, bufferedMs, gaps);

        public void SetCalibratedDelay(long delayHns) => calibratedDelayHns = delayHns;
        public void ResetFollower() => follower.Reset();

        public string? Pump(SynchronizedTimeline timeline, SourceClock source, float masterGain, MicTest? test)
        {
            int padding = stream.Client.CurrentPadding;
            bufferedMs = padding * 1000.0 / outputRate;
            int frames = capacity - padding;
            if (frames <= 0) return null;
            long now = Now();
            double tailTime = now + padding * (double)TicksPerSecond / observedRate;
            bool goodObservation = false;
            if (clock != null && frequency > 0)
            {
                try
                {
                    if (clock.GetPosition(out ulong position, out ulong sampleQpc) &&
                        sampleQpc <= long.MaxValue && sampleQpc > 0 && position > 0)
                    {
                        long qpc = (long)sampleQpc;
                        double playFrame = position * (double)outputRate / frequency;
                        double difference = submitted - playFrame;
                        double clockTail = qpc + difference * TicksPerSecond / observedRate;
                        if (qpc >= now - 100 * TicksPerMs && qpc <= now + 15 * TicksPerMs &&
                            difference >= -outputRate * .022 && Math.Abs(clockTail - tailTime) <= 38 * TicksPerMs)
                        {
                            goodObservation = true;
                            lastGoodClock = now;
                            tailTime = clockTail;
                            if (rateAnchorQpc == 0) { rateAnchorQpc = qpc; rateAnchorFrame = playFrame; }
                            else if (qpc - rateAnchorQpc >= 2 * TicksPerSecond)
                            {
                                double measured = (playFrame - rateAnchorFrame) * TicksPerSecond / (qpc - rateAnchorQpc);
                                if (Math.Abs(measured / outputRate - 1) <= .005)
                                    observedRate += (measured - observedRate) * .20;
                                rateAnchorQpc = qpc;
                                rateAnchorFrame = playFrame;
                            }
                        }
                        else if (padding == 0 && difference < -outputRate * .022)
                        {
                            // After an underrun some drivers count synthetic
                            // zero frames as elapsed. Rebase the stream cursor.
                            submitted = (long)Math.Round(playFrame + (now - qpc) * observedRate / TicksPerSecond);
                            rateAnchorQpc = 0;
                        }
                    }
                }
                catch (COMException) { /* CurrentPadding still detects invalidated device. */ }
            }
            bool wasCredible = clockCredible;
            clockCredible = goodObservation || lastGoodClock > 0 && now - lastGoodClock <= 350 * TicksPerMs;
            if (wasCredible && !clockCredible) lastStartupOrDegrade = now;

            int readable = 0;
            float gain = route.Volume * .01f * masterGain;
            if (test != null)
            {
                double period = TicksPerSecond / observedRate;
                int index = test.RouteIndex(this);
                if (gain != 0)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float sample = test.Pulse(index, tailTime + i * period) * gain;
                        if (sample != 0) readable++;
                        for (int ch = 0; ch < channels; ch++) samples[i * channels + ch] = sample;
                    }
                }
            }
            else if (source.Ready)
            {
                double desired = source.At(tailTime - source.LeadHns - (calibratedDelayHns ?? route.DelayHns));
                var (first, step) = follower.Next(desired, frames, source.FramesPerSecond, observedRate);
                readable = timeline.Read(first, step, samples.AsSpan(0, frames * channels), channels, gain);
                gaps += frames - readable; // includes deliberate startup and idle silence
            }
            var ptr = writer.GetBuffer(frames);
            if (readable == 0 || gain == 0)
                writer.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
            else
            {
                try { Marshal.Copy(samples, 0, ptr, frames * channels); }
                catch { writer.ReleaseBuffer(frames, AudioClientBufferFlags.Silent); throw; }
                writer.ReleaseBuffer(frames, AudioClientBufferFlags.None);
            }
            submitted += frames;

            if (clockCredible && warnedClock)
            {
                warnedClock = false;
                return $"{Name}恢复设备时钟。此状态仅代表主机调度，实际出声仍需试听验证。";
            }
            if (!clockCredible && !warnedClock && startedAt > 0 && now - lastStartupOrDegrade > TicksPerSecond)
            {
                warnedClock = true;
                return $"{Name}没有可靠的驱动时钟。正在根据缓冲区估算，不能确认同步。";
            }
            return null;
        }

        public void Dispose() => stream.Dispose();
    }

    private sealed class MicTest : IDisposable
    {
        private readonly Peer[] peers;
        private readonly StreamRegistration microphone;
        private readonly AudioCaptureClient microphoneReader;
        private readonly LoopbackDecoder decoder;
        private readonly WaveFormat format;
        private readonly byte[] raw;
        private readonly float[] stereo;
        private AcousticCalibration phase;
        private long[]? corrections;

        public MicTest(MMDeviceEnumerator enumerator, List<Peer> active)
        {
            peers = active.ToArray();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            if ((endpoint.State & DeviceState.Active) == 0)
                throw new InvalidOperationException("没有可用的默认麦克风。");
            using (var inspect = endpoint.AudioClient) format = inspect.MixFormat;
            decoder = new LoopbackDecoder(format);
            microphone = StreamRegistration.Open(endpoint, format, AudioClientStreamFlags.None, fallbackMs: 35);
            try
            {
                microphoneReader = microphone.Client.AudioCaptureClient;
                raw = new byte[checked(microphone.Client.BufferSize * format.BlockAlign)];
                stereo = new float[checked(microphone.Client.BufferSize * 2)];
                microphone.Client.Start();
                phase = new AcousticCalibration(Now(), peers.Length);
            }
            catch { microphone.Dispose(); throw; }
        }

        public long End => phase.End;

        public int RouteIndex(Peer peer) => Array.IndexOf(peers, peer);

        public float Pulse(int i, double at) => i < 0 ? 0 : phase.Pulse(i, at, corrections?[i] ?? 0);

        public void ReadMicrophone()
        {
            for (int packets = 0; packets < 8 && microphoneReader.GetNextPacketSize() > 0; packets++)
            {
                var ptr = microphoneReader.GetBuffer(out var frames, out var flags, out _, out long qpc);
                try
                {
                    long now = Now();
                    int samples = checked(frames * 2);
                    if (frames <= 0 || samples > stereo.Length || (flags & AudioClientBufferFlags.TimestampError) != 0 ||
                        qpc <= 0 || qpc < now - 500 * TicksPerMs || qpc > now + 25 * TicksPerMs) continue;
                    if (ptr == IntPtr.Zero || (flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(stereo, 0, samples);
                    else
                    {
                        int bytes = checked(frames * format.BlockAlign);
                        Marshal.Copy(ptr, raw, 0, bytes);
                        decoder.Decode(raw.AsSpan(0, bytes), stereo.AsSpan(0, samples));
                    }
                    phase.Capture(qpc, format.SampleRate, stereo.AsSpan(0, samples));
                }
                finally { microphoneReader.ReleaseBuffer(frames); }
            }
        }

        public AcousticCalibrationResult? Advance(long now)
        {
            if (!phase.TryMeasure(out var measured, out var problem))
                return new(false, $"校准未采用：{problem}", []);
            if (corrections == null)
            {
                corrections = AcousticCalibration.RelativeCompensations(measured);
                phase = new AcousticCalibration(now, peers.Length);
                return null;
            }
            double spread = measured.Max() - measured.Min();
            if (spread > AcousticCalibration.MaxVerifiedSpreadMs)
                return new(false, $"校准复测差 {spread:F0} 毫秒，未采用结果。请确认设备位置及蓝牙连接稳定后重试。", [], spread);
            for (int i = 0; i < peers.Length; i++) peers[i].SetCalibratedDelay(corrections[i]);
            var readings = peers.Select((peer, i) => new AcousticCalibrationReading(peer.Name,
                (int)Math.Round(corrections[i] / (double)TicksPerMs))).ToArray();
            return new(true, $"校准已应用到本次播放。麦克风位置的复测差约 {spread:F0} 毫秒。", readings, spread);
        }

        public void Dispose() => microphone.Dispose();
    }
}
