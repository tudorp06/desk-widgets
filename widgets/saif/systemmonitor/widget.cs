using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DeskWidgets.Widgets
{
    [DeskWidget(Name = "System Monitor", Description = "CPU, memory, disk and network")]
    sealed class SystemMonitor : CardWidget
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        const int HistLen = 30;
        const int SlowEvery = 5;

        float cpuPct;
        long prevIdle, prevKernel, prevUser;
        bool cpuReady;
        int ramPct;
        int batPct;
        bool hasBat;
        ulong uptimeMs;

        readonly float[] cpuHist = new float[HistLen];
        int histHead, histCount;

        struct DiskEntry { public string Label; public float UsedGB, TotalGB, Pct; }
        DiskEntry[] diskEntries = new DiskEntry[0];
        long prevNetIn, prevNetOut;
        float netDown, netUp;
        bool netReady;

        int tick;

        public SystemMonitor(Section settings) : base(settings, CardSize.Large) { }

        protected override int NextFrameDelay() { return 2000; }

        void PollFast()
        {
            long idle, kern, usr;
            if (GetSystemTimes(out idle, out kern, out usr))
            {
                if (cpuReady)
                {
                    long di = idle - prevIdle, dk = kern - prevKernel, du = usr - prevUser;
                    long total = dk + du;
                    cpuPct = total > 0 ? (total - di) / (float)total * 100f : 0;
                }
                prevIdle = idle;
                prevKernel = kern;
                prevUser = usr;
                cpuReady = true;
            }

            cpuHist[histHead] = cpuPct;
            histHead = (histHead + 1) % HistLen;
            if (histCount < HistLen) histCount++;

            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref mem))
                ramPct = (int)mem.dwMemoryLoad;

            SYSTEM_POWER_STATUS pw;
            if (GetSystemPowerStatus(out pw))
            {
                hasBat = (pw.BatteryFlag & 128) == 0;
                batPct = pw.BatteryLifePercent <= 100 ? pw.BatteryLifePercent : 0;
            }

            uptimeMs = GetTickCount64();
        }

        void PollSlow()
        {
            try
            {
                var list = new List<DiskEntry>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    float total = d.TotalSize / (1024f * 1024 * 1024);
                    float used = total - d.AvailableFreeSpace / (1024f * 1024 * 1024);
                    list.Add(new DiskEntry
                    {
                        Label = d.Name.TrimEnd('\\'),
                        TotalGB = total,
                        UsedGB = used,
                        Pct = total > 0 ? used / total : 0
                    });
                }
                diskEntries = list.ToArray();
            }
            catch { }

            try
            {
                long bIn = 0, bOut = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var s = ni.GetIPStatistics();
                    bIn += s.BytesReceived;
                    bOut += s.BytesSent;
                }
                if (netReady)
                {
                    float secs = SlowEvery * 2f;
                    netDown = Math.Max(0, (bIn - prevNetIn) / secs);
                    netUp = Math.Max(0, (bOut - prevNetOut) / secs);
                }
                prevNetIn = bIn;
                prevNetOut = bOut;
                netReady = true;
            }
            catch { }
        }

        protected override void DrawContent(Graphics g, Palette pal)
        {
            PollFast();
            if (tick % SlowEvery == 0) PollSlow();
            tick++;

            float m = P(16);
            float cw = W - m * 2;

            float y = m;
            Label(g, "SYSTEM MONITOR", F(11, "semibold"), pal.Fg2, m, y, cw, StringAlignment.Near);

            bool stressed = cpuPct > 90 || ramPct > 90;
            Color stClr = stressed ? pal.Orange : pal.Green;
            string stTxt = stressed ? "High usage" : "All healthy";
            Font stF = F(10, "semibold");
            float stW = TextWidth(g, stTxt, stF);
            FillCircle(g, stClr, W - m - stW - P(10), y + P(6), P(3));
            Label(g, stTxt, stF, stClr, W - m, y, cw, StringAlignment.Far);

            float ringR = P(30);
            float ringsY = y + P(26);
            float ringCy = ringsY + ringR;
            float col = cw / 3f;

            float dPct = diskEntries.Length > 0 ? diskEntries[0].Pct : 0;
            DrawRing(g, pal, m + col * 0.5f, ringCy, ringR, cpuPct / 100f, pal.Blue,
                     ((int)Math.Round(cpuPct)) + "%", "CPU");
            DrawRing(g, pal, m + col * 1.5f, ringCy, ringR, ramPct / 100f, pal.Orange,
                     ramPct + "%", "RAM");
            DrawRing(g, pal, m + col * 2.5f, ringCy, ringR, dPct, pal.Purple,
                     ((int)Math.Round(dPct * 100)) + "%", "Disk");

            float hlY = ringsY + ringR * 2 + P(18);
            Label(g, "CPU History", F(11, "regular"), pal.Fg2, m, hlY, cw / 2, StringAlignment.Near);
            Label(g, "60s", F(11, "regular"), pal.Fg2, W - m, hlY, cw / 2, StringAlignment.Far);

            float chY = hlY + P(16);
            float chH = P(36);
            DrawChart(g, pal, m, chY, cw, chH);

            float ry = chY + chH + P(8);
            float rh = P(26);

            for (int i = 0; i < diskEntries.Length && i < 3; i++)
            {
                DrawSep(g, pal.Sep, m, ry);
                DrawDiskRow(g, pal, m, ry, cw, diskEntries[i]);
                ry += rh;
            }

            DrawSep(g, pal.Sep, m, ry);
            DrawNetRow(g, pal, m, ry, cw);
            ry += rh;

            if (hasBat)
            {
                DrawSep(g, pal.Sep, m, ry);
                DrawBatRow(g, pal, m, ry, cw);
                ry += rh;
            }

            DrawSep(g, pal.Sep, m, ry);
            DrawUptimeRow(g, pal, m, ry, cw);
        }

        void DrawRing(Graphics g, Palette pal, float cx, float cy, float r, float pct, Color clr,
                      string val, string lbl)
        {
            pct = Math.Max(0, Math.Min(1, pct));
            float sw = P(7);
            float ar = r - sw / 2 - P(2);

            using (var bg = new Pen(pal.Fill, sw))
                g.DrawEllipse(bg, cx - ar, cy - ar, ar * 2, ar * 2);
            if (pct > 0.005f)
                using (var fg = new Pen(clr, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(fg, cx - ar, cy - ar, ar * 2, ar * 2, -90, pct * 360);

            Font vf = F(16, "bold");
            Label(g, val, vf, pal.Fg, cx, cy - vf.GetHeight() / 2, r * 2, StringAlignment.Center);
            Label(g, lbl, F(10, "regular"), pal.Fg2, cx, cy + r, r * 2, StringAlignment.Center);
        }

        void DrawChart(Graphics g, Palette pal, float x, float y, float w, float h)
        {
            if (histCount < 2) return;
            var pts = new PointF[histCount];
            for (int i = 0; i < histCount; i++)
            {
                int idx = (histHead - histCount + i + HistLen) % HistLen;
                float v = Math.Max(0, Math.Min(100, cpuHist[idx]));
                pts[i] = new PointF(x + (float)i / (HistLen - 1) * w, y + h - v / 100f * h);
            }
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, y + h, pts[0].X, y + h);
                path.CloseFigure();
                using (var fill = new LinearGradientBrush(new PointF(x, y), new PointF(x, y + h),
                    Color.FromArgb(50, pal.Blue), Color.FromArgb(0, pal.Blue)))
                    g.FillPath(fill, path);
            }
            using (var pen = new Pen(pal.Blue, P(1.5f)) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
        }

        void DrawSep(Graphics g, Color sep, float x, float y)
        {
            using (var p = new Pen(sep, 1))
                g.DrawLine(p, x, y, W - x, y);
        }

        void DrawDiskRow(Graphics g, Palette pal, float x, float y, float rw, DiskEntry d)
        {
            float ty = y + P(7);
            Label(g, d.Label, F(12, "regular"), pal.Fg, x, ty, P(55), StringAlignment.Near);
            Color bc = d.Pct > 0.9f ? pal.Red : d.Pct > 0.75f ? pal.Orange : pal.Blue;
            Bar(g, pal.Fill, bc, x + P(65), ty + P(5), P(80), P(4), d.Pct);
            Label(g, Fmt(d.UsedGB) + " / " + Fmt(d.TotalGB), F(12, "regular"), pal.Fg2,
                  x + rw, ty, P(130), StringAlignment.Far);
        }

        void DrawNetRow(Graphics g, Palette pal, float x, float y, float rw)
        {
            float ty = y + P(7);
            Label(g, "Network", F(12, "regular"), pal.Fg, x, ty, P(60), StringAlignment.Near);
            Label(g, "▼ " + Bps(netDown) + "   ▲ " + Bps(netUp),
                  F(12, "regular"), pal.Fg2, x + rw, ty, P(220), StringAlignment.Far);
        }

        void DrawBatRow(Graphics g, Palette pal, float x, float y, float rw)
        {
            float ty = y + P(7);
            Label(g, "Battery", F(12, "regular"), pal.Fg, x, ty, P(60), StringAlignment.Near);
            Bar(g, pal.Fill, batPct <= 20 ? pal.Red : pal.Green, x + P(65), ty + P(5), P(80), P(4), batPct / 100f);
            Label(g, batPct + "%", F(12, "regular"), pal.Fg2, x + rw, ty, P(60), StringAlignment.Far);
        }

        void DrawUptimeRow(Graphics g, Palette pal, float x, float y, float rw)
        {
            float ty = y + P(7);
            Label(g, "Uptime", F(12, "regular"), pal.Fg, x, ty, P(60), StringAlignment.Near);
            long sec = (long)(uptimeMs / 1000);
            long d = sec / 86400, h = (sec % 86400) / 3600, min = (sec % 3600) / 60;
            string t = d > 0 ? d + "d " + h + "h " + min + "m"
                     : h > 0 ? h + "h " + min + "m" : min + "m";
            Label(g, t, F(12, "regular"), pal.Fg2, x + rw, ty, P(120), StringAlignment.Far);
        }

        void Bar(Graphics g, Color bg, Color fg, float x, float y, float w, float h, float pct)
        {
            FillRound(g, bg, new RectangleF(x, y, w, h), P(2));
            float fw = Math.Max(0, Math.Min(w, w * pct));
            if (fw > P(1))
                FillRound(g, fg, new RectangleF(x, y, fw, h), P(2));
        }

        static string Fmt(float gb)
        {
            if (gb >= 1024) return (gb / 1024).ToString("0.#") + " TB";
            return ((int)Math.Round(gb)) + " GB";
        }

        static string Bps(float b)
        {
            if (b >= 1073741824) return (b / 1073741824).ToString("0.0") + " GB/s";
            if (b >= 1048576) return (b / 1048576).ToString("0.0") + " MB/s";
            if (b >= 1024) return (b / 1024).ToString("0.0") + " KB/s";
            return ((int)b) + " B/s";
        }
    }
}
