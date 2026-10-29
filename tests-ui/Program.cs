using ClashTraffic;
using System.Reflection;
using System.Text;
class UiTests
{
 [STAThread]static int Main()
 {
  ApplicationConfiguration.Initialize();int result=1;using var context=new ApplicationContext();using var starter=new System.Windows.Forms.Timer{Interval=1};
  starter.Tick+=(_,_)=>{starter.Stop();try{result=RunChecks();}finally{context.ExitThread();}};starter.Start();Application.Run(context);return result;
 }
 static int RunChecks()
 {
  string temp=Path.Combine(Path.GetTempPath(),"clash-ui-"+Guid.NewGuid());Directory.CreateDirectory(temp); int passed=0;
  void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
  void Pump(int milliseconds){var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(20);}}
  bool WaitFor(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<end){Pump(30);if(condition())return true;}return false;}
  try
  {
   Check(Encoding.UTF8.GetString(ClashClient.Unchunk(Encoding.ASCII.GetBytes("4\r\ntest\r\n0\r\n\r\n")))=="test","chunked HTTP decoded");
   try {ClashClient.Unchunk(Encoding.ASCII.GetBytes("9\r\nshort"));throw new Exception("truncated chunk accepted");}catch(IOException){passed++;Console.WriteLine("PASS truncated chunk rejected");}
   var store=new CsvStore(temp);store.Append(new[]{new TrafficRecord(DateTime.Now,"edge.icloud-content.com.cn","iCloudDrive.exe","DIRECT > 全球直连",1,3L*1024*1024*1024,1000),new TrafficRecord(DateTime.Now,"proxy.example","ProxyOnlySample.exe","node 2x",2,100,0),new TrafficRecord(DateTime.Now,"direct.example","ProxyOnlySample.exe","DIRECT",1,1000,0)});
   store.Append(new[]{new TrafficRecord(DateTime.Today.AddDays(-1).AddHours(12),"past.example","YesterdaySample.exe","node 2x",2,2048,0)});
   Control.CheckForIllegalCrossThreadCalls=true;using var form=new MainForm(temp);form.Text="Clash 流量监控 · 自动验证";form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;form.Show();Pump(10000);
   var shortcutFile=Path.Combine(temp,"monitor-test.lnk");
   var target=Path.Combine(AppContext.BaseDirectory,"ClashTrafficMonitor.exe");
   StartupRegistration.CreateShortcut(target,shortcutFile);
   dynamic shortcutShell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
   dynamic testShortcut=shortcutShell.CreateShortcut(shortcutFile);
   Check(Path.GetFullPath((string)testShortcut.TargetPath)==Path.GetFullPath(target)&&(string)testShortcut.Arguments=="--background","startup shortcut targets native app with background argument");
   System.Runtime.InteropServices.Marshal.FinalReleaseComObject(testShortcut);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcutShell);
   using(var settings=new GuardSettingsForm(1_000_000_000))
   {
    settings.StartPosition=FormStartPosition.Manual;settings.Location=new Point(-20000,-20000);settings.ShowInTaskbar=false;
    settings.Show();Pump(200);
    var presets=settings.Controls.OfType<ComboBox>().Single();var number=settings.Controls.OfType<NumericUpDown>().Single();
    presets.SelectedIndex=4;Check(number.Value==5m,"5GB preset sets editable field");
    number.Value=0.37m;Check(presets.SelectedIndex==6,"free numeric input selects custom threshold");
    using(var bitmap=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(temp,"settings-preview.png"));}
    settings.Controls.OfType<Button>().Single(x=>x.Text=="保存").PerformClick();
    Check(settings.SelectedLimit==370_000_000,"custom decimal threshold saved in exact bytes");
   }
   Check(form.Visible,"window opens");
   IEnumerable<Control> Desc(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Desc(x)));
   var proxyOnly=Desc(form).OfType<CheckBox>().SingleOrDefault(x=>x.Text=="只看代理应用");
   Check(proxyOnly!=null&&proxyOnly.Checked,"proxy-only shortcut is visible and enabled by default");
   var proxyGrid=Desc(form).OfType<DataGridView>().First();
   Check(!proxyGrid.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells.Cast<DataGridViewCell>().Any(c=>c.Value?.ToString()=="iCloudDrive.exe")),"proxy-only view hides direct-only apps");
   var mixedApp=proxyGrid.Rows.Cast<DataGridViewRow>().Single(r=>r.Cells[0].Value?.ToString()=="ProxyOnlySample.exe");
   Check(mixedApp.Cells[1].Value?.ToString()=="100 B","mixed-route app includes only its proxy bytes");
   proxyOnly!.Checked=false;
   Check(WaitFor(()=>proxyGrid.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells[0].Value?.ToString()=="iCloudDrive.exe")),"unchecking proxy-only restores direct apps after async refresh");
   var labels=Desc(form).OfType<Label>().Select(x=>x.Text).ToArray();
   Check(labels.Any(x=>x.Contains("正在监控")),"real kernel connected and window remains responsive");
   Check(labels.Any(x=>x.Contains("总计 3")),"historical total rendered");
   var grids=Desc(form).OfType<DataGridView>().ToList();
   Check(Desc(form).OfType<Button>().Any(b=>b.Text=="禁止流量"&&b.Enabled),"manual block button available without reaching cap");
   Check(grids.Any(g=>g.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells.Cast<DataGridViewCell>().Any(c=>c.Value?.ToString()=="iCloudDrive.exe"))),"process ranking rendered");
   Check(grids.Any(g=>g.Columns.Cast<DataGridViewColumn>().Any(c=>c.HeaderText=="来源")),"cached subscription table rendered");
   Check(File.Exists(Path.Combine(AppContext.BaseDirectory,"app.ico")),"application logo distributed with executable");
   proxyGrid.Sort(proxyGrid.Columns["流量"],System.ComponentModel.ListSortDirection.Descending);
   Check(proxyGrid.Rows[0].Cells[0].Value?.ToString()=="iCloudDrive.exe","clickable size column sorts descending by actual bytes");
   Pump(3500);Check(proxyGrid.SortOrder==SortOrder.Descending&&proxyGrid.Rows[0].Cells[0].Value?.ToString()=="iCloudDrive.exe","selected sort persists through refresh");
   var periods=Desc(form).OfType<ComboBox>().Single(c=>c.Name=="HistoryPeriod");
   bool HasYesterday()=>proxyGrid.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells[0].Value?.ToString()=="YesterdaySample.exe");
   Check(!HasYesterday(),"day view does not include yesterday");
   periods.SelectedIndex=1;Check(WaitFor(HasYesterday),"week filter combines local daily files");
   periods.SelectedIndex=2;Check(WaitFor(HasYesterday),"month filter combines local daily files");
   var mainTabs=Desc(form).OfType<TabControl>().Single();mainTabs.SelectedTab=mainTabs.TabPages.Cast<TabPage>().Single(p=>p.Text.StartsWith("历史明细"));Pump(500);
   var detailGrid=Desc(form).OfType<DataGridView>().Single(g=>g.Name=="HistoryDetails");
   Check(detailGrid.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells.Cast<DataGridViewCell>().Any(c=>c.Value?.ToString()=="YesterdaySample.exe")),"historical details connect timestamp with program");
   periods.SelectedIndex=0;Check(WaitFor(()=>!HasYesterday()),"switching back to day narrows history");
   form.Close();Pump(500);Check(!form.Visible&&!form.IsDisposed,"close hides without stopping monitor");
   var last=Directory.GetFiles(temp,"clash_*.csv").Select(File.GetLastWriteTimeUtc).Max();Pump(4500);
   Check(Directory.GetFiles(temp,"clash_*.csv").Select(File.GetLastWriteTimeUtc).Max()>=last,"background collector survives hidden window");
   form.RestoreWindow();Pump(500);Check(form.Visible,"window can restore");
   string preview=Path.Combine(temp,"preview.png");
   using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(preview);}
   var screenshotOutput=Environment.GetEnvironmentVariable("CLASH_SCREENSHOT_OUTPUT");
   if(!string.IsNullOrWhiteSpace(screenshotOutput))
   {
    Directory.CreateDirectory(screenshotOutput);form.Text="Clash 流量监控 · 演示数据";
    var search=Desc(form).OfType<TextBox>().Single(t=>t.PlaceholderText=="筛选程序 / 域名");search.Text="ProxyOnlySample.exe";
    WaitFor(()=>proxyGrid.Rows.Cast<DataGridViewRow>().All(r=>r.Cells[0].Value?.ToString()=="ProxyOnlySample.exe"));
    mainTabs.SelectedIndex=0;Pump(250);
    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(screenshotOutput,"overview.png"));}
    mainTabs.SelectedTab=mainTabs.TabPages.Cast<TabPage>().Single(p=>p.Text.StartsWith("历史明细"));Pump(250);
    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(screenshotOutput,"history.png"));}
    File.Copy(Path.Combine(temp,"settings-preview.png"),Path.Combine(screenshotOutput,"protection.png"),true);
   }
   var icon=(NotifyIcon)typeof(MainForm).GetField("tray",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
   var exit=icon.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(x=>x.Text=="退出并停止监控");exit.PerformClick();Pump(500);Check(form.IsDisposed,"tray exit ends form");
   Console.WriteLine($"{passed} UI/integration checks passed");return 0;
  }
  catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{try{Directory.Delete(temp,true);}catch{}}
 }
}
