using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace ClashTraffic;

public record ConnectionSnapshot(List<Connection> Connections,long Down,long Up);
public sealed class ClashClient : IClashControl
{
    private string ConfigDir=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"io.github.clash-verge-rev.clash-verge-rev");
    public Task<JsonDocument> GetAsync(string path,CancellationToken token=default)=>RequestAsync("GET",path,null,token);
    public async Task<JsonDocument> RequestAsync(string method,string path,object? payload=null,CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(7));
        await using var pipe=new NamedPipeClientStream(".","verge-mihomo",PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        // 密钥只从本机配置读取，不写进报告或错误日志。
        string secret="";
        foreach(var line in File.ReadLines(Path.Combine(ConfigDir,"clash-verge.yaml")))
        {
            if(line.StartsWith("secret:")) {secret=line[7..].Trim().Trim('\'', '"');break;}
        }
        var content=payload is null?Array.Empty<byte>():JsonSerializer.SerializeToUtf8Bytes(payload);
        var bytes=Encoding.UTF8.GetBytes($"{method} {path} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {secret}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await pipe.WriteAsync(bytes,timeout.Token);await pipe.FlushAsync(timeout.Token);
        if(content.Length>0){await pipe.WriteAsync(content,timeout.Token);await pipe.FlushAsync(timeout.Token);}
        using var memory=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await pipe.ReadAsync(buffer,timeout.Token))>0)
        {
            await memory.WriteAsync(buffer.AsMemory(0,count),timeout.Token);
            if(memory.Length>64*1024*1024)throw new IOException("内核响应过大");
        }
        var raw=memory.ToArray(); int header=-1;
        for(int i=0;i+3<raw.Length;i++)if(raw[i]==13&&raw[i+1]==10&&raw[i+2]==13&&raw[i+3]==10){header=i;break;}
        if(header<0)throw new IOException("内核返回不完整 HTTP 响应");
        string head=Encoding.ASCII.GetString(raw,0,header);
        if(!int.TryParse(head.Split('\n')[0].Split(' ').ElementAtOrDefault(1),out var code)||code<200||code>=300)throw new IOException("内核 API 请求未成功，请确认配置和登录状态");
        var body=raw.AsSpan(header+4).ToArray();
        if(head.Contains("Transfer-Encoding: chunked",StringComparison.OrdinalIgnoreCase))body=Unchunk(body);
        return JsonDocument.Parse(body.Length==0?Encoding.UTF8.GetBytes("{}"):body);
    }
    public async Task<ClashSettings> ReadSettingsAsync(CancellationToken token)
    {
        using var configs=await GetAsync("/configs",token);using var global=await GetAsync("/proxies/GLOBAL",token);
        string mode=configs.RootElement.GetProperty("mode").GetString()??"rule";
        string choice=global.RootElement.GetProperty("now").GetString()??"DIRECT";
        bool reject=global.RootElement.TryGetProperty("all",out var all)&&all.EnumerateArray().Any(x=>x.GetString()=="REJECT");
        return new(mode,choice,reject);
    }
    public async Task SetGlobalAsync(string name,CancellationToken token){using var r=await RequestAsync("PUT","/proxies/GLOBAL",new{name},token);}
    public async Task SetModeAsync(string mode,CancellationToken token){using var r=await RequestAsync("PATCH","/configs",new{mode},token);}
    public async Task DisconnectAsync(CancellationToken token){using var r=await RequestAsync("DELETE","/connections",null,token);}
    public static byte[] Unchunk(byte[] body)
    {
        using var output=new MemoryStream();int offset=0;
        while(offset<body.Length)
        {
            int end=Array.IndexOf(body,(byte)13,offset);if(end<0||end+1>=body.Length||body[end+1]!=10)throw new IOException("不完整的 chunk");
            var lengthText=Encoding.ASCII.GetString(body,offset,end-offset).Split(';')[0];
            int size=int.Parse(lengthText,NumberStyles.HexNumber,CultureInfo.InvariantCulture);offset=end+2;
            if(size==0)return output.ToArray();
            if(size<0||offset+size+2>body.Length)throw new IOException("不完整的 chunk 内容");
            output.Write(body,offset,size);offset+=size+2;
        }
        throw new IOException("缺失 chunk 结束标记");
    }
    public async Task<ConnectionSnapshot> ReadConnectionsAsync(CancellationToken token=default)
    {
        using var doc=await GetAsync("/connections",token);var root=doc.RootElement;var list=new List<Connection>();
        var owners=PortOwners.Get();
        if(root.TryGetProperty("connections",out var array)&&array.ValueKind==JsonValueKind.Array)
        foreach(var c in array.EnumerateArray())
        {
            var meta=c.GetProperty("metadata");string proc=Text(meta,"processPath");if(proc.Length==0)proc=Text(meta,"process");
            proc=Path.GetFileName(proc);
            if(proc.Length==0&&int.TryParse(Text(meta,"sourcePort"),out int port)&&owners.TryGetValue(port,out int pid))
            {try{proc=Process.GetProcessById(pid).ProcessName+".exe";}catch{}}
            string chain=c.TryGetProperty("chains",out var chains)&&chains.ValueKind==JsonValueKind.Array?string.Join(" > ",chains.EnumerateArray().Select(x=>x.GetString())):"";
            string host=Text(meta,"host");if(host.Length==0)host=Text(meta,"destinationIP");
            list.Add(new(Text(c,"id"),proc,host,chain,Number(c,"download"),Number(c,"upload"),Text(c,"rule"),Text(c,"rulePayload")));
        }
        return new(list,Number(root,"downloadTotal"),Number(root,"uploadTotal"));
    }
    private static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var x)?x.ToString():"";
    private static long Number(JsonElement e,string key)=>e.TryGetProperty(key,out var x)&&x.TryGetInt64(out long n)?n:0;
}
internal static class PortOwners
{
    [DllImport("iphlpapi.dll")]private static extern uint GetExtendedTcpTable(IntPtr table,ref int length,bool order,int family,int tableClass,uint reserved);
    [DllImport("iphlpapi.dll")]private static extern uint GetExtendedUdpTable(IntPtr table,ref int length,bool order,int family,int tableClass,uint reserved);
    public static Dictionary<int,int> Get()
    {
        var result=new Dictionary<int,int>();
        foreach(bool udp in new[]{false,true})foreach(int family in new[]{2,23})
        {
            int size=0;
            if(udp)GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0);else GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,5,0);
            if(size<=4||size>32*1024*1024)continue;
            var mem=Marshal.AllocHGlobal(size);
            try
            {
                uint error=udp?GetExtendedUdpTable(mem,ref size,false,family,1,0):GetExtendedTcpTable(mem,ref size,false,family,5,0);
                if(error!=0)continue;
                int n=Marshal.ReadInt32(mem), rowSize=udp?(family==2?12:28):(family==2?24:56);
                for(int i=0;i<n&&4+(i+1)*rowSize<=size;i++)
                {
                    var row=IntPtr.Add(mem,4+i*rowSize);int portOffset=family==2?(udp?4:8):20;
                    int rawPort=Marshal.ReadInt32(row,portOffset);int port=((rawPort&255)<<8)|((rawPort>>8)&255);
                    if(!udp)
                    {
                        int rp=Marshal.ReadInt32(row,family==2?16:44);int remote=((rp&255)<<8)|((rp>>8)&255);
                        if(remote is not(7897 or 7898 or 7899))continue;
                    }
                    result.TryAdd(port,Marshal.ReadInt32(row,rowSize-4));
                }
            }finally{Marshal.FreeHGlobal(mem);}
        }
        return result;
    }
}
