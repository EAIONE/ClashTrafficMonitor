using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClashTraffic;
public record SubscriptionUsage(string Name,long Used,long Total,long Expire,string Source,DateTime Updated);
internal record SubscriptionEntry(string Name,string Url,Dictionary<string,long> Extra);
public static class Subscriptions
{
    private static string ProfilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"io.github.clash-verge-rev.clash-verge-rev","profiles.yaml");
    private static List<SubscriptionEntry> ReadEntries()
    {
        var output=new List<SubscriptionEntry>(); if(!File.Exists(ProfilePath))return output;
        var current=new Dictionary<string,string>();var extra=new Dictionary<string,long>();bool inItems=false,inExtra=false;
        string Clean(string value)=>value.Trim().Trim('\'', '"');
        void Save(){if(current.GetValueOrDefault("type")=="remote"&&current.TryGetValue("url",out var url))output.Add(new(current.GetValueOrDefault("name",current.GetValueOrDefault("uid","订阅")),url,new(extra)));}
        foreach(string line in File.ReadLines(ProfilePath))
        {
            var text=line.Trim();if(text=="items:"){inItems=true;continue;}if(!inItems||text.Length==0||text.StartsWith('#'))continue;
            int indent=line.Length-line.TrimStart().Length;
            if(line.StartsWith("- ")){Save();current.Clear();extra.Clear();inExtra=false;text=text[2..];indent=2;}
            int colon=text.IndexOf(':');if(colon<0)continue;
            string key=text[..colon],value=Clean(text[(colon+1)..]);
            if(indent<=2){inExtra=key=="extra";current[key]=value;}
            else if(inExtra&&long.TryParse(value,out long n))extra[key]=n;
        }
        Save();return output;
    }
    public static List<SubscriptionUsage> ReadCached()=>ReadEntries().Select(x=>new SubscriptionUsage(x.Name,x.Extra.GetValueOrDefault("upload")+x.Extra.GetValueOrDefault("download"),x.Extra.GetValueOrDefault("total"),x.Extra.GetValueOrDefault("expire"),"Clash 本地缓存",File.GetLastWriteTime(ProfilePath))).ToList();
    public static async Task<List<SubscriptionUsage>> RefreshAsync(CancellationToken token)
    {
        var results=new List<SubscriptionUsage>();
        using var client=new HttpClient(new HttpClientHandler{Proxy=new WebProxy("http://127.0.0.1:7897"),UseProxy=true}){Timeout=TimeSpan.FromSeconds(15)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("clash-verge-rev/2.5.1");
        foreach(var entry in ReadEntries())
        {
            try
            {
                using var response=await client.GetAsync(entry.Url,HttpCompletionOption.ResponseHeadersRead,token);
                response.EnsureSuccessStatusCode();
                if(!response.Headers.TryGetValues("subscription-userinfo",out var values))throw new IOException("订阅未提供用量头");
                var numbers=new Dictionary<string,long>();
                foreach(Match match in Regex.Matches(string.Join(";",values),@"(upload|download|total|expire)\s*=\s*(\d+)",RegexOptions.IgnoreCase))numbers[match.Groups[1].Value.ToLowerInvariant()]=long.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
                results.Add(new(entry.Name,numbers.GetValueOrDefault("upload")+numbers.GetValueOrDefault("download"),numbers.GetValueOrDefault("total"),numbers.GetValueOrDefault("expire"),"订阅服务端",DateTime.Now));
            }
            catch when(!token.IsCancellationRequested){results.Add(new(entry.Name,entry.Extra.GetValueOrDefault("upload")+entry.Extra.GetValueOrDefault("download"),entry.Extra.GetValueOrDefault("total"),entry.Extra.GetValueOrDefault("expire"),"刷新失败 · 使用 Clash 缓存",File.GetLastWriteTime(ProfilePath)));}
        }
        return results;
    }
}
