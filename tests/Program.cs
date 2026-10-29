using ClashTraffic;
int passed=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); passed++; Console.WriteLine("PASS "+name); }
Connection C(string id,long down,long up,string chain="DIRECT > 全球直连") => new(id,"iCloudDrive.exe","edge.icloud-content.com.cn",chain,down,up,"DomainSuffix","icloud-content.com.cn");
Check(Routing.Classify("DIRECT > 全球直连")=="直连","DIRECT chain stays direct");
Check(Routing.Classify("香港 1.7x > 自动选择")=="代理","node chain is proxy");
Check(Routing.Classify("")=="未知","empty chain not invented proxy");
var tracker=new SampleTracker(); var time=new DateTime(2026,10,26,23,59,59);
var first=tracker.Sample(new[]{C("a",100,20)},time);
Check(first.Single().Down==100,"new connection counts observed bytes");
Check(first.Single().EstimatedQuota==0,"direct costs zero proxy quota");
var next=tracker.Sample(new[]{C("a",160,30)},time.AddSeconds(2));
Check(next.Single().Down==60 && next.Single().Up==10,"midnight preserves baseline");
Check(tracker.Sample(new[]{C("a",5,2)},time.AddSeconds(4)).Single().Down==5,"counter reset never negative");
tracker.Sample(Array.Empty<Connection>(),time.AddSeconds(5));
Check(tracker.Sample(new[]{C("a",9,1)},time.AddSeconds(6)).Single().Down==9,"reused id after close counts new bytes");
var proxy=new TrafficRecord(time,"a,b\"c","程序","香港 1.7x > 代理",1.7,100,20);
Check(Math.Abs(proxy.EstimatedQuota-204)<0.001,"proxy quota uses multiplier");
var dir=Path.Combine(Path.GetTempPath(),"clash-tests-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
try {
 var store=new CsvStore(dir); store.Append(new[]{proxy});
 File.AppendAllText(Path.Combine(dir,"clash_2026-10-26.csv"),"broken,row\n");
 var read=store.ReadDay(new DateTime(2026,10,26));
 Check(read.Count==1 && read[0].Host=="a,b\"c","CSV roundtrip with comma and quote and malformed tail");
 var stats=TrafficStats.Build(read.Concat(first));
 Check(stats.Total==240 && stats.Direct==120 && stats.Proxy==120,"all traffic retained with separate routes");
 Check(stats.Hourly[23]==240,"hourly aggregation");
 Check(stats.Apps.Count==2,"app grouping");
} finally {Directory.Delete(dir,true);}
Console.WriteLine($"{passed} tests passed");
var all=new TrafficAccounting();
Check(all.Sample(new[]{C("a",100,20)},1000,200,time).Count==0,"startup establishes baseline, no historic double count");
var reconciled=all.Sample(new[]{C("a",160,30)},1200,230,time.AddSeconds(2));
Check(reconciled.Sum(r=>r.Down)==200 && reconciled.Sum(r=>r.Up)==30,"short connections reconciled using kernel total");
Check(reconciled.Single(r=>r.Process=="（未归因·短连接）").Down==140,"unknown gap kept rather than dropped");
Check(reconciled.Single(r=>r.Process=="（未归因·短连接）").Route=="未知","unattributed traffic not invented proxy");
var afterReset=all.Sample(Array.Empty<Connection>(),20,3,time.AddSeconds(4));
Check(afterReset.Sum(r=>r.Down)==20 && afterReset.Sum(r=>r.Up)==3,"kernel reset records new counts");
Console.WriteLine($"{passed} tests passed total");
var budget=new TransferBudget(1000,GuardMode.EstimatedQuota);
budget.Add(new[]{new TrafficRecord(time,"host","app","node 4x",4,249,0)});
Check(!budget.Triggered && budget.Used==996,"4x budget not triggered prematurely");
budget.Add(new[]{new TrafficRecord(time,"host","app","node 4x",4,1,0)});
Check(budget.Triggered,"4x budget trips at weighted threshold");
budget.Reset();budget.Add(new[]{new TrafficRecord(time,"host","app","DIRECT",1,2000,0)});
Check(!budget.Triggered && budget.Used==0,"quota guard excludes direct but direct remains in store");
var allBudget=new TransferBudget(1000,GuardMode.AllTraffic);allBudget.Add(new[]{new TrafficRecord(time,"host","app","DIRECT",1,1000,0)});
Check(allBudget.Triggered,"all-traffic guard includes direct");
var guardPath=Path.Combine(Path.GetTempPath(),"clash-guard-"+Guid.NewGuid()+".json");
try {
 var fake=new FakeClash();var breaker=new PauseController(fake,guardPath);
 await breaker.PauseAsync(CancellationToken.None);
 Check(fake.Mode=="global"&&fake.Choice=="REJECT"&&fake.Disconnects==1,"pause rejects new connections and closes existing ones");
 var recovered=new PauseController(fake,guardPath);Check(recovered.Paused,"pause persists after restart");
 fake.FailNext=true;
 try{await recovered.ResumeAsync(CancellationToken.None);}catch(IOException){}
 Check(recovered.Paused,"failed restore does not falsely mark resumed");
 await recovered.ResumeAsync(CancellationToken.None);
 Check(!recovered.Paused&&fake.Mode=="rule"&&fake.Choice=="old node","continue restores exact pre-pause selection and mode");
} finally {if(File.Exists(guardPath))File.Delete(guardPath);}
Console.WriteLine($"{passed} tests passed including guard");
var stable=new TrafficAccounting();stable.Sample(new[]{C("stable",1000,0)},5000,0,time);
var unchanged=stable.Sample(new[]{C("stable",1000,0)},0,0,time.AddSeconds(1));
Check(unchanged.Sum(r=>r.Down)==0,"resetting kernel totals does not recount surviving connections");
var racing=new TrafficAccounting();racing.Sample(new[]{C("r",100,0)},100,0,time);
var lead=racing.Sample(new[]{C("r",120,0)},110,0,time.AddSeconds(1));
var catchup=racing.Sample(new[]{C("r",120,0)},120,0,time.AddSeconds(2));
Check(lead.Concat(catchup).Sum(r=>r.Down)==20,"snapshot skew does not duplicate later global catchup");
Console.WriteLine($"{passed} tests passed overall");
var journalDir=Path.Combine(Path.GetTempPath(),"clash-journal-"+Guid.NewGuid());Directory.CreateDirectory(journalDir);
try{
 var cs=new CsvStore(journalDir);var r1=new TrafficRecord(time,"first","app","DIRECT",1,10,0);var r2=r1 with{Host="retry",Down=20};
 cs.Append(new[]{r1});var file=Directory.GetFiles(journalDir,"*.csv").Single();
 using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
  try{cs.Append(new[]{r2});}catch(IOException){}
  Check(Directory.GetFiles(journalDir,"*.pending").Length==1,"failed CSV append leaves recoverable transaction");
 }
 cs.Append(new[]{r2});Check(cs.ReadDay(time).Count==2&&cs.ReadDay(time).Sum(r=>r.Down)==30,"retry commits exactly once");
}finally{Directory.Delete(journalDir,true);}
Console.WriteLine($"{passed} tests passed with journal");
var unwritable=Path.Combine(Path.GetTempPath(),"clash-state-directory-"+Guid.NewGuid());Directory.CreateDirectory(unwritable);
try{
 var fake2=new FakeClash();var safety=new PauseController(fake2,unwritable);
 await safety.PauseAsync(CancellationToken.None);
 Check(fake2.Mode=="global"&&fake2.Choice=="REJECT","disk failure does not disable network blocking");
 Check(safety.PersistenceError.Length>0,"pause persistence failure is visible");
}finally{Directory.Delete(unwritable,true);if(File.Exists(unwritable+".tmp"))File.Delete(unwritable+".tmp");}
Console.WriteLine($"{passed} tests passed final");
var adjustable=new TransferBudget(1000,GuardMode.AllTraffic);adjustable.Restore(800);adjustable.SetLimit(500);
Check(adjustable.Triggered&&adjustable.Used==800,"lowering threshold preserves usage and triggers protection");
adjustable.SetLimit(2000);Check(!adjustable.Triggered&&adjustable.Used==800,"raising threshold preserves usage");
try{adjustable.SetLimit(0);throw new Exception("zero threshold accepted");}catch(ArgumentOutOfRangeException){passed++;Console.WriteLine("PASS invalid threshold rejected");}
Console.WriteLine($"{passed} tests passed including configurable threshold");
var manualFile=Path.Combine(Path.GetTempPath(),"clash-manual-"+Guid.NewGuid()+".json");
try{
 var manualFake=new FakeClash();var manual=new PauseController(manualFake,manualFile);
 await manual.PauseAsync(CancellationToken.None,"manual");
 Check(manual.Paused&&manual.Reason=="manual"&&manualFake.Disconnects==1,"manual pause works below quota threshold");
 var restarted=new PauseController(manualFake,manualFile);
 Check(restarted.Reason=="manual","manual pause reason survives restart");
 await restarted.ResumeAsync(CancellationToken.None);Check(manualFake.Mode=="rule"&&!restarted.Paused,"manual continue restores prior routing");
}finally{if(File.Exists(manualFile))File.Delete(manualFile);}
Console.WriteLine($"{passed} tests passed with manual pause");
var week=HistoryRange.Resolve(HistoryPeriod.Week,new DateTime(2026,11,1),time);
Check(week.Start==new DateTime(2026,10,26)&&week.End==new DateTime(2026,11,1),"week uses Monday and crosses month boundary");
var month=HistoryRange.Resolve(HistoryPeriod.Month,new DateTime(2028,2,8),time);
Check(month.End==new DateTime(2028,2,29),"month handles leap February");
try{HistoryRange.Resolve(HistoryPeriod.Custom,time,time.AddDays(-1));throw new Exception("reversed range accepted");}catch(ArgumentException){passed++;}
var historyDir=Path.Combine(Path.GetTempPath(),"clash-history-"+Guid.NewGuid());Directory.CreateDirectory(historyDir);
try{
 var cs=new CsvStore(historyDir);cs.Append(new[]{new TrafficRecord(new DateTime(2026,10,26,20,1,0),"a","browser.exe","node 2x",2,100,10),new TrafficRecord(new DateTime(2026,10,27,9,2,0),"b","browser.exe","node 2x",2,200,20),new TrafficRecord(new DateTime(2026,10,28,9,2,0),"c","browser.exe","DIRECT",1,900,0)});
 var repository=new HistoryRepository(cs);var result=repository.Read(new DateTime(2026,10,26),new DateTime(2026,10,27));
 Check(result.Records.Count==2&&TrafficStats.Build(result.Records).Total==330,"history reads inclusive start and end dates only");
 var same=repository.Read(new DateTime(2026,10,26),new DateTime(2026,10,27));Check(same.Version==result.Version,"unchanged history reuses cached revision");
 var app=TrafficStats.Build(result.Records).Apps.Single();Check(app.First==new DateTime(2026,10,26,20,1,0)&&app.Last==new DateTime(2026,10,27,9,2,0),"app ranking includes observed first and last times");
 var details=HistoryDetails.Build(result.Records);Check(details.Count==2&&details.Sum(x=>x.Down)==300,"details retain time and app linkage across days");
 var series=HistorySeries.Build(result.Records,new DateTime(2026,10,26),new DateTime(2026,10,27));Check(series.Values.SequenceEqual(new long[]{110,220}),"multi-day chart uses daily buckets");
 cs.Append(new[]{new TrafficRecord(new DateTime(2026,10,27,10,0,0),"d","other.exe","DIRECT",1,1,0)});
 Check(repository.Read(new DateTime(2026,10,26),new DateTime(2026,10,27)).Records.Count==3,"history cache notices new appended bytes");
 var quantities=new SortableBindingList<RankedTest>(new List<RankedTest>{new(new ByteQuantity(2L*1024*1024*1024)),new(new ByteQuantity(900L*1024*1024))});
 ((System.ComponentModel.IBindingList)quantities).ApplySort(System.ComponentModel.TypeDescriptor.GetProperties(typeof(RankedTest))["Bytes"]!,System.ComponentModel.ListSortDirection.Ascending);
 Check(quantities[0].Bytes.Value==900L*1024*1024,"size sorting compares raw bytes across units");
}finally{Directory.Delete(historyDir,true);}
Console.WriteLine($"{passed} tests passed with history and sorting");
var page=new SortableBindingList<RankedTest>(new List<RankedTest>{new(new ByteQuantity(1000)),new(new ByteQuantity(500)),new(new ByteQuantity(2000))},2);
((System.ComponentModel.IBindingList)page).ApplySort(System.ComponentModel.TypeDescriptor.GetProperties(typeof(RankedTest))["Bytes"]!,System.ComponentModel.ListSortDirection.Descending);
Check(page.Count==2&&page[0].Bytes.Value==2000,"sorting includes records beyond initial visible limit");
var protectedDir=Path.Combine(Path.GetTempPath(),"clash-export-"+Guid.NewGuid());Directory.CreateDirectory(protectedDir);
try{
 var protectedPath=Path.Combine(protectedDir,"clash_2026-10-28.csv");File.WriteAllText(protectedPath,"original history");
 try{CsvStore.Export(protectedPath,Array.Empty<TrafficRecord>(),protectedDir);throw new Exception("managed history overwritten");}catch(InvalidOperationException){}
 Check(File.ReadAllText(protectedPath)=="original history","export cannot truncate managed history files");
}finally{Directory.Delete(protectedDir,true);}
Console.WriteLine($"{passed} tests passed with export protection");

record RankedTest(ByteQuantity Bytes);

class FakeClash:IClashControl {
 public string Mode="rule",Choice="old node";public int Disconnects;public bool FailNext;
 public Task<ClashSettings> ReadSettingsAsync(CancellationToken token)=>Task.FromResult(new ClashSettings(Mode,Choice,true));
 public Task SetGlobalAsync(string name,CancellationToken token){if(FailNext){FailNext=false;throw new IOException("offline");}Choice=name;return Task.CompletedTask;}
 public Task SetModeAsync(string mode,CancellationToken token){Mode=mode;return Task.CompletedTask;}
 public Task DisconnectAsync(CancellationToken token){Disconnects++;return Task.CompletedTask;}
}
