using AudioRelay;
void Require(bool valid,string reason) { if (!valid) throw new Exception(reason); Console.WriteLine("PASS  " + reason); }
bool Equal(float a,float b) => Math.Abs(a-b)<1e-6f;
bool Matches(float[] a,params float[] b) => a.Length==b.Length && a.Zip(b).All(p=>Equal(p.First,p.Second));
void Reject(Action x) { try { x(); } catch(ArgumentException) { return; } throw new Exception("Expected ArgumentException"); }
var t=new SynchronizedTimeline(8000,1);
Require(t.CapacityFrames==8&&t.OldestFrame==0&&t.EndFrame==0,"correct initial empty / bounded timeline");
t.Write(0,new[]{.2f,.6f,.4f,-.4f,.8f,.2f,-1f,1f});
var stereo=new float[8];
Require(t.Read(.5,.5,stereo,2)==4&&Matches(stereo,.3f,.1f,.4f,-.4f,.6f,-.1f,.8f,.2f),"true fractional interpolation and independent stereo");
var mono=new float[4];
Require(t.Read(.5,.5,mono,1,.5f)==4&&Matches(mono,.1f,0,.125f,.25f),"mono downmix and nominal gain after interpolation");
t.Write(11,new[]{.2f,.4f,.8f,.5f});
Require(t.OldestFrame==5&&t.EndFrame==13&&t.Read(7,1,stereo,2)==0&&Matches(stereo,0,0,0,0,0,0,0,0),"wrapped empty slots cannot leak retained old samples");
Require(t.Read(10.5,.5,stereo,2)==3&&Matches(stereo,0,0,.2f,.4f,.5f,.45f,.8f,.5f),"fractional reads crossing a hole are silent");
t.Write(-30,new[]{1f,1f,1f,1f});
Require(t.Read(8,1,stereo,2)==1&&Matches(stereo,0,0,0,0,0,0,.2f,.4f),"late unreachable write ignored");
t.Reset(-5);
t.Write(-4,new[]{float.NaN,float.PositiveInfinity,1.4f,-1.4f});
Require(t.Read(-5,1,stereo,2)==2&&Matches(stereo,0,0,0,0,1,-1,0,0)&&t.EndFrame==-2,"signed reset, memory scrub and non-finite sanitization");
t.Reset();
Require(t.Read(-1,.5,stereo,2)==0&&Matches(stereo,0,0,0,0,0,0,0,0),"reset invalidates both previous and empty generations");
var qpc=long.MaxValue-10_000_000L;
double after=SynchronizedTimeline.PositionAtQpc(qpc+10_000_000,qpc,23.25,44100);
double before=SynchronizedTimeline.PositionAtQpc(qpc-5_000_000,qpc,23.25,44100);
Require(after==44123.25&&before==-22026.75,"QPC mapping preserves small signed differences near long boundary");
Require(SynchronizedTimeline.PositionAtQpc(1_050,1_000,20,8000,1000)==420,"caller-generated clock scale and anchor");
Reject(()=>new SynchronizedTimeline(7999));
Reject(()=>t.Write(0,new[] { 1f }));
Reject(()=>t.Write(SynchronizedTimeline.MaximumAbsoluteFrame,new[] { 1f,1f }));
Reject(()=>t.Read(double.NaN,1,stereo,2));
Reject(()=>t.Read(0,0,stereo,2));
Reject(()=>t.Read(0,1,stereo,2,float.NaN));
Require(true,"corrupt or unbounded ingress fails closed");

var r=new Random(924);
var model=new Dictionary<long,(float l,float r)>();
var actual=new SynchronizedTimeline(8000,2); // 16-frame ring
long end=0, origin=-7;
actual.Reset(origin);end=origin;
for(int tick=0;tick<3000;tick++) {
   long from=end+r.Next(-30,24);
   int n=r.Next(1,40);
   var block=Enumerable.Range(0,n*2).Select(_=>(float)(r.NextDouble()*2-1)).ToArray();
   actual.Write(from,block);
   end=Math.Max(end,from+n);
   long oldest=Math.Max(origin,end-actual.CapacityFrames);
   for(int i=0;i<n;i++)if(from+i>=oldest) model[from+i]=(block[2*i],block[2*i+1]);
   foreach(var key in model.Keys.Where(x=>x<oldest).ToArray())model.Remove(key);
   if(actual.EndFrame!=end||actual.OldestFrame!=oldest) throw new Exception("history mismatch");
   double start=oldest+r.Next(-9,10)+.25;
   double rate=r.Next(1,8)*.25;
   var result=new float[40];
   int count=actual.Read(start,rate,result,2);
   int expectedCount=0;
   for(int i=0;i<20;i++) {
      double pos=start+i*rate;
      var f=(long)Math.Floor(pos);double a=pos-f;
      float l=0, rr=0;
      if(model.TryGetValue(f,out var fr)) {
          if(a==0) { l=fr.l;rr=fr.r;expectedCount++; }
          else if(model.TryGetValue(f+1,out var next)) {
              l=(float)(fr.l+(next.l-fr.l)*a);rr=(float)(fr.r+(next.r-fr.r)*a);expectedCount++;
          }
      }
      if(!Equal(result[2*i],l)||!Equal(result[2*i+1],rr)) throw new Exception("random model mismatch");
   }
   if(expectedCount!=count) throw new Exception("covered frame count mismatch");
}
Require(true,"3000 deterministic out-of-order writes and variable-rate reads agree with unbounded reference model");
