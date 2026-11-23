using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioRelay;

internal sealed class UserSettings
{
    public UserSettings() { }

    public string? ParallelSourceId { get; set; }
    public List<string> ParallelTargetIds { get; set; } = new();
    public bool KeepSyncEnabled { get; set; }
    public ResumeApproval? KeepSyncRoute { get; set; }
    public Dictionary<string, OutputSettings> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Round-trip settings written by other versions without interpreting them.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherValues { get; set; }

    internal void StopAutomaticResume() => KeepSyncRoute = null;

    internal ResumeApproval? ApprovedForCurrentRoute() =>
        KeepSyncEnabled && KeepSyncRoute is { } route && route.Matches(ParallelSourceId, ParallelTargetIds) ? route : null;

    public OutputSettings For(string endpointId)
    {
        if (!Outputs.TryGetValue(endpointId, out var prefs) || prefs is null)
            Outputs[endpointId] = prefs = new();
        return prefs;
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioRelay", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (string.IsNullOrWhiteSpace(value.ParallelSourceId)) value.ParallelSourceId = null;
            value.ParallelTargetIds = NormalizeIds(value.ParallelTargetIds);
            value.ParallelTargetIds.RemoveAll(id => string.Equals(id, value.ParallelSourceId, StringComparison.OrdinalIgnoreCase));
            if (value.KeepSyncEnabled && value.KeepSyncRoute is { } route)
            {
                route.TargetIds = NormalizeIds(route.TargetIds);
                if (!route.Matches(value.ParallelSourceId, value.ParallelTargetIds)) value.KeepSyncRoute = null;
            }
            else value.KeepSyncRoute = null;
            value.Outputs = (value.Outputs ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value is not null)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            foreach (var prefs in value.Outputs.Values)
            {
                prefs.Volume = Math.Clamp(prefs.Volume, 0, 100);
                prefs.DelayMs = Math.Clamp(prefs.DelayMs, 0, 1000);
            }
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal static List<string> NormalizeIds(IEnumerable<string>? ids) => (ids ?? [])
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool TrySave(out string error)
    {
        string? pending = null;
        error = "";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            pending = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(pending, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "无法写入本地设置，本次选择仍可使用。";
            return false;
        }
        finally
        {
            if (pending is not null)
            {
                try { if (File.Exists(pending)) File.Delete(pending); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

internal sealed class OutputSettings
{
    public OutputSettings() { }

    public int Volume { get; set; } = 100;
    public int DelayMs { get; set; }
    public string? LastName { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherValues { get; set; }
}

internal sealed class ResumeApproval
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public string? SourceId { get; set; }
    public List<string> TargetIds { get; set; } = new();

    public bool Matches(string? sourceId, IEnumerable<string>? targets)
    {
        if (string.IsNullOrWhiteSpace(SourceId) || !IdComparer.Equals(sourceId, SourceId) || TargetIds is null) return false;
        var expected = UserSettings.NormalizeIds(TargetIds);
        var actual = UserSettings.NormalizeIds(targets);
        return expected.Count >= 2 && !expected.Contains(SourceId, IdComparer)
            && new HashSet<string>(expected, IdComparer).SetEquals(actual);
    }

    public static ResumeApproval For(string source, IEnumerable<string> targets) => new()
    {
        SourceId = source,
        TargetIds = UserSettings.NormalizeIds(targets)
    };
}
