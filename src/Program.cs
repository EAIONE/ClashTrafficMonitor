using System.Text.Json;

namespace ClashTraffic;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string root=AppContext.BaseDirectory;string data=Path.Combine(root,"data");Directory.CreateDirectory(data);
        if(args.Contains("--enable-startup"))
        {
            try{StartupRegistration.SetEnabled(true,Environment.ProcessPath!);return 0;}
            catch(Exception ex){File.WriteAllText(Path.Combine(data,"startup-error.log"),ex.GetType().Name);return 1;}
        }
        if(args.Contains("--diagnose"))
        {
            try{var s=new ClashClient().ReadConnectionsAsync().GetAwaiter().GetResult();File.WriteAllText(Path.Combine(root,"diagnostic.json"),JsonSerializer.Serialize(new{success=true,connections=s.Connections.Count,down=s.Down,up=s.Up,processes=s.Connections.Count(x=>x.Process.Length>0),time=DateTime.Now}));return 0;}
            catch(Exception ex){File.WriteAllText(Path.Combine(root,"diagnostic.json"),JsonSerializer.Serialize(new{success=false,error=ex.GetType().Name}));return 1;}
        }
        using var mutex=new Mutex(true,@"Local\ClashTrafficMonitor.Desktop.v1",out bool first);
        using var wake=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\ClashTrafficMonitor.Show.v1");
        using var quit=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\ClashTrafficMonitor.Exit.v1");
        if(args.Contains("--exit")){if(!first)quit.Set();return 0;}
        if(!first){wake.Set();return 0;}
        ApplicationConfiguration.Initialize();
        Application.ThreadException+=(_,e)=>{try{File.AppendAllText(Path.Combine(data,"ui-error.log"),$"{DateTime.Now:o} {e.Exception.GetType().Name}\r\n");}catch{}};
        using var form=new MainForm(data,args.Contains("--background"));
        var registration=ThreadPool.RegisterWaitForSingleObject(wake,(_,_)=>{try{if(form.IsHandleCreated&&!form.IsDisposed)form.BeginInvoke(form.RestoreWindow);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        var exitRegistration=ThreadPool.RegisterWaitForSingleObject(quit,(_,_)=>{try{if(form.IsHandleCreated&&!form.IsDisposed)form.BeginInvoke(form.ExitMonitor);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        try{Application.Run(form);}finally{registration.Unregister(null);exitRegistration.Unregister(null);}
        GC.KeepAlive(mutex);return 0;
    }
}
