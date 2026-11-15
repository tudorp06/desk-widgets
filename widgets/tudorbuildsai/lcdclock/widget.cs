using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskWidgets;

[DeskWidget(Name = "Mac Dial", Description = "A minimal digital clock with four LCD and LED faces")]
public sealed class MacDialWidget : WidgetWindow
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left,top,right,bottom; }
    readonly Section settings;
    int theme = 5;
    static readonly int[] masks = {63,6,91,79,102,109,125,7,127,111};
    readonly string[] names = { "LCD · Cream", "LCD · Graphite", "LED · Green", "LED · White" };
    public MacDialWidget(Section settings) : base(settings) {
        this.settings=settings;
        int saved=settings.GetInt("face",5);
        theme=saved>=2 && saved<=5?saved:5;
    }
    protected override Size Measure() {return new Size(264,116);}
    protected override int NextFrameDelay() {return 1000;}
    protected override void Draw(Graphics g) {using(var face=Face(theme))g.DrawImageUnscaled(face,0,0);}
    protected override void BuildMenu(PopupMenu menu) {
        base.BuildMenu(menu);
        menu.AddSeparator();
        for(int i=0;i<names.Length;i++) {
            int choice=i+2;
            menu.AddChoice(names[i],theme==choice,delegate {theme=choice;settings.Set("face",choice);Render();});
        }
    }
    internal static Rectangle Constrain(Rectangle proposed, Rectangle area) {
        const int gap=8;
        int minX=area.Left+gap, minY=area.Top+gap;
        int maxX=Math.Max(minX,area.Right-gap-proposed.Width);
        int maxY=Math.Max(minY,area.Bottom-gap-proposed.Height);
        int x=Math.Max(minX,Math.Min(maxX,proposed.X));
        int y=Math.Max(minY,Math.Min(maxY,proposed.Y));
        return new Rectangle(x,y,proposed.Width,proposed.Height);
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x0216) {
            var r=(RECT)Marshal.PtrToStructure(m.LParam,typeof(RECT));
            var bounded=Constrain(Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom),Screen.FromPoint(Cursor.Position).WorkingArea);
            r.left=bounded.Left;r.top=bounded.Top;r.right=bounded.Right;r.bottom=bounded.Bottom;
            Marshal.StructureToPtr(r,m.LParam,false);m.Result=new IntPtr(1);return;
        }
        base.WndProc(ref m);
    }
    static GraphicsPath Round(float x,float y,float w,float h,float r){var p=new GraphicsPath();p.AddArc(x,y,r*2,r*2,180,90);p.AddArc(x+w-r*2,y,r*2,r*2,270,90);p.AddArc(x+w-r*2,y+h-r*2,r*2,r*2,0,90);p.AddArc(x,y+h-r*2,r*2,r*2,90,90);p.CloseFigure();return p;}
    static void Fill(Graphics g,Color c,float x,float y,float w,float h,float r){using(var p=Round(x,y,w,h,r))using(var b=new SolidBrush(c))g.FillPath(b,p);}
    Bitmap Face(int face) {
        var bmp=new Bitmap(264,116,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(bmp)) {
            g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            bool flip=face<2, cream=face==2, gray=face==3;
            Color frame=cream?Color.FromArgb(220,173,91):Color.FromArgb(224,227,222);
            Fill(g,Color.FromArgb(60,0,0,0),5,7,254,104,13);
            Fill(g,frame,3,3,258,106,12);
            Fill(g,Color.FromArgb(250,250,244),6,5,252,100,10);
            Color screen=flip?(face==0?Color.FromArgb(247,248,241):Color.FromArgb(244,239,171)):cream?Color.FromArgb(255,253,227):gray?Color.FromArgb(137,145,137):Color.FromArgb(10,14,13);
            Fill(g,Color.FromArgb(81,87,80),12,11,240,88,7);
            Fill(g,screen,15,14,234,82,5);
            Color ink=face==4?Color.FromArgb(38,246,96):face==5?Color.FromArgb(248,252,247):cream?Color.FromArgb(99,76,28):Color.FromArgb(26,30,25);
            string text=DateTime.Now.ToString("HHmm");
            for(int i=0;i<4;i++) {
                float x=33+i*49+(i>=2?10:0);
                if(flip) {
                    Fill(g,face==0?Color.FromArgb(233,235,227):Color.FromArgb(227,223,151),x-4,22,42,65,3);
                    using(var f=new Font("Consolas",37,FontStyle.Bold,GraphicsUnit.Pixel))using(var b=new SolidBrush(ink))using(var fmt=new StringFormat()){fmt.Alignment=StringAlignment.Center;fmt.LineAlignment=StringAlignment.Center;g.DrawString(text[i].ToString(),f,b,new RectangleF(x-4,22,42,65),fmt);}
                    using(var p=new Pen(Color.FromArgb(45,90,90,70)))g.DrawLine(p,x-3,54,x+37,54);
                } else DrawDigit(g,text[i]-'0',x,29,ink,screen);
            }
            if(!flip) using(var b=new SolidBrush(ink)){g.FillRectangle(b,125,43,5,6);g.FillRectangle(b,122,65,5,6);}
            using(var p=new Pen(Color.FromArgb(90,255,255,255)))g.DrawLine(p,20,15,242,15);
        }
        return bmp;
    }
    static void DrawDigit(Graphics g,int digit,float x,float y,Color ink,Color screen) {
        int mask=masks[digit];
        // Seven discrete beveled segments, with a slight LCD-style slant.
        PointF[][] shapes={
            new[]{new PointF(7,0),new PointF(31,0),new PointF(27,5),new PointF(10,5)},
            new[]{new PointF(32,2),new PointF(35,5),new PointF(32,23),new PointF(27,26),new PointF(28,7)},
            new[]{new PointF(31,29),new PointF(34,32),new PointF(31,50),new PointF(26,54),new PointF(27,34)},
            new[]{new PointF(5,52),new PointF(26,52),new PointF(30,56),new PointF(2,56)},
            new[]{new PointF(1,29),new PointF(6,33),new PointF(4,49),new PointF(0,53),new PointF(-1,50)},
            new[]{new PointF(5,2),new PointF(9,7),new PointF(7,23),new PointF(2,26),new PointF(2,7)},
            new[]{new PointF(7,25),new PointF(26,25),new PointF(30,28),new PointF(25,31),new PointF(6,31),new PointF(2,28)}
        };
        for(int i=0;i<7;i++){var pts=new PointF[shapes[i].Length];for(int j=0;j<pts.Length;j++)pts[j]=new PointF(x+shapes[i][j].X,y+shapes[i][j].Y);using(var b=new SolidBrush((mask&(1<<i))!=0?ink:Color.FromArgb(12,ink)))g.FillPolygon(b,pts);}
    }
}
