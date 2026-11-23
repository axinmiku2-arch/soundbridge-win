using AudioRelay;
const long Ticks=10_000_000;
const long Epoch=365L*24*60*60*Ticks;
const int SourceRate=48000, Packet=480;
static void Check(bool condition,string text) { if(!condition) throw new Exception(text); }
var source=new ParallelPlaybackEngine.SourceClock(SourceRate);
double physicalRate=SourceRate*1.00021;
for (int i=0;i<10000;i++)
{
    long first=(long)i*Packet;
    long qpc=Epoch+(long)Math.Round(first*Ticks/physicalRate);
    long frame=source.Place(Packet,initialDevice()+first,qpc,qpc+75_000+(i%4)*5000,valid:true,discontinuity:false);
    Check(frame==first,$"valid source contiguous frame {i}: {frame} != {first}");
}
Check(Math.Abs(source.FramesPerSecond-physicalRate)<.3,"capture drift converges");
Check(source.LeadHns==850_000,"initial schedule chooses safe common lead");
long endQpc=Epoch+(long)Math.Round(10000L*Packet*Ticks/physicalRate);
long resumeQpc=endQpc+Ticks;
long resume=source.Place(Packet,0,resumeQpc,resumeQpc+80_000,valid:true,discontinuity:true);
long expected=(long)Math.Round((resumeQpc-Epoch)*physicalRate/Ticks);
Check(Math.Abs(resume-expected)<50,"silence/controller reset keeps absolute gap");
long consecutive=source.Place(Packet,480,resumeQpc+100_000,resumeQpc+180_000,valid:true,discontinuity:false);
Check(consecutive==resume+Packet,"new device position rebase contiguous");
long badTimestamp=source.Place(Packet,960,0,resumeQpc+280_000,valid:false,discontinuity:false);
Check(badTimestamp==consecutive+Packet,"invalid timestamp fallback keeps packet order");

var a=new ParallelPlaybackEngine.ReadFollower(48000);
var b=new ParallelPlaybackEngine.ReadFollower(44100);
double aRate=48000*(1+170e-6), bRate=44100*(1-190e-6);
double peakIdealA=0,peakIdealB=0;
for(int i=0;i<3000;i++)
{
    double actualSourceA=i*480*physicalRate/aRate, actualSourceB=i*441*physicalRate/bRate;
    // Clock diagnostics are noisy by about 1.5ms; don't chase that noise.
    double jitterA=69*Math.Sin(i*.63),jitterB=44*Math.Cos(i*.29);
    var x=a.Next(actualSourceA+jitterA,480,physicalRate,aRate);
    var y=b.Next(actualSourceB+jitterB,441,physicalRate,bRate);
    if(i>150)
    {
        peakIdealA=Math.Max(peakIdealA,Math.Abs(x.first-actualSourceA));
        peakIdealB=Math.Max(peakIdealB,Math.Abs(y.first-actualSourceB));
    }
}
Check(peakIdealA<30&&peakIdealB<30,$"read followers should smooth jitter: {peakIdealA} {peakIdealB}");
var firstA = source.At(resumeQpc+Ticks-source.LeadHns);
var firstB = source.At(resumeQpc+Ticks+750_000-source.LeadHns-750_000);
Check(firstA==firstB,"manual delay aligns intended material timeline with a later frame");
Console.WriteLine($"PASS: same-QPC scheduling; source clock +210ppm; reset+timestamp fallback; 48k/44.1k read coasting max phase error {peakIdealA:0.00}/{peakIdealB:0.00} source samples (synthetic).");
static long initialDevice()=>48_000_000;
