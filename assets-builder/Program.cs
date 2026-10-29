using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "../assets");
Directory.CreateDirectory(output);
const string svg = """
<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
  <defs><linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#172C49"/><stop offset="1" stop-color="#0A1428"/></linearGradient></defs>
  <rect x="24" y="24" width="464" height="464" rx="112" fill="url(#tile)"/>
  <rect x="25" y="25" width="462" height="462" rx="111" fill="none" stroke="#304663" stroke-width="2"/>
  <g fill="none" stroke-width="44" stroke-linecap="round" stroke-linejoin="round">
    <path d="M144 204 L196 152 L248 204 M196 158 V356" stroke="#48DCF1"/>
    <path d="M264 308 L316 360 L368 308 M316 354 V156" stroke="#5895FF"/>
  </g>
</svg>
""";
File.WriteAllText(Path.Combine(output,"logo.svg"), svg, new UTF8Encoding(false));
GraphicsPath RoundRect(float x,float y,float w,float h,float r) {
 var p=new GraphicsPath(); float d=r*2;
 p.AddArc(x,y,d,d,180,90);p.AddArc(x+w-d,y,d,d,270,90);p.AddArc(x+w-d,y+h-d,d,d,0,90);p.AddArc(x,y+h-d,d,d,90,90);p.CloseFigure();return p;
}
Bitmap Render(int size) {
 int scale=4;using var hi=new Bitmap(size*scale,size*scale,PixelFormat.Format32bppArgb);
 using(var g=Graphics.FromImage(hi)) {
 g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size*scale/512f,size*scale/512f);
 using var tile=RoundRect(24,24,464,464,112);using var bg=new LinearGradientBrush(new Point(24,24),new Point(488,488),Color.FromArgb(23,44,73),Color.FromArgb(10,20,40));g.FillPath(bg,tile);
 using var border=RoundRect(25,25,462,462,111);using var outline=new Pen(Color.FromArgb(48,70,99),2);g.DrawPath(outline,border);
 void Arrow(Color color,PointF[] head,PointF a,PointF b){using var p=new Pen(color,44){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};g.DrawLines(p,head);g.DrawLine(p,a,b);}
 Arrow(Color.FromArgb(72,220,241),[new(144,204),new(196,152),new(248,204)],new(196,158),new(196,356));
 Arrow(Color.FromArgb(88,149,255),[new(264,308),new(316,360),new(368,308)],new(316,354),new(316,156));
 }
 var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);using var dst=Graphics.FromImage(result);dst.CompositingMode=CompositingMode.SourceCopy;dst.InterpolationMode=InterpolationMode.HighQualityBicubic;dst.DrawImage(hi,new Rectangle(0,0,size,size));return result;
}
using(var logo=Render(512)) logo.Save(Path.Combine(output,"logo.png"),ImageFormat.Png);
int[] sizes=[16,24,32,48,64,128,256];var frames=new List<byte[]>();foreach(var size in sizes){using var bitmap=Render(size);using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);frames.Add(stream.ToArray());}
using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"app.ico")))){writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;}foreach(var frame in frames)writer.Write(frame);}
using(var preview=new Bitmap(560,192)){using var g=Graphics.FromImage(preview);g.Clear(Color.FromArgb(239,243,249));int x=12;foreach(int size in new[]{16,24,32,48,64,128}){using var icon=Render(size);g.DrawImageUnscaled(icon,x,(192-size)/2);x+=size+24;}preview.Save(Path.Combine(output,"size-preview.png"),ImageFormat.Png);}
Console.WriteLine($"Created SVG, 512px PNG, ICO frames: {string.Join(", ",sizes)}");
