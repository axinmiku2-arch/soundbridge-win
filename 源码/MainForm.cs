using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AudioRelay;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 44, 63);
    private static readonly Color Muted = Color.FromArgb(72, 90, 107);
    private static readonly Color Surface = Color.FromArgb(244, 248, 251);
    private static readonly Color Accent = Color.FromArgb(1, 104, 110);
    private static readonly Color AccentDark = Color.FromArgb(17, 48, 61);
    private static readonly Color Danger = Color.FromArgb(153, 50, 26);
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ParallelPlaybackEngine engine = new();
    private readonly UserSettings settings = UserSettings.Load();
    private readonly ComboBox source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, IntegralHeight = false, DropDownHeight = 260 };
    private readonly CheckedListBox outputs = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White
    };
    private readonly TrackBar volume = new()
    {
        Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Height = 30, Dock = DockStyle.Fill, SmallChange = 1, LargeChange = 10
    };
    private readonly NumericUpDown delay = new()
    {
        Minimum = 0, Maximum = 1000, Increment = 5, Width = 80, TextAlign = HorizontalAlignment.Right, Dock = DockStyle.Fill
    };
    private readonly Label sourceLabel = Copy("1   选择无声虚拟来源", Ink, 10.5f, FontStyle.Bold);
    private readonly Label sourceHint = Copy("只选已经安装且确认不发声的虚拟播放设备。通过 Windows 声音设置将应用的音频送到它；声桥不会安装驱动或修改默认设备。", Muted, 9f);
    private readonly LinkLabel soundSettings = new() { Text = "打开 Windows 声音设置", AutoSize = true, Margin = new Padding(0, 3, 0, 4) };
    private readonly Label delayHint = Copy("可给较快的设备手动增加延迟。蓝牙和有线的实际出声时间仍可能不同。", Muted, 9f);
    private readonly Label selection = Copy("未选择");
    private readonly Label status = Copy("准备就绪");
    private readonly Label streamNote = Copy("", Muted, 8.5f);
    private readonly Label settingsNote = Copy("选择会自动保存在此电脑");
    private readonly Label outputName = Copy("先点选一个目标设备");
    private readonly Label gainValue = Copy("100%");
    private readonly Label badge = Copy("待机");
    private readonly Button refresh = ActionButton("刷新设备");
    private readonly Button calibrate = ActionButton("测量并对齐");
    private readonly Button toggle = ActionButton("开始并行", true);
    private readonly CheckBox startupCheck = new() { Text = "登录 Windows 时启动", AutoSize = true, Margin = new Padding(0, 3, 18, 3) };
    private readonly CheckBox keepCheck = new() { Text = "自动保持同步", AutoSize = true, Margin = new Padding(0, 3, 18, 3) };
    private readonly LinkLabel pauseRetry = new() { Text = "停止自动恢复", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
    private readonly Label resumeNote = Copy("", Muted, 8.5f);
    private readonly bool startedAtSignIn;
    private readonly System.Windows.Forms.Timer watch = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 320 };
    private int engineStatusVersion;
    private bool changingPreferences;
    private ResumeApproval? manuallyStartedRoute;
    private long retryAt, onlineSince;
    private int retryStep, failedStarts;
    private IReadOnlyList<AudioDevice> devices = Array.Empty<AudioDevice>();
    private bool changingList, hasEverEnumerated, busy, active, calibrating, closing, allowClose, initialized;
    private const int NeededTargetCount = 2;
    private const int MaxStartsBetweenReconnects = 4;
    private static readonly int[] RetryMs = [2_000, 4_000, 8_000, 16_000, 30_000];
    private List<string> SavedTargets => settings.ParallelTargetIds;

    private sealed record SourceOption(string? Id, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record TargetOption(string Id, string Name, bool Online = true)
    {
        public override string ToString() => Name;
    }

    public MainForm(bool startedAtSignIn = false)
    {
        this.startedAtSignIn = startedAtSignIn;
        Text = "声桥 · 多设备并行播放";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(720, 755);
        MinimumSize = new Size(580, 650);
        BackColor = Surface;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        // A single-file release exposes its own embedded Windows executable icon.
        try
        {
            using var embeddedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (embeddedIcon is not null) Icon = (Icon)embeddedIcon.Clone();
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            // Program remains usable if a developer/debug launch has no embedded icon.
        }

        Controls.Add(BuildLayout());
        ClientSizeChanged += (_, _) => WrapHints();
        WrapHints();
        source.AccessibleName = "已有的无声虚拟输出";
        outputs.AccessibleName = "目标播放设备";
        volume.AccessibleName = "选中设备的相对音量百分比";
        delay.AccessibleName = "选中设备的附加延迟毫秒数";
        volume.MaximumSize = new Size(360, 32);
        LoadStartupPreference();
        keepCheck.Checked = settings.KeepSyncEnabled;
        startupCheck.CheckedChanged += (_, _) => StartupPreferenceChanged();
        keepCheck.CheckedChanged += (_, _) => KeepPreferenceChanged();
        pauseRetry.LinkClicked += (_, _) => StopAutomaticRecovery();
        source.SelectedIndexChanged += (_, _) => SourceChanged();
        outputs.ItemCheck += (_, e) => TargetChecked(e);
        outputs.SelectedIndexChanged += (_, _) => ShowSelectedOutput();
        volume.ValueChanged += (_, _) => VolumeChanged();
        delay.ValueChanged += (_, _) => DelayChanged();
        refresh.Click += async (_, _) => await RefreshDevices();
        soundSettings.LinkClicked += (_, _) => OpenSoundSettings();
        calibrate.Click += async (_, _) => await CalibratePlaybackAsync();
        toggle.Click += async (_, _) => await ToggleAsync();
        saveTimer.Tick += (_, _) => SaveNow();
        watch.Tick += async (_, _) => await WatchEngineAsync();
        engine.Status += ReceiveEngineStatus;
        watch.Start();
        UpdateControls();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!initialized)
        {
            initialized = true;
            await RefreshDevices();
            if (closing) return;
            if (ApprovedRoute is not null)
            {
                SetStatus(startedAtSignIn ? "已随 Windows 登录启动，正在检查已批准的输出设备…" : "正在检查已批准的输出设备…");
                await WatchEngineAsync();
            }
            else if (startedAtSignIn)
                SetStatus("已随 Windows 登录启动。未自动播放，请手动开始并行。");
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (allowClose || e.Cancel) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        watch.Stop();
        SaveNow();
        UpdateControls();
        // Do not let a hung native device enumeration trap the user in the window.
        try { await Task.Run(engine.Dispose); }
        catch (Exception) { /* The engine cannot restart after exit. */ }
        finally
        {
            engine.Status -= ReceiveEngineStatus;
            allowClose = true;
            if (!IsDisposed) Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            watch.Dispose();
            saveTimer.Dispose();
            statusToolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(22, 14, 22, 14), BackColor = AccentDark, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var eyebrow = Copy("PARALLEL AUDIO  /  WINDOWS 11", Color.FromArgb(161, 226, 221), 8.5f, FontStyle.Bold);
        var title = Copy("声桥", Color.White, 22f, FontStyle.Bold);
        var deck = Copy("同一路声音，并行发往多台播放设备。", Color.FromArgb(211, 233, 235));
        badge.ForeColor = Color.White;
        badge.BackColor = Color.FromArgb(42, 77, 85);
        badge.Padding = new Padding(12, 5, 12, 5);
        badge.Anchor = AnchorStyles.Right;
        header.Controls.Add(eyebrow, 0, 0);
        header.Controls.Add(title, 0, 1);
        header.Controls.Add(deck, 0, 2);
        header.Controls.Add(badge, 1, 1);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(22, 13, 22, 12), Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) body.RowStyles.Add(new RowStyle(row == 2 ? SizeType.Percent : SizeType.AutoSize, row == 2 ? 100 : 0));
        root.Controls.Add(body, 0, 1);

        var sourceBlock = Block(4, new Padding(0, 0, 0, 10));
        source.Margin = new Padding(0, 4, 0, 4);
        sourceBlock.Controls.Add(sourceLabel, 0, 0);
        sourceBlock.Controls.Add(source, 0, 1);
        sourceBlock.Controls.Add(sourceHint, 0, 2);
        sourceBlock.Controls.Add(soundSettings, 0, 3);
        statusToolTip.SetToolTip(soundSettings, "仅打开系统界面。请自行选择声音输出或在音量合成器中为单个应用选择所需输出。");
        body.Controls.Add(sourceBlock, 0, 0);

        var outputHeading = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 4) };
        outputHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputHeading.Controls.Add(Copy("2   勾选至少两个播放设备", Ink, 10.5f, FontStyle.Bold), 0, 0);
        selection.Anchor = AnchorStyles.Right; selection.ForeColor = Muted;
        outputHeading.Controls.Add(selection, 1, 0);
        outputs.Margin = new Padding(0);
        body.Controls.Add(outputHeading, 0, 1);
        body.Controls.Add(outputs, 0, 2);

        var outputOptions = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 2, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 6, 0, 5), BackColor = Color.FromArgb(234, 242, 247) };
        outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputName.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        outputName.Margin = new Padding(3, 0, 3, 4);
        outputName.AutoSize = false; outputName.AutoEllipsis = true; outputName.Height = 24; outputName.Dock = DockStyle.Fill;
        outputOptions.Controls.Add(outputName, 0, 0);
        outputOptions.SetColumnSpan(outputName, 5);
        volume.Margin = new Padding(3, 0, 3, 0);
        gainValue.Anchor = AnchorStyles.Left;
        outputOptions.Controls.Add(Copy("相对音量"), 0, 1);
        outputOptions.Controls.Add(volume, 1, 1);
        outputOptions.Controls.Add(gainValue, 2, 1);
        outputOptions.Controls.Add(Copy("延迟（毫秒）"), 3, 1);
        outputOptions.Controls.Add(delay, 4, 1);
        body.Controls.Add(outputOptions, 0, 3);

        statusToolTip.SetToolTip(delay, "只给此设备增加手动延迟。停止并行后修改，重新开始时生效。");
        statusToolTip.SetToolTip(outputs, "Windows 音量滑块和静音控制整体音量。这里可微调各设备；更改勾选需要先停止并行。");
        delayHint.Margin = new Padding(0, 1, 0, 9);
        body.Controls.Add(delayHint, 0, 4);

        var preferences = Block(3, new Padding(0, 4, 0, 9));
        preferences.Controls.Add(Copy("3   启动与恢复", Ink, 10.5f, FontStyle.Bold), 0, 0);
        var choices = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty
        };
        choices.Controls.Add(startupCheck);
        choices.Controls.Add(keepCheck);
        choices.Controls.Add(pauseRetry);
        preferences.Controls.Add(choices, 0, 1);
        preferences.Controls.Add(resumeNote, 0, 2);
        statusToolTip.SetToolTip(startupCheck, "为当前 Windows 用户登记启动。不会代你开启自动恢复。移动程序后，请重新勾选启动。");
        statusToolTip.SetToolTip(keepCheck, "手动确认且成功启动过这组设备后，断线或重开时可恢复同一组。点停止会取消恢复，直到手动开始。");
        body.Controls.Add(preferences, 0, 5);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        refresh.Margin = Padding.Empty;
        calibrate.Margin = new Padding(9, 0, 0, 0);
        toggle.Margin = Padding.Empty;
        actions.Controls.Add(refresh, 0, 0);
        actions.Controls.Add(calibrate, 1, 0);
        statusToolTip.SetToolTip(calibrate, "并行播放时可选。短暂用默认麦克风测量 2–4 路扬声器的实际出声时差；默认不使用麦克风。");
        actions.Controls.Add(toggle, 2, 0);
        body.Controls.Add(actions, 0, 6);

        var footer = Block(3, new Padding(0, 8, 0, 0));
        status.AutoEllipsis = true; status.AutoSize = false; status.Height = 27; status.Dock = DockStyle.Fill;
        settingsNote.ForeColor = Muted; settingsNote.Margin = Padding.Empty; settingsNote.Font = new Font("Microsoft YaHei UI", 8.5f);
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(streamNote, 0, 1);
        footer.Controls.Add(settingsNote, 0, 2);
        body.Controls.Add(footer, 0, 7);
        return root;
    }

    private void WrapHints()
    {
        var preferred = new Size(Math.Max(170, ClientSize.Width - (int)Math.Ceiling(46 * DeviceDpi / 96.0)), 0);
        sourceHint.MaximumSize = preferred;
        delayHint.MaximumSize = preferred;
        streamNote.MaximumSize = preferred;
        resumeNote.MaximumSize = preferred;
    }

    private static TableLayoutPanel Block(int rows, Padding padding)
    {
        var result = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = rows, Margin = Padding.Empty, Padding = padding };
        result.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++) result.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return result;
    }

    private static Label Copy(string text, Color? color = null, float size = 9.5f, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color ?? Ink, Margin = new Padding(0, 2, 0, 2)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat,
            AutoSize = true, MinimumSize = new Size(primary ? 122 : 104, 38), Padding = new Padding(11, 3, 11, 3),
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
            Font = new Font("Microsoft YaHei UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular)
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(172, 189, 204);
        return b;
    }

    private ResumeApproval? ApprovedRoute => settings.ApprovedForCurrentRoute();

    private string[] WantedTargetIds() => SavedTargets.Where(id => !IdComparer.Equals(id, settings.ParallelSourceId)).ToArray();

    private void LoadStartupPreference()
    {
        try { startupCheck.Checked = StartupRegistration.IsEnabled(); }
        catch (Exception ex)
        {
            SetStatus("无法读取登录启动设置：" + SingleLine(ex.Message), true);
        }
    }

    private void StartupPreferenceChanged()
    {
        if (changingPreferences || closing) return;
        var enabled = startupCheck.Checked;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            SetStatus(enabled ? "已开启登录启动。是否恢复播放由「自动保持同步」单独控制。" : "已关闭登录启动。");
        }
        catch (Exception ex)
        {
            changingPreferences = true;
            try
            {
                try { startupCheck.Checked = StartupRegistration.IsEnabled(); }
                catch (Exception) { startupCheck.Checked = !enabled; }
            }
            finally { changingPreferences = false; }
            SetStatus("无法更改登录启动：" + SingleLine(ex.Message), true);
        }
    }

    private void KeepPreferenceChanged()
    {
        if (changingPreferences || closing) return;
        settings.KeepSyncEnabled = keepCheck.Checked;
        settings.KeepSyncRoute = keepCheck.Checked && active && manuallyStartedRoute is { } started &&
            started.Matches(settings.ParallelSourceId, WantedTargetIds()) ? started : null;
        ResetRetry();
        SaveNow();
        UpdateControls();
        SetStatus(!settings.KeepSyncEnabled ? "自动恢复已关闭。不会因此停止当前播放。"
            : ApprovedRoute is not null ? "已启用这组设备的自动恢复。"
            : "已启用保持同步。请手动确认并成功开始这组设备以授权恢复。");
    }

    private void ClearResumeIntent()
    {
        manuallyStartedRoute = null;
        settings.StopAutomaticResume();
        ResetRetry();
        SaveNow();
    }

    private void StopAutomaticRecovery()
    {
        if (busy || closing || ApprovedRoute is null) return;
        if (active) return; // The ordinary Stop button handles a live stream.
        ClearResumeIntent();
        SetStatus("已停止自动恢复。下一次请手动开始并行。");
        UpdateControls();
    }

    private void ResetRetry()
    {
        retryAt = onlineSince = 0;
        retryStep = failedStarts = 0;
    }

    private void ScheduleRetry()
    {
        retryAt = Environment.TickCount64 + RetryMs[Math.Min(retryStep, RetryMs.Length - 1)];
        if (retryStep < RetryMs.Length - 1) retryStep++;
    }

    private void OpenSoundSettings()
    {
        try { using var opened = Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus("无法打开 Windows 声音设置，请从「设置 → 系统 → 声音」手动打开。", true);
        }
    }

    private async Task RefreshDevices()
    {
        if (busy || active || closing) return;
        busy = true;
        SetStatus("正在读取 Windows 播放设备…");
        UpdateControls();
        try
        {
            devices = await Task.Run(AudioDeviceCatalog.GetDevices);
            hasEverEnumerated = true;
            if (closing) return;
            RebuildDeviceChoices();
            var sourceId = ResolveSource();
            var availableTargets = devices.Count(d => !IdComparer.Equals(d.Id, sourceId));
            SetStatus(devices.Count == 0 ? "未检测到播放设备。请先在 Windows 中连接 USB 或配对蓝牙音响，再刷新。"
                : sourceId is null ? "请明确选择一个在线的无声虚拟来源。"
                : availableTargets < NeededTargetCount ? "可用播放设备不足两个。请连接第二个输出后刷新。"
                : "找到 " + devices.Count + " 个输出。请勾选实际要发声的设备。", devices.Count == 0 || availableTargets < NeededTargetCount);
        }
        catch (Exception ex)
        {
            if (!closing) SetStatus("读取设备失败：" + ex.Message, true);
        }
        finally
        {
            busy = false;
            if (!closing) UpdateControls();
        }
    }

    private void RebuildDeviceChoices()
    {
        changingList = true;
        source.BeginUpdate();
        try
        {
            source.Items.Clear();
            source.Items.Add(new SourceOption(null, "请选择已有的无声虚拟输出…"));
            foreach (var d in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, IdComparer))
                source.Items.Add(new SourceOption(d.Id, DisplayDevice(d)));
            var wanted = settings.ParallelSourceId;
            var chosen = source.Items.Cast<SourceOption>().ToList().FindIndex(x => IdComparer.Equals(x.Id, wanted));
            if (chosen < 0)
                chosen = source.Items.Add(new SourceOption(wanted, "上次选择的声音来源 · 未连接 · " + AudioRoutePolicy.LabelId(wanted!)));
            source.SelectedIndex = chosen;
            RebuildOutputChoices();
        }
        finally
        {
            source.EndUpdate();
            changingList = false;
        }
        ShowSelectedOutput();
    }

    private string? ResolveSource() => AudioRoutePolicy.ResolvePinned(settings.ParallelSourceId, devices.Select(d => d.Id));

    private string DisplayDevice(AudioDevice d) =>
        devices.Count(x => StringComparer.CurrentCultureIgnoreCase.Equals(x.Name, d.Name)) > 1
            ? d.Name + " · " + AudioRoutePolicy.LabelId(d.Id)
            : d.Name;

    private void RebuildOutputChoices()
    {
        var previous = (outputs.SelectedItem as TargetOption)?.Id;
        var src = ResolveSource();
        outputs.BeginUpdate();
        try
        {
            outputs.Items.Clear();
            foreach (var d in devices.Where(d => !IdComparer.Equals(d.Id, src)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var selected = SavedTargets.Contains(d.Id, IdComparer);
                if (selected) settings.For(d.Id).LastName = d.Name;
                var i = outputs.Items.Add(new TargetOption(d.Id, DisplayDevice(d)), selected);
                if (IdComparer.Equals(previous, d.Id)) outputs.SelectedIndex = i;
            }
            var offline = WantedTargetIds().Where(id => !devices.Any(d => IdComparer.Equals(d.Id, id))).ToArray();
            for (int j = 0; j < offline.Length; j++)
            {
                var id = offline[j];
                var name = settings.For(id).LastName ?? $"上次勾选的设备 {j + 1}";
                var i = outputs.Items.Add(new TargetOption(id, name + "（离线，可取消勾选）", false), true);
                if (IdComparer.Equals(previous, id)) outputs.SelectedIndex = i;
            }
            if (outputs.SelectedIndex < 0 && outputs.Items.Count > 0)
            {
                var firstSaved = outputs.Items.Cast<TargetOption>().ToList().FindIndex(x => SavedTargets.Contains(x.Id, IdComparer));
                outputs.SelectedIndex = Math.Max(0, firstSaved);
            }
        }
        finally { outputs.EndUpdate(); }
    }

    private void SourceChanged()
    {
        if (changingList || source.SelectedItem is not SourceOption choice) return;
        if (!IdComparer.Equals(settings.ParallelSourceId, choice.Id))
        {
            settings.ParallelSourceId = choice.Id;
            SavedTargets.RemoveAll(id => IdComparer.Equals(id, choice.Id));
            ClearResumeIntent();
        }
        changingList = true;
        try { RebuildOutputChoices(); }
        finally { changingList = false; }
        QueueSave();
        ShowSelectedOutput();
        UpdateControls();
        if (ResolveSource() is null)
            SetStatus(choice.Id is null ? "请明确选择一个在线的无声虚拟来源。" : "所选来源未连接。重新连接后请刷新设备。");
        else SetStatus("请确认音频已送到该虚拟输出，并勾选至少两个播放设备。");
    }

    private void TargetChecked(ItemCheckEventArgs e)
    {
        if (changingList || outputs.Items[e.Index] is not TargetOption d) return;
        if (active)
        {
            e.NewValue = e.CurrentValue;
            return;
        }
        if (!d.Online && e.NewValue == CheckState.Checked)
        {
            e.NewValue = e.CurrentValue;
            return;
        }
        var hadTarget = SavedTargets.Contains(d.Id, IdComparer);
        var nowTarget = e.NewValue == CheckState.Checked;
        if (hadTarget == nowTarget) return;
        SavedTargets.RemoveAll(x => IdComparer.Equals(d.Id, x));
        if (nowTarget)
        {
            SavedTargets.Add(d.Id);
            settings.For(d.Id).LastName = d.Name;
        }
        ClearResumeIntent();
        UpdateControls();
    }

    private void ShowSelectedOutput()
    {
        if (changingList) return;
        var target = outputs.SelectedItem as TargetOption;
        changingList = true;
        try
        {
            var prefs = target is null ? new OutputSettings() : settings.For(target.Id);
            outputName.Text = target?.Name ?? "先点选一个目标设备";
            volume.Value = prefs.Volume;
            delay.Value = prefs.DelayMs;
            gainValue.Text = prefs.Volume + "%";
        }
        finally { changingList = false; }
        UpdateControls();
    }

    private void VolumeChanged()
    {
        if (changingList || outputs.SelectedItem is not TargetOption d) return;
        settings.For(d.Id).Volume = volume.Value;
        gainValue.Text = volume.Value + "%";
        QueueSave();
        if (active)
        {
            try { engine.SetVolume(d.Id, volume.Value); }
            catch (Exception ex) { SetStatus("无法调整此设备音量：" + ex.Message, true); }
        }
    }

    private void DelayChanged()
    {
        if (changingList || outputs.SelectedItem is not TargetOption d) return;
        var prefs = settings.For(d.Id);
        prefs.DelayMs = decimal.ToInt32(delay.Value);
        QueueSave();
    }

    private List<RelayTarget> RequestedTargets() => outputs.Items.Cast<TargetOption>()
        .Where(x => x.Online && SavedTargets.Contains(x.Id, IdComparer))
        .Select(x => { var p = settings.For(x.Id); return new RelayTarget(x.Id, p.Volume, p.DelayMs); }).ToList();

    private void UpdateControls()
    {
        var selectedCount = outputs.Items.Cast<TargetOption>().Count(x => x.Online && SavedTargets.Contains(x.Id, IdComparer));
        var offlineCount = SavedTargets.Count(x => !devices.Any(d => IdComparer.Equals(d.Id, x)));
        selection.Text = "已选 " + selectedCount + " 个" + (offlineCount > 0 && hasEverEnumerated ? " · " + offlineCount + " 个离线" : "");
        source.Enabled = hasEverEnumerated && !busy && !active && !closing && source.Items.Count > 0;
        // Keep the list readable while running; checkbox edits apply after a stop.
        outputs.Enabled = hasEverEnumerated && !busy && !closing && outputs.Items.Count > 0;
        refresh.Enabled = !busy && !active && !closing;
        startupCheck.Enabled = !closing;
        keepCheck.Enabled = !busy && !closing;
        pauseRetry.Visible = !active && ApprovedRoute is not null;
        pauseRetry.Enabled = !busy && !closing;
        resumeNote.Text = !settings.KeepSyncEnabled ? "关闭时不会在断线或重开后自动播放。"
            : ApprovedRoute is not null
                ? "仅恢复这组已确认的设备；点停止后，重开也不会自动播放。"
                : "需手动确认并成功开始这组设备，才会自动恢复。";
        var editing = !busy && !calibrating && !closing && outputs.SelectedItem is TargetOption { Online: true };
        volume.Enabled = editing;
        delay.Enabled = editing && !active;
        UpdateCalibrationButton();
        toggle.Enabled = !busy && !closing && (active || (ResolveSource() is not null && selectedCount >= NeededTargetCount && selectedCount == WantedTargetIds().Length));
        toggle.Text = active ? "停止并行" : "开始并行";
        badge.Text = closing ? "正在退出" : busy ? "处理中" : active ? "播放中" : ApprovedRoute is not null ? "等待恢复" : "待机";
        badge.BackColor = active ? Accent : Color.FromArgb(42, 77, 85);
        if (!active) streamNote.Visible = false;
    }

    private void UpdateCalibrationButton()
    {
        calibrate.Enabled = !busy && !calibrating && !closing && active && engine.GetDiagnostics().Count is >= 2 and <= 4;
        calibrate.Text = calibrating ? "测量中…" : "测量并对齐";
    }

    private async Task CalibratePlaybackAsync()
    {
        if (busy || calibrating || closing || !engine.IsRunning || engine.GetDiagnostics().Count is < 2 or > 4) return;
        const string consent = "声桥会暂时暂停镜像播放，由各播放设备依次发出短促的测试音，用 Windows 默认麦克风测量出声时间，约需 10–20 秒。\n\n"
            + "请把麦克风放在你的听音位置，保持安静，把 Windows 音量调到舒适且可听的水平。测量在本机完成，不保存或上传录音。\n\n"
            + "只有验证通过才会应用本次对齐，不会改动已保存的手动延迟。测量期间可点击「停止并行」退出。现在开始吗？";
        if (MessageBox.Show(this, consent, "使用默认麦克风测量", MessageBoxButtons.YesNo,
                MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (closing || !engine.IsRunning || engine.GetDiagnostics().Count is < 2 or > 4) return;
        calibrating = true;
        SetStatus("正在测量实际出声时差。测量时可点击「停止并行」。");
        UpdateControls();
        try
        {
            var result = await engine.CalibrateAsync();
            if (closing || !engine.IsRunning) return;
            SetStatus(result.Message, !result.Applied);
            var routeDetails = string.Join("\n", result.Routes.Select(r => $"{r.Name}：补偿 {r.AdditionalDelayMs} ms"));
            var detail = string.IsNullOrEmpty(routeDetails) ? result.Message : result.Message + "\n\n" + routeDetails;
            MessageBox.Show(this, detail, "声学对齐结果", MessageBoxButtons.OK,
                result.Applied ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            if (!closing && engine.IsRunning) SetStatus("未能完成测量：" + SingleLine(ex.Message), true);
        }
        finally
        {
            calibrating = false;
            if (!closing) UpdateControls();
        }
    }

    private Form CreateDialog(string title, Size size) => new()
    {
        Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = size, MinimumSize = size,
        ShowInTaskbar = false, ShowIcon = false, MaximizeBox = false, MinimizeBox = false,
        AutoScaleMode = AutoScaleMode.Dpi, BackColor = Surface, ForeColor = Ink, Font = Font
    };

    private static FlowLayoutPanel DialogActions(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0, 7, 0, 0)
        };
        for (var i = buttons.Length - 1; i >= 0; i--) row.Controls.Add(buttons[i]);
        return row;
    }

    private bool ConfirmParallelStart(string sourceId, IReadOnlyList<RelayTarget> targets)
    {
        using var dialog = CreateDialog("确认开始并行输出", new Size(640, 365));
        dialog.MinimumSize = new Size(400, 260);
        var frame = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dialog.Controls.Add(frame);

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        frame.Controls.Add(body, 0, 0);
        var panel = Block(5, new Padding(20, 14, 20, 12));
        panel.Dock = DockStyle.Top;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        body.Controls.Add(panel);
        var title = Copy("确认无声虚拟来源", Ink, 13f, FontStyle.Bold);
        var matched = devices.FirstOrDefault(d => IdComparer.Equals(d.Id, sourceId));
        var name = matched is null ? AudioRoutePolicy.LabelId(sourceId) : DisplayDevice(matched);
        var sourceInfo = Copy("明确选择的来源：" + name, Ink);
        var targetInfo = Copy("实际发声设备：" + string.Join("、", targets.Select(t =>
            devices.FirstOrDefault(d => IdComparer.Equals(d.Id, t.Id)) is { } target ? DisplayDevice(target) : "选中设备")), Ink);
        var text = "请先在 Windows 声音设置中把程序的输出送到这一个虚拟播放设备。声桥不会更改系统设置、安装驱动或在该来源离线时改用其他设备。";
        if (settings.KeepSyncEnabled) text += "「自动保持同步」已勾选：这组设备全部成功启动后，声桥可在断线或重开时仅恢复同一组。";
        var detail = Copy(text, Muted, 9f);
        var confirmed = new CheckBox
        {
            Text = "我确认所选虚拟来源不会发声",
            AutoSize = true, Margin = new Padding(0, 11, 0, 5),
            AccessibleDescription = "确认本次选择的来源是没有实际声音的虚拟输出，而不是音响或内置扬声器。"
        };
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(sourceInfo, 0, 1);
        panel.Controls.Add(targetInfo, 0, 2);
        panel.Controls.Add(detail, 0, 3);
        panel.Controls.Add(confirmed, 0, 4);

        var go = ActionButton("开始并行", true);
        go.DialogResult = DialogResult.OK;
        go.Enabled = false;
        confirmed.CheckedChanged += (_, _) => go.Enabled = confirmed.Checked;
        var cancel = ActionButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        var actions = DialogActions(cancel, go);
        actions.Padding = new Padding(16, 6, 16, 10);
        actions.Margin = Padding.Empty;
        actions.BackColor = Color.FromArgb(234, 242, 247);
        actions.WrapContents = true;
        // Only the instructions and checkbox scroll; the buttons stay anchored.
        frame.Controls.Add(actions, 0, 1);
        void Reflow()
        {
            var available = Math.Max(100, body.ClientSize.Width - panel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 12);
            foreach (var content in new Control[] { title, sourceInfo, targetInfo, detail, confirmed })
                content.MaximumSize = new Size(available, 0);
        }
        body.SizeChanged += (_, _) => Reflow();
        dialog.AcceptButton = go;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            dialog.MinimumSize = new Size(Math.Min(dialog.MinimumSize.Width, area.Width), Math.Min(dialog.MinimumSize.Height, area.Height));
            dialog.Size = new Size(Math.Min(dialog.Width, area.Width), Math.Min(dialog.Height, area.Height));
            dialog.Location = new Point(
                Math.Clamp(dialog.Left, area.Left, Math.Max(area.Left, area.Right - dialog.Width)),
                Math.Clamp(dialog.Top, area.Top, Math.Max(area.Top, area.Bottom - dialog.Height)));
            Reflow();
            cancel.Focus();
            body.AutoScrollPosition = Point.Empty;
        };
        return dialog.ShowDialog(this) == DialogResult.OK && confirmed.Checked;
    }

    private async Task ToggleAsync()
    {
        if (busy || closing) return;
        var wasActive = active;
        busy = true;
        UpdateControls();
        try
        {
            if (wasActive)
            {
                ClearResumeIntent();
                SetStatus("正在停止并行输出…");
                await Task.Run(engine.Stop);
                active = false;
                if (!closing) SetStatus("已停止并行输出和自动恢复。再次播放请手动开始。");
            }
            else
            {
                SetStatus("正在打开所选设备…");
                devices = await Task.Run(AudioDeviceCatalog.GetDevices);
                if (closing) return;
                RebuildDeviceChoices();
                var src = ResolveSource();
                var targets = RequestedTargets();
                if (src is null)
                {
                    SetStatus("请明确选择一个在线的无声虚拟来源。", true);
                    return;
                }
                if (targets.Count < NeededTargetCount || targets.Count != WantedTargetIds().Length)
                {
                    SetStatus("请连接全部勾选的设备，或取消勾选离线设备。至少需要两路输出。", true);
                    return;
                }
                if (!ConfirmParallelStart(src, targets))
                {
                    SetStatus("未开启并行输出。");
                    return;
                }
                if (closing) return;
                SaveNow();
                var statusBeforeStart = engineStatusVersion;
                await Task.Run(() => engine.Start(src, targets));
                active = engine.IsRunning;
                if (closing) return;
                var attempted = ResumeApproval.For(src, targets.Select(t => t.Id));
                if (active && AllOutputsRunning(attempted))
                {
                    manuallyStartedRoute = attempted;
                    if (settings.KeepSyncEnabled) settings.KeepSyncRoute = attempted;
                    ResetRetry();
                    SaveNow();
                }
                if (engineStatusVersion == statusBeforeStart)
                    SetStatus(active ? "正在并行输出。用 Windows 音量滑块调整整体音量，声音仍需送到该虚拟来源。" : "启动未完成。请检查设备是否仍连接。", !active);
            }
        }
        catch (Exception ex)
        {
            active = engine.IsRunning;
            if (!closing) SetStatus((wasActive ? "停止失败：" : "启动失败：") + SingleLine(ex.Message), true);
        }
        finally
        {
            busy = false;
            if (!closing) UpdateControls();
        }
    }

    private void ReceiveEngineStatus(string text)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (closing || IsDisposed) return;
                engineStatusVersion++;
                SetStatus(SingleLine(text), true);
                if (!busy && active && !engine.IsRunning) { active = false; UpdateControls(); }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private bool AllOutputsRunning(ResumeApproval route) => engine.IsRunning &&
        new HashSet<string>(engine.GetDiagnostics().Select(d => d.TargetId), IdComparer).SetEquals(route.TargetIds);

    private async Task WatchEngineAsync()
    {
        if (!initialized || closing || busy) return;
        var running = engine.IsRunning;
        if (active != running)
        {
            active = running;
            UpdateControls();
            if (!active && status.ForeColor != Danger)
                SetStatus("并行输出已中断。", true);
        }
        if (running)
        {
            var budget = engine.CommonLeadMs;
            var live = engine.GetDiagnostics().Count;
            streamNote.Text = budget <= 0 ? "等待虚拟来源送来音频…"
                : $"活跃输出：{live} 路　共同排程：{budget} ms（软件缓冲，不是声学时差）";
            streamNote.Visible = true;
        }
        UpdateCalibrationButton();
        if (calibrating) return;

        var route = ApprovedRoute;
        if (route is null) return;
        if (AllOutputsRunning(route))
        {
            ResetRetry();
            return;
        }
        if (Environment.TickCount64 < retryAt) return;
        await RetryRouteAsync(route);
    }

    private async Task RetryRouteAsync(ResumeApproval route)
    {
        if (busy || closing || !ReferenceEquals(ApprovedRoute, route)) return;
        busy = true;
        UpdateControls();
        try
        {
            // Enumeration is a presence check, not proof the peer stream is healthy.
            var observed = await Task.Run(AudioDeviceCatalog.GetDevices);
            if (closing || !ReferenceEquals(ApprovedRoute, route)) return;
            if (!SameDeviceInventory(devices, observed))
            {
                devices = observed;
                hasEverEnumerated = true;
                RebuildDeviceChoices();
            }
            if (AllOutputsRunning(route))
            {
                ResetRetry();
                return;
            }
            var src = route.SourceId!;
            if (!AudioRoutePolicy.AllActive(src, route.TargetIds, observed.Select(d => d.Id)))
            {
                onlineSince = 0;
                failedStarts = 0; // A new reconnection may clear a driver fault.
                ScheduleRetry();
                SetStatus(engine.IsRunning ? "部分设备离线，保留现有输出，等待原设备恢复。"
                    : "等待原来设备上线。声桥不会自动改用其他输出。");
                return;
            }
            long now = Environment.TickCount64;
            if (onlineSince == 0)
            {
                onlineSince = now;
                retryAt = now + 2_000;
                SetStatus("原设备已上线，正在等待连接稳定…");
                return;
            }
            if (failedStarts >= MaxStartsBetweenReconnects)
            {
                retryAt = now + RetryMs[^1];
                SetStatus("多次恢复失败，暂不反复重开。请重连设备，或停止自动恢复后手动启动。", true);
                return;
            }

            var targets = route.TargetIds.Select(id =>
            {
                var p = settings.For(id);
                return new RelayTarget(id, p.Volume, p.DelayMs);
            }).ToArray();
            if (closing || !ReferenceEquals(ApprovedRoute, route)) return;
            SetStatus("正在恢复同一组已确认的设备…");
            var startVersion = engineStatusVersion;
            // Start stops a degraded session only when all exact devices are online.
            failedStarts++;
            await Task.Run(() => engine.Start(src, targets));
            active = engine.IsRunning;
            if (closing || !ReferenceEquals(ApprovedRoute, route))
            {
                await Task.Run(engine.Stop);
                active = false;
                return;
            }
            if (AllOutputsRunning(route))
            {
                ResetRetry();
                if (engineStatusVersion == startVersion)
                    SetStatus("已恢复原来的并行播放设备。用 Windows 音量滑块调整整体音量。");
                return;
            }
            ScheduleRetry();
            if (engineStatusVersion == startVersion)
                SetStatus("仍缺少部分输出，稍后重试。", true);
        }
        catch (Exception ex)
        {
            active = engine.IsRunning;
            ScheduleRetry();
            if (!closing) SetStatus("自动恢复未成功：" + SingleLine(ex.Message), true);
        }
        finally
        {
            busy = false;
            if (!closing) UpdateControls();
        }
    }

    private static bool SameDeviceInventory(IReadOnlyList<AudioDevice> left, IReadOnlyList<AudioDevice> right)
        => left.Count == right.Count && new HashSet<string>(left.Select(d => d.Id), IdComparer).SetEquals(right.Select(d => d.Id));

    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    private void SetStatus(string text, bool isError = false)
    {
        status.Text = text;
        status.AccessibleDescription = text;
        status.ForeColor = isError ? Danger : Ink;
        statusToolTip.SetToolTip(status, text);
    }

    private readonly ToolTip statusToolTip = new() { AutoPopDelay = 12000 };

    private void QueueSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveNow()
    {
        saveTimer.Stop();
        var ok = settings.TrySave(out var error);
        settingsNote.ForeColor = ok ? Muted : Danger;
        settingsNote.Text = ok ? "设备、音量、延迟及启动与恢复偏好保存在此电脑。" : error;
    }
}
