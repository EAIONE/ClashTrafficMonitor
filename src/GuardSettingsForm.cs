namespace ClashTraffic;
public sealed class GuardSettingsForm:Form
{
    public long SelectedLimit{get;private set;}
    public GuardSettingsForm(long current)
    {
        Text="流量保护设置";ClientSize=new Size(510,285);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",10);
        var heading=new Label{Text="机场扣量估算达到多少时暂停？",AutoSize=true,Font=new Font(Font,FontStyle.Bold),Location=new Point(24,20)};Controls.Add(heading);
        var number=new NumericUpDown{Minimum=0.01m,Maximum=10000m,DecimalPlaces=2,Increment=0.1m,Value=Math.Clamp(current/1_000_000_000m,0.01m,10000m),Location=new Point(24,65),Width=150,Name="ThresholdGb"};Controls.Add(number);
        Controls.Add(new Label{Text="GB（1 GB = 1000 MB）",AutoSize=true,Location=new Point(187,68)});
        var presets=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(24,112),Width=240,Name="Presets"};presets.Items.AddRange(new object[]{"100 MB","500 MB","1 GB","2 GB","5 GB","10 GB","自定义"});presets.SelectedIndex=6;Controls.Add(presets);
        bool changing=false;decimal[] values={0.1m,0.5m,1m,2m,5m,10m};
        presets.SelectedIndexChanged+=(_,_)=>{if(presets.SelectedIndex>=0&&presets.SelectedIndex<values.Length){changing=true;number.Value=values[presets.SelectedIndex];changing=false;}};
        number.ValueChanged+=(_,_)=>{if(!changing)presets.SelectedIndex=6;};
        Controls.Add(new Label{Text="修改阈值不会清零本次累计量。已经暂停时，仍需点击继续。\r\n未知流量暂按 1x 估算；每秒检查，可能略超。",Location=new Point(24,158),Size=new Size(460,58)});
        var apply=new Button{Text="保存",DialogResult=DialogResult.OK,Location=new Point(280,228),Size=new Size(90,34)};apply.Click+=(_,_)=>SelectedLimit=decimal.ToInt64(number.Value*1_000_000_000m);Controls.Add(apply);
        var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,Location=new Point(386,228),Size=new Size(90,34)};Controls.Add(cancel);AcceptButton=apply;CancelButton=cancel;
    }
}
