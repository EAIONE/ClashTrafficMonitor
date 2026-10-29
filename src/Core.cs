using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTraffic;

public record Connection(string Id,string Process,string Host,string Chain,long Down,long Up,string Rule,string RulePayload);
public record TrafficRecord(DateTime Time,string Host,string Process,string Chain,double Multiplier,long Down,long Up)
{
    public string Route=>Routing.Classify(Chain);
    public double EstimatedQuota=>Route=="代理"?(Down+Up)*Multiplier:0;
}
public static class Routing
{
    public static string Classify(string chain)
    {
        var parts=chain.Split('>').Select(x=>x.Trim()).ToArray();
        if(parts.Any(x=>x.Equals("DIRECT",StringComparison.OrdinalIgnoreCase)))return "直连";
        if(parts.Any(x=>x.Equals("REJECT",StringComparison.OrdinalIgnoreCase)||x.Equals("REJECT-DROP",StringComparison.OrdinalIgnoreCase)))return "拒绝";
        return string.IsNullOrWhiteSpace(chain)?"未知":"代理";
    }
    public static double Multiplier(string chain)
    {
        var values=Regex.Matches(chain,@"(\d+(?:\.\d+)?)\s*[xX倍]").Select(x=>double.Parse(x.Groups[1].Value,CultureInfo.InvariantCulture));
        return values.DefaultIfEmpty(1).Max();
    }
    public static string Format(double bytes)
    {
        string[] units={"B","KiB","MiB","GiB","TiB"}; int i=0;
        while(bytes>=1024 && i<units.Length-1) {bytes/=1024;i++;}
        return $"{bytes:0.##} {units[i]}";
    }
}
public sealed class SampleTracker
{
    private Dictionary<string,(long Down,long Up)> seen=new();
    public List<TrafficRecord> Sample(IEnumerable<Connection> connections,DateTime now)
    {
        var result=new List<TrafficRecord>(); var alive=new Dictionary<string,(long,long)>();
        foreach(var c in connections)
        {
            var dn=Math.Max(0,c.Down); var up=Math.Max(0,c.Up);
            var old=seen.GetValueOrDefault(c.Id);
            var dd=dn>=old.Down?dn-old.Down:dn;
            var du=up>=old.Up?up-old.Up:up;
            alive[c.Id]=(dn,up);
            if(dd>0||du>0) result.Add(new(now,c.Host,c.Process,c.Chain,Routing.Multiplier(c.Chain),dd,du));
        }
        seen=alive;
        return result;
    }
}
public sealed class TrafficAccounting
{
    private SampleTracker tracker=new();
    private (long Down,long Up)? previous;
    private long downBalance,upBalance;
    public List<TrafficRecord> Sample(IEnumerable<Connection> connections,long totalDown,long totalUp,DateTime now)
    {
        // 全局累计值用于补齐轮询间已关闭的短连接；不伪造其进程和路由。
        if(previous is null) { tracker.Sample(connections,now);previous=(totalDown,totalUp);return new(); }
        bool reset=totalDown<previous.Value.Down||totalUp<previous.Value.Up;
        if(reset){downBalance=0;upBalance=0;}
        var rows=tracker.Sample(connections,now);
        long dd=reset?totalDown:totalDown-previous.Value.Down, du=reset?totalUp:totalUp-previous.Value.Up;
        previous=(totalDown,totalUp);
        downBalance+=dd-rows.Sum(r=>r.Down);upBalance+=du-rows.Sum(r=>r.Up);
        long missingDown=Math.Max(0,downBalance),missingUp=Math.Max(0,upBalance);
        downBalance-=missingDown;upBalance-=missingUp;
        if(missingDown>0||missingUp>0)rows.Add(new(now,"（内核总量补齐）","（未归因·短连接）","",1,missingDown,missingUp));
        return rows;
    }
}
public sealed class CsvStore(string directory)
{
    public string DirectoryPath=>directory;
    private record PendingAppend(long Offset,byte[] Bytes,string Hash);
    private static void Commit(string path,PendingAppend pending)
    {
        using(var file=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read))
        {
            if(file.Length<pending.Offset)throw new IOException("CSV 比待提交事务的基线更短");
            file.SetLength(pending.Offset);file.Position=pending.Offset;file.Write(pending.Bytes);file.Flush(true);
        }
        File.Delete(path+".pending");
    }
    public void RecoverPending()
    {
        Directory.CreateDirectory(directory);
        foreach(var pendingPath in Directory.GetFiles(directory,"*.csv.pending"))
        {
            var pending=JsonSerializer.Deserialize<PendingAppend>(File.ReadAllText(pendingPath))??throw new IOException("CSV 事务无效");
            Commit(pendingPath[..^8],pending);
        }
    }
    public void Append(IEnumerable<TrafficRecord> records)
    {
        Directory.CreateDirectory(directory);
        foreach(var group in records.GroupBy(r=>r.Time.Date))
        {
            var path=Path.Combine(directory,$"clash_{group.Key:yyyy-MM-dd}.csv");
            var content=new StringBuilder();
            foreach(var r in group)
                content.AppendLine(string.Join(",",new[]{r.Time.ToString("yyyy-MM-dd HH:mm:ss"),r.Host,r.Process,r.Chain,r.Multiplier.ToString(CultureInfo.InvariantCulture),r.Down.ToString(),r.Up.ToString()}.Select(Quote)));
            string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
            if(File.Exists(path+".pending"))
            {
                var old=JsonSerializer.Deserialize<PendingAppend>(File.ReadAllText(path+".pending"))??throw new IOException("CSV 事务无效");
                Commit(path,old);if(old.Hash==hash)continue;
            }
            long offset=File.Exists(path)?new FileInfo(path).Length:0;
            if(offset==0)content.Insert(0,"\ufeffts,host,process,chain,multiplier,down_bytes,up_bytes\r\n");
            var pending=new PendingAppend(offset,Encoding.UTF8.GetBytes(content.ToString()),hash);
            File.WriteAllText(path+".pending.tmp",JsonSerializer.Serialize(pending));File.Move(path+".pending.tmp",path+".pending",true);
            Commit(path,pending);
        }
    }
    private static string Quote(string value)=>value.Contains(',')||value.Contains('"')||value.Contains('\n')?"\""+value.Replace("\"","\"\"")+"\"":value;
    public static void Export(string path,IEnumerable<TrafficRecord> records,string? managedDirectory=null)
    {
        var target=Path.GetFullPath(path);
        if(managedDirectory!=null&&string.Equals(Path.GetDirectoryName(target),Path.GetFullPath(managedDirectory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("clash_",StringComparison.OrdinalIgnoreCase)&&Path.GetExtension(target).Equals(".csv",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("不能覆盖监控原始历史，请选择其他文件名或目录。");
        using var file=new StreamWriter(path,false,new UTF8Encoding(true));file.WriteLine("ts,host,process,chain,multiplier,down_bytes,up_bytes");
        foreach(var r in records.OrderBy(x=>x.Time))file.WriteLine(string.Join(',',new[]{r.Time.ToString("yyyy-MM-dd HH:mm:ss"),r.Host,r.Process,r.Chain,r.Multiplier.ToString(CultureInfo.InvariantCulture),r.Down.ToString(),r.Up.ToString()}.Select(Quote)));
    }
    public List<TrafficRecord> ReadDay(DateTime date)
    {
        var path=Path.Combine(directory,$"clash_{date:yyyy-MM-dd}.csv"); var records=new List<TrafficRecord>();
        if(!File.Exists(path)) return records;
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var reader=new StreamReader(stream,Encoding.UTF8,true);
        _=reader.ReadLine(); string? line;
        while((line=reader.ReadLine())!=null)
        {
            var cells=ParseLine(line);
            if(cells.Count<7||!DateTime.TryParseExact(cells[0],"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)
               ||!long.TryParse(cells[5],out long down)||!long.TryParse(cells[6],out long up)) continue;
            if(!double.TryParse(cells[4],NumberStyles.Float,CultureInfo.InvariantCulture,out double mult)||!double.IsFinite(mult)||mult<0) mult=1;
            records.Add(new(time,cells[1],cells[2],cells[3],mult,Math.Max(0,down),Math.Max(0,up)));
        }
        return records;
    }
    private static List<string> ParseLine(string line)
    {
        var cells=new List<string>();var cell=new StringBuilder();bool quoted=false;
        for(int i=0;i<line.Length;i++)
        {
            char c=line[i];
            if(c=='"') {if(quoted&&i+1<line.Length&&line[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
            else if(c==','&&!quoted) {cells.Add(cell.ToString());cell.Clear();}
            else cell.Append(c);
        }
        if(quoted) return new();
        cells.Add(cell.ToString());return cells;
    }
}
public record RankedUsage(string Name,long Down,long Up,double Quota,string Route,DateTime First=default,DateTime Last=default)
{
    public long Total=>Down+Up;
}
public sealed class TrafficStats
{
    public long Total,Down,Up,Direct,Proxy,Unknown; public double Quota;
    public List<RankedUsage> Apps=new(),Hosts=new(); public long[] Hourly=new long[24];
    public static TrafficStats Build(IEnumerable<TrafficRecord> source)
    {
        var rows=source.ToList(); var s=new TrafficStats();
        foreach(var r in rows)
        {
            var bytes=r.Down+r.Up;s.Down+=r.Down;s.Up+=r.Up;s.Total+=bytes;s.Quota+=r.EstimatedQuota;
            if(r.Route=="直连")s.Direct+=bytes;else if(r.Route=="代理")s.Proxy+=bytes;else s.Unknown+=bytes;
            s.Hourly[r.Time.Hour]+=bytes;
        }
        List<RankedUsage> Rank(Func<TrafficRecord,string> key)=>rows.GroupBy(key).Select(g=>new RankedUsage(string.IsNullOrWhiteSpace(g.Key)?"（未知）":g.Key,g.Sum(r=>r.Down),g.Sum(r=>r.Up),g.Sum(r=>r.EstimatedQuota),string.Join(" / ",g.Select(r=>r.Route).Distinct()),g.Min(r=>r.Time),g.Max(r=>r.Time))).OrderByDescending(r=>r.Total).ToList();
        s.Apps=Rank(r=>r.Process);s.Hosts=Rank(r=>r.Host);return s;
    }
}
