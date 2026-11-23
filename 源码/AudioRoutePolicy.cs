namespace AudioRelay;

/// <summary>A route names exactly one previously confirmed render endpoint and its render destinations.</summary>
internal static class AudioRoutePolicy
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;

    internal static string[] RequireTargets(string sourceId, IEnumerable<string> targetIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(targetIds);
        var targets = targetIds.ToArray();
        if (targets.Length is < 2 or > 32)
            throw new InvalidOperationException("并行播放需要 2–32 个不同的目标设备。");
        var seen = new HashSet<string>(Ids) { sourceId };
        foreach (var target in targets)
            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
                throw new InvalidOperationException("目标设备不能为空、重复或与来源相同。");
        return targets;
    }

    // MMDevice endpoint ID strings are opaque; do not derive topology or direction from them.
    internal static string? ResolvePinned(string? selectedId, IEnumerable<string> activeRenderIds) =>
        string.IsNullOrWhiteSpace(selectedId) ? null : activeRenderIds.FirstOrDefault(x => Ids.Equals(x, selectedId));

    internal static bool AllActive(string? selectedId, IEnumerable<string> targets, IEnumerable<string> activeRenderIds)
    {
        if (string.IsNullOrWhiteSpace(selectedId)) return false;
        var active = new HashSet<string>(activeRenderIds, Ids);
        var ids = targets.ToArray();
        return ids.Length is >= 2 and <= 32 && ids.Distinct(Ids).Count() == ids.Length
            && active.Contains(selectedId)
            && ids.All(id => !string.IsNullOrWhiteSpace(id) && !Ids.Equals(id, selectedId) && active.Contains(id));
    }

    // Display a stable discriminator for identically named devices, without interpreting an opaque ID.
    internal static string LabelId(string id)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 5));
    }
}
