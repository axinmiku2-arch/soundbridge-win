using AudioRelay;
using System.Text.Json;

static void Assert(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS  " + label);
}

var old = JsonSerializer.Deserialize<UserSettings>("""
{"ParallelSourceId":"silent","ParallelTargetIds":["left","right"],"Outputs":{"left":{"Volume":75,"DelayMs":8}}}
""")!;
Assert(!old.KeepSyncEnabled && old.KeepSyncRoute is null, "v2.0 settings do not opt in by themselves");
var approved = ResumeApproval.For("silent", new[]{"left","right"});
Assert(approved.Matches("SILENT", new[]{"RIGHT","LEFT","left"}), "same endpoint set independent of ordering and case");
Assert(!approved.Matches("desktop", new[]{"left","right"}), "different source does not match");
Assert(!approved.Matches("silent", new[]{"left","usb"}), "replaced target does not match");
Assert(!approved.Matches("silent", new[]{"left","right","usb"}), "added target does not match");
Assert(!approved.Matches("silent", new[]{"left"}), "missing target does not match");
Assert(!ResumeApproval.For("silent", new[]{"silent","right"}).Matches("silent", new[]{"silent","right"}), "source cannot be a target");

old.KeepSyncEnabled = true;
old.KeepSyncRoute = approved;
var renewed = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(old))!;
Assert(renewed.KeepSyncEnabled && renewed.KeepSyncRoute!.Matches(renewed.ParallelSourceId, renewed.ParallelTargetIds),
       "approved route survives JSON roundtrip");
Assert(renewed.Outputs["left"].Volume==75 && renewed.Outputs["left"].DelayMs==8, "volume and delay migrate");
renewed.StopAutomaticResume();
var stopped = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(renewed))!;
Assert(stopped.KeepSyncEnabled && stopped.KeepSyncRoute is null, "Stop suppresses resume without turning off feature");
