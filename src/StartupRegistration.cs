using System.Runtime.InteropServices;

namespace ClashTraffic;
public static class StartupRegistration
{
    public static string ShortcutPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"ClashTrafficMonitor.lnk");
    public static bool Enabled=>File.Exists(ShortcutPath);
    public static void CreateShortcut(string executable,string destination)
    {
        executable=Path.GetFullPath(executable);
        if(!File.Exists(executable))throw new FileNotFoundException("找不到监控程序",executable);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        object? shell=null,link=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("Windows 快捷方式服务不可用"));
            dynamic factory=shell!;link=factory.CreateShortcut(destination);dynamic shortcut=link!;
            shortcut.TargetPath=executable;shortcut.Arguments="--background";shortcut.WorkingDirectory=Path.GetDirectoryName(executable)!;
            shortcut.Description="Clash 流量监控 · 开机登录后在托盘运行";shortcut.Save();
        }
        finally
        {
            if(link!=null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);
            if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
        }
    }
    public static void SetEnabled(bool enabled,string executable)
    {
        if(enabled)CreateShortcut(executable,ShortcutPath);
        else if(File.Exists(ShortcutPath))File.Delete(ShortcutPath);
    }
}
