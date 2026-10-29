using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace ClashTraffic;
public sealed class MainForm : Form
{
    private readonly CsvStore store;
    private readonly HistoryRepository history;
    private readonly Icon? appIcon;
    private readonly ClashClient client=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly NotifyIcon tray;
    private readonly DateTimePicker date=new(){Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd",Width=160,Name="HistoryAnchor"};
    private readonly ComboBox period=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=95,Name="HistoryPeriod"};
    private readonly DateTimePicker endDate=new(){Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd",Width=160,Visible=false,Name="HistoryEnd"};
    private readonly Label endLabel=new(){Text="至",AutoSize=true,Visible=false,Margin=new Padding(0,6,0,0)};
    private readonly Label rangeSummary=new(){AutoSize=true,ForeColor=Color.DimGray,Margin=new Padding(12,7,0,0)};
    private string historyKey="";private List<TrafficRecord> exportRows=new();
    private readonly ComboBox route=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=115};
    private readonly CheckBox proxyOnly=new(){Text="只看代理应用",Checked=true,AutoSize=true,Margin=new Padding(8,6,8,0)};
    private readonly TextBox search=new(){PlaceholderText="筛选程序 / 域名",Width=185};
    private readonly Label status=new(){AutoSize=true,ForeColor=Color.DimGray};
    private readonly Label totals=new(){AutoSize=true,Font=new Font("Microsoft YaHei UI",13,FontStyle.Bold)};
    private readonly Label routes=new(){AutoSize=true};
    private readonly Label rateLabel=new(){AutoSize=true,ForeColor=Color.FromArgb(29,93,173),Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold)};
    private readonly Label cloud=new(){AutoSize=true};
    private readonly DataGridView apps=Grid(),hosts=Grid(),live=Grid(),subscriptionGrid=Grid(),detailsGrid=Grid();
    private readonly HourChart chart=new(){Dock=DockStyle.Fill};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=3000};
    private bool exiting,refreshing,refreshPending;private Task? collector;private readonly object gate=new();
    private List<Connection> connections=new();private bool online;private DateTime? lastSuccess;
    private double downRate,upRate;private string problem="等待 Clash 内核响应…";
    private List<SubscriptionUsage> subscriptions=new();
    private readonly ToolStripMenuItem startup;
    private readonly Label guardLabel=new(){AutoSize=true,Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold)};
    private readonly Button continueButton=new(){Text="继续 · 重新计量",AutoSize=true,MinimumSize=new Size(150,34),Enabled=false};
    private readonly Button pauseButton=new(){Text="禁止流量",AutoSize=true,MinimumSize=new Size(110,34),ForeColor=Color.Firebrick};
    private readonly TransferBudget budget=new(1_000_000_000,GuardMode.EstimatedQuota);
    private readonly PauseController breaker;
    private readonly SemaphoreSlim controlLock=new(1,1);
    private readonly string budgetFile;
    private readonly string limitFile;
    private readonly string manualRequestFile;
    private bool manualPauseRequested;
    private string guardError="";private bool guardConfirmed;
    private bool followToday=true,adjustingDate;

    public MainForm(string dataDirectory,bool hidden=false)
    {
        store=new CsvStore(dataDirectory);breaker=new PauseController(client,Path.Combine(dataDirectory,"paused-state.json"));budgetFile=Path.Combine(dataDirectory,"budget.txt");
        history=new HistoryRepository(store);
        detailsGrid.Name="HistoryDetails";
        string iconFile=Path.Combine(AppContext.BaseDirectory,"app.ico");if(File.Exists(iconFile)){using var loaded=new Icon(iconFile);appIcon=(Icon)loaded.Clone();Icon=appIcon;}
        manualRequestFile=Path.Combine(dataDirectory,"manual-pause.request");manualPauseRequested=File.Exists(manualRequestFile);
        limitFile=Path.Combine(dataDirectory,"limit.txt");if(File.Exists(limitFile)&&long.TryParse(File.ReadAllText(limitFile),out long savedLimit)&&savedLimit>=10_000_000&&savedLimit<=10_000_000_000_000)budget.SetLimit(savedLimit);
        if(File.Exists(budgetFile)&&double.TryParse(File.ReadAllText(budgetFile),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double oldUsed))budget.Restore(oldUsed);
        Text="Clash 流量监控";Size=new Size(1380,1000);MinimumSize=new Size(1220,840);
        StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(246,248,252);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(22),BackColor=BackColor};
        layout.RowStyles.Add(new(SizeType.Absolute,52));layout.RowStyles.Add(new(SizeType.Absolute,140));layout.RowStyles.Add(new(SizeType.Absolute,58));layout.RowStyles.Add(new(SizeType.Absolute,94));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,62));
        Controls.Add(layout);
        var header=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        header.Controls.Add(new Label{Text="CLASH  /  流量监控",AutoSize=true,Font=new Font(Font.FontFamily,19,FontStyle.Bold),Margin=new Padding(0,0,25,0)});
        header.Controls.Add(status);status.Margin=new Padding(0,10,0,0);layout.Controls.Add(header,0,0);
        var cards=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        cards.Controls.Add(totals);cards.Controls.Add(routes);cards.Controls.Add(rateLabel);cards.Controls.Add(cloud);layout.Controls.Add(cards,0,1);
        var guardBar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,BackColor=Color.FromArgb(233,240,252),Padding=new Padding(7)};
        guardLabel.Margin=new Padding(0,6,12,0);guardBar.Controls.Add(guardLabel);guardBar.Controls.Add(pauseButton);guardBar.Controls.Add(continueButton);layout.Controls.Add(guardBar,0,2);
        pauseButton.Click+=async(_,_)=>await PauseManuallyAsync();
        continueButton.Click+=async(_,_)=>await ContinueAsync();
        var guardSettings=new Button{Text="保护设置…",AutoSize=true,MinimumSize=new Size(105,34)};guardBar.Controls.Add(guardSettings);guardSettings.Click+=async(_,_)=>await ConfigureGuardAsync();
        var tools=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};tools.RowStyles.Add(new(SizeType.Absolute,44));tools.RowStyles.Add(new(SizeType.Absolute,44));
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};var commands=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};tools.Controls.Add(toolbar,0,0);tools.Controls.Add(commands,0,1);
        toolbar.Controls.Add(new Label{Text="历史范围",AutoSize=true,Margin=new Padding(0,6,6,0)});
        period.Items.AddRange(new object[]{"按日","按周","按月","自定义"});period.SelectedIndex=0;toolbar.Controls.Add(period);toolbar.Controls.Add(date);toolbar.Controls.Add(endLabel);toolbar.Controls.Add(endDate);
        route.Items.AddRange(new object[]{"全部流量","代理","直连","未知","拒绝"});route.SelectedIndex=1;
        toolbar.Controls.Add(route);toolbar.Controls.Add(proxyOnly);toolbar.Controls.Add(search);
        proxyOnly.CheckedChanged+=(_,_)=>{if(proxyOnly.Checked&&route.SelectedIndex!=1)route.SelectedIndex=1;else if(!proxyOnly.Checked&&route.SelectedIndex==1)route.SelectedIndex=0;};
        var refresh=new Button{Text="刷新",AutoSize=true,MinimumSize=new Size(75,34)};refresh.Click+=async(_,_)=>await RenderAsync();commands.Controls.Add(refresh);
        var open=new Button{Text="打开数据目录",AutoSize=true,MinimumSize=new Size(130,34)};open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(store.DirectoryPath){UseShellExecute=true});commands.Controls.Add(open);
        var export=new Button{Text="导出筛选记录…",AutoSize=true,MinimumSize=new Size(130,34)};commands.Controls.Add(export);export.Click+=(_,_)=>ExportHistory();commands.Controls.Add(rangeSummary);
        layout.Controls.Add(tools,0,3);
        var tabs=new TabControl{Dock=DockStyle.Fill};
        AddTab(tabs,"程序排行",apps);AddTab(tabs,"域名排行",hosts);AddTab(tabs,"历史明细（按分钟）",detailsGrid);AddTab(tabs,"用量趋势",chart);AddTab(tabs,"当前连接（实时）",live);
        var quotaPanel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};quotaPanel.RowStyles.Add(new(SizeType.Absolute,44));quotaPanel.RowStyles.Add(new(SizeType.Percent,100));
        var quotaBar=new FlowLayoutPanel{Dock=DockStyle.Fill};var quotaRefresh=new Button{Text="联网刷新订阅余量",AutoSize=true};quotaBar.Controls.Add(quotaRefresh);
        quotaBar.Controls.Add(new Label{Text="服务端余量可能延迟；采样扣量是估算值",AutoSize=true,Margin=new Padding(12,9,0,0)});
        quotaRefresh.Click+=async(_,_)=>{quotaRefresh.Enabled=false;try{subscriptions=await Subscriptions.RefreshAsync(lifetime.Token);ShowSubscriptions();}catch(OperationCanceledException){}finally{if(!IsDisposed)quotaRefresh.Enabled=true;}};
        quotaPanel.Controls.Add(quotaBar,0,0);quotaPanel.Controls.Add(subscriptionGrid,0,1);AddTab(tabs,"订阅余量",quotaPanel);
        var help=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Color.White,Text="记录范围\r\n所有经过当前 Clash 内核的上传和下载均纳入统计，DIRECT 也记录。没有经过 Clash 的流量无法看到。\r\n\r\n统计口径\r\n每 2 秒采样连接增量，并用内核总字节增量补齐已结束的短连接。无法识别的短连接标为未归因，路由未知；不会假定是代理。新程序首次连接内核时建立基线，不回填监控前的总量。\r\n\r\n历史数据\r\n原脚本按 10 秒采样，忽略每个新连接的第一次累计值，也无法补齐短连接，因此历史统计可能低于实际用量。跨天历史已保留。\r\n\r\n节点扣量\r\n只对已确认的代理连接按节点名中的倍率估算；DIRECT 扣量为 0，未知流量不参与估算。机场账单及跨设备用量以订阅服务端为准。\r\n\r\niCloud\r\n看到 iCloud 的 DIRECT 记录表示它经过 Clash 后由本机直连；不是代理节点扣量。工具能看到域名和字节，不能知道加密连接中的文件名。\r\n\r\n后台运行\r\n关闭窗口会隐藏到托盘；双击托盘图标或再次启动本程序恢复。托盘右键选择“退出并停止监控”才结束采样。开机启动可在托盘右键菜单设置。"};AddTab(tabs,"说明",help);
        layout.Controls.Add(tabs,0,4);
        layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="全部经过 Clash 的流量都会记录 · 关闭窗口后继续采样 · 直连不等于机场扣量\r\n1 GB 保护按估算扣量计算，每秒检查一次，可能略超；未知流量暂按 1x 纳入保护。",ForeColor=Color.DimGray,AutoSize=false,Padding=new Padding(0,7,0,0)},0,5);
        var menu=new ContextMenuStrip();menu.Items.Add("打开流量看板",null,(_,_)=>RestoreWindow());menu.Items.Add("打开数据目录",null,(_,_)=>Process.Start(new ProcessStartInfo(store.DirectoryPath){UseShellExecute=true}));
        menu.Items.Add("禁止流量",null,async(_,_)=>await PauseManuallyAsync());
        menu.Items.Add("继续联网",null,async(_,_)=>await ContinueAsync());
        startup=new ToolStripMenuItem("开机启动"){CheckOnClick=true,Checked=StartupRegistration.Enabled};startup.Click+=(_,_)=>SetStartup(startup.Checked);menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出并停止监控",null,(_,_)=>ExitMonitor());
        tray=new NotifyIcon{Icon=appIcon??SystemIcons.Information,Text="Clash 流量监控",ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=(_,_)=>RestoreWindow();
        date.ValueChanged+=async(_,_)=>{if(!adjustingDate)followToday=period.SelectedIndex!=3&&date.Value.Date==DateTime.Today;await RenderAsync();};route.SelectedIndexChanged+=async(_,_)=>{proxyOnly.Checked=route.SelectedIndex==1;await RenderAsync();};search.TextChanged+=async(_,_)=>await RenderAsync();
        period.SelectedIndexChanged+=async(_,_)=>{bool custom=period.SelectedIndex==3;followToday=!custom&&date.Value.Date==DateTime.Today;endDate.Visible=custom;endLabel.Visible=custom;if(custom&&endDate.Value.Date<date.Value.Date)endDate.Value=date.Value.Date;await RenderAsync();};endDate.ValueChanged+=async(_,_)=>await RenderAsync();
        timer.Tick+=async(_,_)=>await RenderAsync();
        Shown+=async(_,_)=>{if(hidden)Hide();try{subscriptions=Subscriptions.ReadCached();ShowSubscriptions();}catch{ }collector=Task.Run(CollectAsync);timer.Start();await RenderAsync();};
        FormClosing+=(_,e)=>{if(!exiting){e.Cancel=true;Hide();return;}lifetime.Cancel();timer.Stop();tray.Visible=false;};
        FormClosed+=(_,_)=>{tray.Dispose();timer.Dispose();appIcon?.Dispose();};
    }
    public void RestoreWindow(){Show();WindowState=FormWindowState.Normal;Activate();}
    public void ExitMonitor(){exiting=true;Close();}
    private void SetStartup(bool enable)
    {
        try {StartupRegistration.SetEnabled(enable,Environment.ProcessPath!);}
        catch {startup.Checked=!enable;MessageBox.Show(this,"无法修改开机启动项，请检查目录权限。","开机启动");}
    }
    private record GridSort(string Property,ListSortDirection Direction);
    private static DataGridView Grid()
    {
        var grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoGenerateColumns=true,ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize};
        grid.Sorted+=(_,_)=>{if(grid.SortedColumn!=null)grid.Tag=new GridSort(grid.SortedColumn.DataPropertyName,grid.SortOrder==SortOrder.Descending?ListSortDirection.Descending:ListSortDirection.Ascending);};return grid;
    }
    private static void BindGrid<T>(DataGridView grid,List<T> rows,int visibleLimit=int.MaxValue)
    {
        var selected=grid.Tag as GridSort;grid.DataSource=new SortableBindingList<T>(rows,visibleLimit);
        foreach(DataGridViewColumn column in grid.Columns)column.SortMode=DataGridViewColumnSortMode.Automatic;
        if(selected!=null){var column=grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(x=>x.DataPropertyName==selected.Property);if(column!=null)grid.Sort(column,selected.Direction);}
    }
    private void ExportHistory()
    {
        using var dialog=new SaveFileDialog{Filter="CSV 记录 (*.csv)|*.csv",FileName=$"Clash流量记录_{DateTime.Now:yyyyMMdd_HHmmss}.csv"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{CsvStore.Export(dialog.FileName,exportRows,store.DirectoryPath);MessageBox.Show(this,$"已导出 {exportRows.Count} 条原始记录。","导出完成");}catch(InvalidOperationException e){MessageBox.Show(this,e.Message,"导出失败");}catch(IOException){MessageBox.Show(this,"导出失败，请检查文件是否被占用和目录权限。","导出失败");}catch(UnauthorizedAccessException){MessageBox.Show(this,"没有权限写入该文件。","导出失败");}
    }
    private static void AddTab(TabControl tabs,string text,Control control){var page=new TabPage(text){Padding=new Padding(12),BackColor=Color.White};page.Controls.Add(control);tabs.TabPages.Add(page);}
    private async Task CollectAsync()
    {
        var accounting=new TrafficAccounting();ConnectionSnapshot? previous=null;DateTime? prevTime=null;
        var pending=new Queue<List<TrafficRecord>>();bool announced=false,recovered=false;
        while(!lifetime.IsCancellationRequested)
        {
            try
            {
                // 已暂停时先独立重申阻断，CSV 故障或采样失败均不得跳过保护。
                await controlLock.WaitAsync(lifetime.Token);
                try
                {
                    if(breaker.Paused||manualPauseRequested||budget.Triggered)
                    {
                        try{await breaker.PauseAsync(lifetime.Token,manualPauseRequested?"manual":"threshold");guardConfirmed=true;}
                        catch when(!lifetime.IsCancellationRequested){guardConfirmed=false;guardError="阻断未确认，正在重试";}
                    }
                }finally{controlLock.Release();}
                string diskError="";
                if(!recovered)
                {
                    try{store.RecoverPending();recovered=true;}
                    catch(IOException){diskError=" · 历史事务恢复失败，正在重试";}
                    catch(UnauthorizedAccessException){diskError=" · 历史数据目录不可写";}
                }
                var snapshot=await client.ReadConnectionsAsync(lifetime.Token);var now=DateTime.Now;
                var records=accounting.Sample(snapshot.Connections,snapshot.Down,snapshot.Up,now);
                await controlLock.WaitAsync(lifetime.Token);
                try
                {
                    budget.Add(records);
                    try{SaveBudget();}catch{guardError="保护计量未能写入磁盘";}
                    if(budget.Triggered||breaker.Paused||manualPauseRequested)
                    {
                        try{await breaker.PauseAsync(lifetime.Token,manualPauseRequested?"manual":"threshold");guardConfirmed=true;guardError="";}
                        catch when(!lifetime.IsCancellationRequested){guardConfirmed=false;guardError="自动阻断未确认，正在重试；请先停止大下载";}
                        if(!announced)
                        {
                            announced=true;
                            BeginInvoke(()=>{RestoreWindow();tray.ShowBalloonTip(15000,"Clash 流量保护",guardConfirmed?(breaker.Reason=="manual"?"你已手动禁止流量，点击继续后恢复。":$"本次扣量估算已达 {budget.Limit/1_000_000_000.0:0.##} GB，已暂停联网。点击继续后恢复。") :"阻断未确认，请查看窗口。",ToolTipIcon.Warning);System.Media.SystemSounds.Exclamation.Play();});
                        }
                    }
                    else announced=false;
                }
                finally{controlLock.Release();}
                if(records.Count>0)pending.Enqueue(records);
                while(pending.Count>0)
                {
                    try{store.Append(pending.Peek());pending.Dequeue();}
                    catch(IOException){diskError=" · 写盘失败，数据暂存内存并重试";break;}
                    catch(UnauthorizedAccessException){diskError=" · 数据目录无写入权限";break;}
                }
                lock(gate)
                {
                    connections=snapshot.Connections;online=true;lastSuccess=now;problem=diskError;
                    double elapsed=prevTime.HasValue?Math.Max(0.1,(now-prevTime.Value).TotalSeconds):1;
                    downRate=previous==null?0:Math.Max(0,snapshot.Down-previous.Down)/elapsed;upRate=previous==null?0:Math.Max(0,snapshot.Up-previous.Up)/elapsed;
                }
                previous=snapshot;prevTime=now;
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                lock(gate){online=false;problem=ex is OperationCanceledException?"查询超时":ex is IOException?"命名管道连接失败":"Clash 暂不可用";}
                if(breaker.Paused){guardConfirmed=false;guardError="内核暂不可用，暂停状态等待重新确认";}
                // 仅记录错误类型，避免泄露密钥或订阅地址。
                try{File.AppendAllText(Path.Combine(store.DirectoryPath,"monitor.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.GetType().Name}\r\n");}catch{}
            }
            try{await Task.Delay(1000,lifetime.Token);}catch(OperationCanceledException){break;}
        }
    }
    private void SaveBudget()
    {
        File.WriteAllText(budgetFile+".tmp",budget.Used.ToString(System.Globalization.CultureInfo.InvariantCulture));File.Move(budgetFile+".tmp",budgetFile,true);
    }
    private async Task ContinueAsync()
    {
        continueButton.Enabled=false;
        await controlLock.WaitAsync(lifetime.Token);
        try
        {
            await breaker.ResumeAsync(lifetime.Token);File.Delete(manualRequestFile);manualPauseRequested=false;budget.Reset();SaveBudget();guardConfirmed=false;guardError="";
        }
        catch when(!lifetime.IsCancellationRequested){guardError="恢复未确认，请检查 Clash 后再次点击继续";}
        finally{controlLock.Release();}
        await RenderAsync();
    }
    private async Task PauseManuallyAsync()
    {
        pauseButton.Enabled=false;manualPauseRequested=true;
        await controlLock.WaitAsync(lifetime.Token);
        try
        {
            try{File.WriteAllText(manualRequestFile,"manual");}catch(IOException){}catch(UnauthorizedAccessException){}
            await breaker.PauseAsync(lifetime.Token,"manual");guardConfirmed=true;guardError="";
        }
        catch when(!lifetime.IsCancellationRequested){guardConfirmed=false;guardError="禁止流量未确认，请检查 Clash，程序将重试";}
        finally{controlLock.Release();}
        await RenderAsync();
    }
    private async Task ConfigureGuardAsync()
    {
        using var dialog=new GuardSettingsForm(budget.Limit);if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        await controlLock.WaitAsync(lifetime.Token);
        try
        {
            File.WriteAllText(limitFile+".tmp",dialog.SelectedLimit.ToString());File.Move(limitFile+".tmp",limitFile,true);budget.SetLimit(dialog.SelectedLimit);
        }
        catch(IOException){MessageBox.Show(this,"阈值未能保存，请检查数据目录。旧阈值继续生效。","保护设置");}
        catch(UnauthorizedAccessException){MessageBox.Show(this,"数据目录不可写，旧阈值继续生效。","保护设置");}
        finally{controlLock.Release();}
        await RenderAsync();
    }
    private async Task RenderAsync()
    {
        if(IsDisposed)return;if(refreshing){refreshPending=true;return;}refreshing=true;
        try
        {
            if(followToday&&date.Value.Date!=DateTime.Today){adjustingDate=true;date.Value=DateTime.Today;adjustingDate=false;}
            UpdateGuardView();
            string filter=route.SelectedItem?.ToString()??"全部流量",term=search.Text.Trim();
            await RefreshHistoryAsync(filter,term);if(IsDisposed)return;
            List<Connection> current;bool connected;DateTime? last;string error;double dn,up;
            lock(gate){current=new(connections);connected=online;last=lastSuccess;error=problem;dn=downRate;up=upRate;}
            status.Text=connected?$"● 正在监控 · {last:HH:mm:ss} · {current.Count} 个连接{error}":$"● {error} · 最后成功 {last?.ToString("HH:mm:ss")??"尚无"}";
            status.ForeColor=connected?Color.SeaGreen:Color.Firebrick;
            rateLabel.Text=connected?$"实时速度   ↓ {Routing.Format(dn)}/s    ↑ {Routing.Format(up)}/s":"实时速度不可用 · 保留历史数据";
            var liveRows=current.Where(c=>(filter=="全部流量"||Routing.Classify(c.Chain)==filter)&&(c.Process.Contains(term,StringComparison.OrdinalIgnoreCase)||c.Host.Contains(term,StringComparison.OrdinalIgnoreCase))).OrderByDescending(c=>c.Down+c.Up).Take(250).ToList();
            BindGrid(live,liveRows.Select(c=>new{程序=string.IsNullOrEmpty(c.Process)?"（未知）":c.Process,域名=c.Host,下载累计=new ByteQuantity(c.Down),上传累计=new ByteQuantity(c.Up),路由=Routing.Classify(c.Chain),倍率=Routing.Classify(c.Chain)=="代理"?$"{Routing.Multiplier(c.Chain):0.##}x":"—",链路=c.Chain,命中规则=c.Rule+" "+c.RulePayload}).ToList());
            var icloud=current.Where(c=>(c.Process+" "+c.Host).Contains("icloud",StringComparison.OrdinalIgnoreCase)||c.Host.Contains("apple",StringComparison.OrdinalIgnoreCase)).ToList();
            cloud.Text=icloud.Count==0?"iCloud 当前无活跃连接；可在程序/域名排行搜索 iCloud 查看历史":$"iCloud / Apple 当前 {icloud.Count} 个连接："+string.Join("、",icloud.GroupBy(c=>Routing.Classify(c.Chain)).Select(g=>$"{g.Key} {g.Count()} 个"));
            cloud.ForeColor=icloud.Any(c=>Routing.Classify(c.Chain)=="代理")?Color.Firebrick:Color.DimGray;
            tray.Text=connected?$"Clash  ↓ {Routing.Format(dn)}/s  ↑ {Routing.Format(up)}/s":"Clash 暂不可用 · 历史数据已保留";
            UpdateGuardView();
        }
        catch(Exception){status.Text="数据刷新失败，下一轮自动重试";status.ForeColor=Color.Firebrick;}
        finally{refreshing=false;if(refreshPending&&!IsDisposed){refreshPending=false;_ = RenderAsync();}}
    }
    private void UpdateGuardView()
    {
        bool blocked=breaker.Paused||budget.Triggered||manualPauseRequested;
        guardLabel.Text=blocked?(guardConfirmed?(breaker.Reason=="manual"?"已禁止流量 · 等待你点击继续":$"已暂停联网 · 阈值 {budget.Limit/1_000_000_000.0:0.##} GB · 等待你继续"):$"保护待确认：{guardError}"):$"扣量保护 · 本次 {budget.Used/1_000_000:0.0} MB / {budget.Limit/1_000_000:0.##} MB";
        if(breaker.PersistenceError.Length>0)guardLabel.Text+=" · 暂停状态未保存，请勿退出";
        guardLabel.ForeColor=blocked?Color.Firebrick:Color.FromArgb(29,93,173);
        continueButton.Enabled=blocked;pauseButton.Enabled=!blocked;pauseButton.Text=blocked?"已禁止流量":"禁止流量";
        if(blocked)tray.Text=guardConfirmed?"Clash 已暂停 · 打开后点击继续":"Clash 保护异常 · 请查看窗口";
    }
    private async Task RefreshHistoryAsync(string filter,string term)
    {
        try
        {
            var range=HistoryRange.Resolve((HistoryPeriod)period.SelectedIndex,date.Value,endDate.Value);
            var snapshot=await Task.Run(()=>history.Read(range.Start,range.End));if(IsDisposed)return;
            string key=$"{snapshot.Version}/{filter}/{term}";if(key==historyKey)return;
            var projection=await Task.Run(()=>
            {
                var filtered=snapshot.Records.Where(r=>(filter=="全部流量"||r.Route==filter)&&(r.Process.Contains(term,StringComparison.OrdinalIgnoreCase)||r.Host.Contains(term,StringComparison.OrdinalIgnoreCase))).ToList();
                return (All:TrafficStats.Build(snapshot.Records),Filtered:filtered,Stats:TrafficStats.Build(filtered),Details:HistoryDetails.Build(filtered),Series:HistorySeries.Build(filtered,range.Start,range.End));
            });if(IsDisposed)return;
            exportRows=projection.Filtered;var total=projection.All;var stats=projection.Stats;
            string heading=range.Start==range.End?range.Start.ToString("MM 月 dd 日"):$"{range.Start:yyyy-MM-dd} — {range.End:yyyy-MM-dd}";
            totals.Text=$"{heading}   总计 {Routing.Format(total.Total)}    ↓ {Routing.Format(total.Down)}    ↑ {Routing.Format(total.Up)}";
            routes.Text=$"全量：代理 {Routing.Format(total.Proxy)}  |  直连 {Routing.Format(total.Direct)}  |  未知/其他 {Routing.Format(total.Unknown)}  |  已归因代理扣量估算 {Routing.Format(total.Quota)}";
            BindGrid(apps,stats.Apps.Select(r=>new{程序=r.Name,流量=new ByteQuantity(r.Total),下载=new ByteQuantity(r.Down),上传=new ByteQuantity(r.Up),路由=r.Route,代理扣量估算=new ByteQuantity(r.Quota),首次记录=r.First.ToString("yyyy-MM-dd HH:mm:ss"),末次记录=r.Last.ToString("yyyy-MM-dd HH:mm:ss")}).ToList());
            BindGrid(hosts,stats.Hosts.Select(r=>new{域名=r.Name,流量=new ByteQuantity(r.Total),下载=new ByteQuantity(r.Down),上传=new ByteQuantity(r.Up),路由=r.Route,代理扣量估算=new ByteQuantity(r.Quota)}).ToList());
            BindGrid(detailsGrid,projection.Details.Select(r=>new{时间=r.Time.ToString("yyyy-MM-dd HH:mm:ss"),末次=r.Last.ToString("HH:mm:ss"),程序=r.Process,域名=r.Host,流量=new ByteQuantity(r.Down+r.Up),下载=new ByteQuantity(r.Down),上传=new ByteQuantity(r.Up),路由=r.Route,代理扣量估算=new ByteQuantity(r.Quota)}).ToList(),2000);
            chart.SetData(projection.Series);
            rangeSummary.Text=$"{range.Start:MM-dd} 至 {range.End:MM-dd} · {filter} · {stats.Apps.Count} 个程序 · 明细 {Math.Min(2000,projection.Details.Count)}/{projection.Details.Count} 组";
            historyKey=key;
        }
        catch(ArgumentException e){rangeSummary.Text=e.Message;historyKey="";exportRows=new();}
        catch(IOException){rangeSummary.Text="历史数据暂不可读，自动重试；实时监控继续";historyKey="";exportRows=new();}
        catch(UnauthorizedAccessException){rangeSummary.Text="历史数据目录无读取权限";historyKey="";exportRows=new();}
    }
    private void ShowSubscriptions()
    {
        subscriptionGrid.DataSource=subscriptions.Select(s=>new{订阅=s.Name,已用=Routing.Format(s.Used),总量=s.Total>0?Routing.Format(s.Total):"未提供",剩余=s.Total>0?Routing.Format(Math.Max(0,s.Total-s.Used)):"未知",使用率=s.Total>0?$"{100.0*s.Used/s.Total:0.0}%":"未知",到期=s.Expire>0?DateTimeOffset.FromUnixTimeSeconds(s.Expire).LocalDateTime.ToString("yyyy-MM-dd"):"未提供",来源=s.Source,缓存更新时间=s.Updated.ToString("MM-dd HH:mm")}).ToList();
    }
}
public sealed class HourChart:Control
{
    private long[] values=new long[24];private string[] labels=Enumerable.Range(0,24).Select(x=>$"{x:00}").ToArray();private string caption="用量趋势";
    public HourChart(){DoubleBuffered=true;BackColor=Color.White;}
    public void SetData(ChartData data){values=data.Values;labels=data.Labels;caption=data.Caption;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;var area=new Rectangle(65,45,Math.Max(100,Width-90),Math.Max(80,Height-100));
        long max=Math.Max(1,values.Max());using var font=new Font("Microsoft YaHei UI",9);using var brush=new SolidBrush(Color.FromArgb(57,113,205));
        g.DrawString(caption+" · 当前范围与筛选条件",font,Brushes.DimGray,65,14);
        for(int i=0;i<=4;i++){float y=area.Bottom-area.Height*i/4f;g.DrawLine(Pens.Gainsboro,area.Left,y,area.Right,y);g.DrawString(Routing.Format(max*i/4.0),font,Brushes.DimGray,0,y-8);}
        float w=area.Width/(float)Math.Max(1,values.Length);int step=Math.Max(1,(int)Math.Ceiling(values.Length/12.0));
        for(int i=0;i<values.Length;i++){float height=(float)(values[i]/(double)max*area.Height);g.FillRectangle(brush,area.Left+i*w+Math.Min(3,w/8),area.Bottom-height,Math.Max(1,w-Math.Min(6,w/4)),height);if(i%step==0)g.DrawString(labels[i],font,Brushes.DimGray,area.Left+i*w,area.Bottom+8);}
        if(values.All(x=>x==0))g.DrawString("此日期暂无符合筛选条件的流量记录",Font,Brushes.DimGray,area.Left+20,area.Top+30);
    }
}
