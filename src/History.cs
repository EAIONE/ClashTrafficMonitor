using System.Globalization;

namespace ClashTraffic;
public enum HistoryPeriod { Day,Week,Month,Custom }
public record HistoryRange(DateTime Start,DateTime End)
{
    public static HistoryRange Resolve(HistoryPeriod period,DateTime anchor,DateTime customEnd)
    {
        var start=anchor.Date;var end=start;
        if(period==HistoryPeriod.Week){start=start.AddDays(-((int)start.DayOfWeek+6)%7);end=start.AddDays(6);}
        else if(period==HistoryPeriod.Month){start=new DateTime(start.Year,start.Month,1);end=start.AddMonths(1).AddDays(-1);}
        else if(period==HistoryPeriod.Custom)end=customEnd.Date;
        if(end<start)throw new ArgumentException("结束日期不能早于开始日期");
        return new(start,end);
    }
}
public record HistorySnapshot(List<TrafficRecord> Records,long Version);
public sealed class HistoryRepository(CsvStore store)
{
    private readonly Dictionary<DateTime,(long Length,DateTime Modified,List<TrafficRecord> Rows)> cache=new();
    private string signature="";private long version;private HistorySnapshot last=new(new(),0);
    public HistorySnapshot Read(DateTime start,DateTime end)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        var selected=new List<(DateTime Day,FileInfo Info)>();
        foreach(var path in Directory.GetFiles(store.DirectoryPath,"clash_*.csv"))
        {
            var name=Path.GetFileNameWithoutExtension(path);
            if(DateTime.TryParseExact(name[6..],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)&&day>=start.Date&&day<=end.Date)selected.Add((day,new FileInfo(path)));
        }
        selected.Sort((a,b)=>a.Day.CompareTo(b.Day));
        string next=$"{start.Date.Ticks}/{end.Date.Ticks}/"+string.Join(';',selected.Select(x=>$"{x.Day.Ticks}:{x.Info.Length}:{x.Info.LastWriteTimeUtc.Ticks}"));
        if(next==signature)return last;
        var rows=new List<TrafficRecord>();
        foreach(var item in selected)
        {
            if(!cache.TryGetValue(item.Day,out var entry)||entry.Length!=item.Info.Length||entry.Modified!=item.Info.LastWriteTimeUtc)
            {entry=(item.Info.Length,item.Info.LastWriteTimeUtc,store.ReadDay(item.Day));cache[item.Day]=entry;}
            rows.AddRange(entry.Rows);
        }
        // 不让反复浏览多年历史使缓存无限增长；当前范围的数据仍由 snapshot 持有。
        foreach(var key in cache.Keys.Where(x=>x<start.Date||x>end.Date).ToList())cache.Remove(key);
        signature=next;last=new(rows,++version);return last;
    }
}
public record HistoryDetail(DateTime Time,DateTime Last,string Process,string Host,long Down,long Up,double Quota,string Route);
public static class HistoryDetails
{
    public static List<HistoryDetail> Build(IEnumerable<TrafficRecord> records)=>records.GroupBy(r=>new{Minute=new DateTime(r.Time.Year,r.Time.Month,r.Time.Day,r.Time.Hour,r.Time.Minute,0),r.Process,r.Host,r.Route})
        .Select(g=>new HistoryDetail(g.Min(r=>r.Time),g.Max(r=>r.Time),string.IsNullOrWhiteSpace(g.Key.Process)?"（未知）":g.Key.Process,g.Key.Host,g.Sum(r=>r.Down),g.Sum(r=>r.Up),g.Sum(r=>r.EstimatedQuota),g.Key.Route))
        .OrderByDescending(x=>x.Time).ThenByDescending(x=>x.Down+x.Up).ToList();
}
public record ChartData(long[] Values,string[] Labels,string Caption);
public static class HistorySeries
{
    public static ChartData Build(IEnumerable<TrafficRecord> records,DateTime start,DateTime end)
    {
        var rows=records.ToList();
        if(start.Date==end.Date)return new(TrafficStats.Build(rows).Hourly,Enumerable.Range(0,24).Select(x=>$"{x:00}").ToArray(),"按小时统计");
        bool monthly=(end.Date-start.Date).TotalDays>93;
        DateTime Bucket(DateTime t)=>monthly?new DateTime(t.Year,t.Month,1):t.Date;
        var totals=rows.GroupBy(r=>Bucket(r.Time)).ToDictionary(g=>g.Key,g=>g.Sum(r=>r.Down+r.Up));
        var dates=new List<DateTime>();var last=Bucket(end);
        for(var cursor=Bucket(start);cursor<=last;cursor=monthly?cursor.AddMonths(1):cursor.AddDays(1)){dates.Add(cursor);if(cursor==last)break;}
        return new(dates.Select(x=>totals.GetValueOrDefault(x)).ToArray(),dates.Select(x=>x.ToString(monthly?"yy-MM":"MM-dd")).ToArray(),monthly?"按月统计":"按天统计");
    }
}
