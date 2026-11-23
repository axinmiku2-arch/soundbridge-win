using AudioRelay;
using System.Text.Json;

var all = new[] { "SilentVirtual", "physical-right", "physical-left" };
var two = all[1..];
Require(AudioRoutePolicy.ResolvePinned("silentvirtual", all) == all[0], "resolve canonical endpoint ID");
Require(AudioRoutePolicy.ResolvePinned(null, all) is null, "no default endpoint fallback without a selection");
Require(AudioRoutePolicy.ResolvePinned("replaced-virtual", all) is null, "do not pick another endpoint by name/order");
Require(AudioRoutePolicy.ResolvePinned("SilentVirtual", two) is null, "offline selected endpoint does not fall back to audible output");
Require(AudioRoutePolicy.AllActive("SILENTVIRTUAL", two, all), "source and two exact active render destinations");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", two, two), "active-render list excludes capture-only and offline source");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", two, all[..2]), "do not partially start a selected route");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", [two[0], two[0].ToUpperInvariant()], all), "duplicates invalid");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", [all[0], two[0]], all), "feedback to source prohibited");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", [two[0], ""], all), "empty device ID invalid");
Require(!AudioRoutePolicy.AllActive("SilentVirtual", [], all), "no active targets");
Require(AudioRoutePolicy.RequireTargets(all[0], two).SequenceEqual(two), "valid target order preserved");
Reject(() => AudioRoutePolicy.RequireTargets(all[0], [all[0].ToUpperInvariant(), ..two]), "do not silently filter a feedback request");
Reject(() => AudioRoutePolicy.RequireTargets(all[0], [two[0], two[0].ToUpperInvariant(), two[1]]), "do not silently de-duplicate output");
Reject(() => AudioRoutePolicy.RequireTargets(all[0], [two[0], " ", two[1]]), "do not silently filter empty output");
Reject(() => AudioRoutePolicy.RequireTargets(all[0], Enumerable.Range(0,33).Select(x => x.ToString())), "bounded route size");
Require(AudioRoutePolicy.LabelId(all[0]) == AudioRoutePolicy.LabelId(all[0].ToLowerInvariant()), "display label independent of ID casing");
Require(AudioRoutePolicy.LabelId(two[0]) != AudioRoutePolicy.LabelId(two[1]), "similarly named devices can get an independent label");
var original = new UserSettings { ParallelSourceId = all[0], ParallelTargetIds = [..two], KeepSyncEnabled = true, KeepSyncRoute = ResumeApproval.For(all[0], two) };
var loaded = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(original))!;
Require(loaded.ApprovedForCurrentRoute() is not null, "configured route survives a local settings round-trip");
loaded.ParallelSourceId = "different-silent-source";
Require(loaded.ApprovedForCurrentRoute() is null, "changing pinned device invalidates older auto-resume approval");
Console.WriteLine("PASS pinned-source: exact ID, offline/default/mismatch, duplicate/cycle, all-or-none, settings and resume.");
return;
static void Require(bool condition, string test)
{
    if (!condition) throw new Exception(test);
}
static void Reject(Action action, string test)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception(test);
}
