using AudioRelay;

static void Near(float actual, float expected, string scenario)
{
    if (MathF.Abs(actual - expected) > 0.0001f || !float.IsFinite(actual))
        throw new InvalidOperationException($"{scenario}: expected {expected}, got {actual}");
}

var virtualSource = MasterVolumeSnapshot.FromEndpoint("virtual", -6, 0, false);
Near(virtualSource.GainFor("bluetooth"), .501187f, "half amplitude on BT");
Near(virtualSource.GainFor("usb"), .501187f, "both mirrors follow default");
Near(virtualSource.GainFor("bluetooth") * .5f, .250593f, "relative volume is preserved");

var physicalDefault = MasterVolumeSnapshot.FromEndpoint("USB", -30, -10, false);
Near(physicalDefault.GainFor("usb"), 1, "do not double-apply actual render endpoint volume");
Near(physicalDefault.GainFor("bluetooth"), .1f, "track changed default and normalize range");

Near(MasterVolumeSnapshot.FromEndpoint("virtual", 2, 0, false).GainFor("usb"), 1, "never amplify");
Near(MasterVolumeSnapshot.FromEndpoint("virtual", -6, 0, true).GainFor("usb"), 0, "mute");
Near(MasterVolumeSnapshot.FromEndpoint("virtual", float.NaN, 0, false).GainFor("usb"), 0, "invalid endpoint data");
Near(MasterVolumeSnapshot.Unavailable.GainFor("usb"), 0, "unavailable endpoint fails closed");
Console.WriteLine("Master-volume gain and routing checks passed.");
