using System.Text.Json;

namespace ClashTraffic;
public enum GuardMode { EstimatedQuota,ProxyTraffic,AllTraffic }
public sealed class TransferBudget(long limit,GuardMode mode)
{
    public long Limit{get;private set;}=limit;public GuardMode Mode{get;}=mode;
    public double Used{get;private set;}
    public bool Triggered=>Used>=Limit;
    public void Add(IEnumerable<TrafficRecord> rows)
    {
        foreach(var r in rows)
        {
            if(Mode==GuardMode.AllTraffic)Used+=r.Down+r.Up;
            else if(r.Route=="代理")Used+=Mode==GuardMode.EstimatedQuota?r.EstimatedQuota:r.Down+r.Up;
            // 无法归因的流量也纳入保护；倍率未知时只能暂按 1x 估算。
            else if(r.Route=="未知")Used+=r.Down+r.Up;
        }
    }
    public void Reset()=>Used=0;
    public void Restore(double value)=>Used=double.IsFinite(value)?Math.Max(0,value):0;
    public void SetLimit(long value){if(value<=0)throw new ArgumentOutOfRangeException(nameof(value));Limit=value;}
}
public record ClashSettings(string Mode,string GlobalChoice,bool HasReject);
public interface IClashControl
{
    Task<ClashSettings> ReadSettingsAsync(CancellationToken token);
    Task SetGlobalAsync(string name,CancellationToken token);
    Task SetModeAsync(string mode,CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed class PauseController
{
    private record PauseState(ClashSettings Original,string Reason);
    private readonly IClashControl control;private readonly string path;
    public ClashSettings? Saved{get;private set;}public bool Paused=>Saved!=null;
    public string PersistenceError{get;private set;}="";
    public string Reason{get;private set;}="threshold";
    public PauseController(IClashControl control,string statePath)
    {
        this.control=control;path=statePath;
        if(File.Exists(path))
        {
            var json=File.ReadAllText(path);using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.TryGetProperty("Original",out _))
            {
                var state=JsonSerializer.Deserialize<PauseState>(json)??throw new IOException("保护状态文件无效");Saved=state.Original;Reason=state.Reason;
            }
            else Saved=JsonSerializer.Deserialize<ClashSettings>(json)??throw new IOException("保护状态文件无效");
        }
    }
    public async Task PauseAsync(CancellationToken token,string reason="threshold")
    {
        if(Saved==null)
        {
            var settings=await control.ReadSettingsAsync(token);
            if(!settings.HasReject)throw new IOException("当前 GLOBAL 组不支持 REJECT，不能确认阻断");
            Saved=settings;Reason=reason;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new PauseState(settings,Reason)));File.Move(path+".tmp",path,true);PersistenceError="";
            }
            catch(IOException){PersistenceError="暂停状态未写入磁盘，请勿退出监控；重启后需手动检查 Clash 模式";}
            catch(UnauthorizedAccessException){PersistenceError="暂停状态目录不可写，请勿退出监控";}
        }
        await EnforceAsync(token);
    }
    public async Task EnforceAsync(CancellationToken token)
    {
        if(!Paused)return;
        await control.SetGlobalAsync("REJECT",token);await control.SetModeAsync("global",token);
        await control.DisconnectAsync(token);
        var actual=await control.ReadSettingsAsync(token);
        if(actual.Mode!="global"||actual.GlobalChoice!="REJECT")throw new IOException("Clash 阻断状态未确认");
    }
    public async Task ResumeAsync(CancellationToken token)
    {
        if(Saved==null)return;
        await control.SetGlobalAsync(Saved.GlobalChoice,token);await control.SetModeAsync(Saved.Mode,token);
        var actual=await control.ReadSettingsAsync(token);
        if(actual.Mode!=Saved.Mode||actual.GlobalChoice!=Saved.GlobalChoice)throw new IOException("Clash 恢复状态未确认");
        File.Delete(path);Saved=null;
    }
}
