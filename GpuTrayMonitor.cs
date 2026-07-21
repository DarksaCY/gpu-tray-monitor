using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuTrayMonitor
{
    class GpuStat
    {
        public string Name = "GPU";
        public string FullName = "";
        public bool Valid;
        public double Temp, Util, MemUsed, MemTotal, Power, Fan;
        public bool PowerOk, FanOk;
        public uint[] Rpms; // реальные обороты с тахометров (NvAPI), null если недоступно
    }

    // Чтение реальных оборотов вентиляторов через NvAPI (nvidia-smi отдаёт только % duty cycle)
    static class NvFan
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDel([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameDel(IntPtr h, System.Text.StringBuilder name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FanStatusDel(IntPtr h, ref FanCoolersStatus s);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct FanCoolersStatusItem
        {
            public uint CoolerId, CurrentRpm, CurrentMinLevel, CurrentMaxLevel, CurrentLevel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct FanCoolersStatus
        {
            public uint Version;
            public uint Count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public FanCoolersStatusItem[] Items;
        }

        static FanStatusDel fanStatus;
        static IntPtr[] map = new IntPtr[0]; // индекс GPU (как в nvidia-smi) -> handle NvAPI
        public static bool Available;

        static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static void Init(string[] fullNames)
        {
            try
            {
                var init = Get<InitDel>(0x0150E828);
                var enumGpus = Get<EnumDel>(0xE5AC921F);
                var getName = Get<NameDel>(0xCEEE8E9F);
                fanStatus = Get<FanStatusDel>(0x35AED5E8);
                if (init == null || enumGpus == null || fanStatus == null || init() != 0) return;

                var handles = new IntPtr[64];
                int count;
                if (enumGpus(handles, out count) != 0 || count == 0) return;

                var names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    var sb = new System.Text.StringBuilder(64);
                    names[i] = (getName != null && getName(handles[i], sb) == 0) ? sb.ToString() : "";
                }

                // Порядок EnumPhysicalGPUs не обязан совпадать с индексами nvidia-smi — сопоставляем по имени
                map = new IntPtr[fullNames.Length];
                var used = new bool[count];
                for (int g = 0; g < fullNames.Length; g++)
                {
                    int found = -1;
                    for (int i = 0; i < count; i++)
                        if (!used[i] && string.Equals(names[i], fullNames[g], StringComparison.OrdinalIgnoreCase))
                        { found = i; break; }
                    if (found < 0)
                        for (int i = 0; i < count; i++)
                            if (!used[i]) { found = i; break; }
                    if (found >= 0) { used[found] = true; map[g] = handles[found]; }
                }
                Available = true;
            }
            catch { Available = false; }
        }

        public static uint[] GetRpms(int gpuIndex)
        {
            if (!Available || gpuIndex >= map.Length || map[gpuIndex] == IntPtr.Zero) return null;
            try
            {
                var s = new FanCoolersStatus();
                s.Version = (uint)((1 << 16) | Marshal.SizeOf(typeof(FanCoolersStatus)));
                if (fanStatus(map[gpuIndex], ref s) != 0) return null;
                int n = (int)Math.Min(s.Count, 32);
                var rpms = new uint[n];
                for (int i = 0; i < n; i++) rpms[i] = s.Items[i].CurrentRpm;
                return rpms;
            }
            catch { return null; }
        }
    }

    class Metric
    {
        public string Key;          // T, G, M, P, F
        public string RuName;       // для тултипа
        public Color Strip;         // цветовая полоска метрики внизу значка
        public Func<GpuStat, string> Value;
        public Func<GpuStat, Color> TextColor;
    }

    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "GpuTrayMonitor";

        readonly object statsLock = new object();
        GpuStat[] stats = new GpuStat[0];

        readonly List<NotifyIcon> icons = new List<NotifyIcon>();
        readonly List<Tuple<int, Metric>> iconMap = new List<Tuple<int, Metric>>(); // (gpuIndex, metric) для каждого значка
        readonly System.Windows.Forms.Timer timer;
        Form panel;
        Label panelLabel;
        Process smiProc;
        volatile bool exiting;

        static readonly Color BgGpu0 = Color.FromArgb(18, 52, 86);    // тёмно-синий
        static readonly Color BgGpu1 = Color.FromArgb(26, 60, 40);    // тёмно-зелёный

        readonly Metric[] metrics;

        public TrayApp()
        {
            metrics = new Metric[]
            {
                new Metric { Key = "T", RuName = "Температура", Strip = Color.FromArgb(229, 72, 77),
                    Value = s => !s.Valid ? "–" : ((int)s.Temp) + "°",
                    TextColor = s => !s.Valid ? Color.Gray : s.Temp >= 85 ? Color.FromArgb(255, 90, 90)
                                   : s.Temp >= 75 ? Color.FromArgb(255, 180, 80) : Color.White },
                new Metric { Key = "G", RuName = "Загрузка GPU", Strip = Color.FromArgb(70, 167, 88),
                    Value = s => !s.Valid ? "–" : ((int)s.Util) + "%",
                    TextColor = s => s.Valid ? Color.White : Color.Gray },
                new Metric { Key = "M", RuName = "VRAM", Strip = Color.FromArgb(142, 78, 198),
                    Value = s => !s.Valid || s.MemTotal <= 0 ? "–" : ((int)Math.Round(s.MemUsed * 100.0 / s.MemTotal)) + "%",
                    TextColor = s => s.Valid ? Color.White : Color.Gray },
                new Metric { Key = "P", RuName = "Мощность", Strip = Color.FromArgb(245, 166, 35),
                    Value = s => !s.Valid || !s.PowerOk ? "–" : ((int)Math.Round(s.Power)).ToString(),
                    TextColor = s => s.Valid ? Color.White : Color.Gray },
                new Metric { Key = "F", RuName = "Вентиляторы", Strip = Color.FromArgb(0, 181, 216),
                    Value = s => FanIconValue(s),
                    TextColor = s => (s.Rpms != null || s.Valid) ? Color.White : Color.Gray },
            };

            DetectGpus();
            BuildPanel();
            BuildIcons();
            StartReader();

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => Redraw();
            timer.Start();
            Redraw();
        }

        void DetectGpus()
        {
            var list = new List<GpuStat>();
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=index,name --format=csv,noheader")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        var parts = line.Split(new[] { ',' }, 2);
                        if (parts.Length < 2) continue;
                        string full = parts[1].Trim();
                        list.Add(new GpuStat { Name = ShortName(full), FullName = full });
                    }
                    p.WaitForExit(3000);
                }
            }
            catch { }
            if (list.Count == 0) list.Add(new GpuStat { Name = "GPU?" });
            stats = list.ToArray();
            var fullNames = new string[stats.Length];
            for (int i = 0; i < stats.Length; i++) fullNames[i] = stats[i].FullName;
            NvFan.Init(fullNames);
        }

        static string ShortName(string full)
        {
            return full.Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();
        }

        // На значке — максимальные обороты по вентиляторам карты (реальный тахометр).
        // Если NvAPI недоступен, откатываемся на % из nvidia-smi.
        static string FanIconValue(GpuStat s)
        {
            if (s.Rpms != null && s.Rpms.Length > 0)
            {
                uint mx = 0;
                foreach (var r in s.Rpms) if (r > mx) mx = r;
                return mx >= 10000 ? "10k+" : mx.ToString();
            }
            return !s.Valid || !s.FanOk ? "–" : ((int)s.Fan) + "%";
        }

        static string FanTipValue(GpuStat s)
        {
            if (s.Rpms != null && s.Rpms.Length > 0)
            {
                var parts = new string[s.Rpms.Length];
                for (int i = 0; i < s.Rpms.Length; i++) parts[i] = s.Rpms[i].ToString();
                string duty = s.Valid && s.FanOk ? " (" + (int)s.Fan + " %)" : "";
                return string.Join(" / ", parts) + " RPM" + duty;
            }
            if (!s.Valid) return "нет данных";
            return s.FanOk ? ((int)s.Fan) + " %" : "н/д";
        }

        void BuildIcons()
        {
            var menu = BuildMenu();
            // Создаём в обратном порядке: Windows обычно ставит новые значки левее,
            // так слева направо получится T G M P F для GPU0, затем GPU1.
            for (int g = stats.Length - 1; g >= 0; g--)
            {
                for (int m = metrics.Length - 1; m >= 0; m--)
                {
                    var ni = new NotifyIcon { Visible = true, ContextMenuStrip = menu };
                    ni.DoubleClick += (s, e) => TogglePanel();
                    icons.Add(ni);
                    iconMap.Add(Tuple.Create(g, metrics[m]));
                }
            }
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("Показать панель");
            showItem.Click += (s, e) => TogglePanel();
            var autoItem = new ToolStripMenuItem("Запускать при входе в Windows") { CheckOnClick = true };
            autoItem.Checked = IsAutoStart();
            autoItem.CheckedChanged += (s, e) => SetAutoStart(autoItem.Checked);
            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) => ExitApp();
            menu.Items.Add(showItem);
            menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        void BuildPanel()
        {
            panel = new Form
            {
                Text = "GPU Monitor",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(24, 24, 28),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            panelLabel = new Label
            {
                AutoSize = true,
                Font = new Font("Consolas", 11f),
                ForeColor = Color.Gainsboro,
                Padding = new Padding(12),
                Text = "..."
            };
            panel.Controls.Add(panelLabel);
            panel.FormClosing += (s, e) =>
            {
                if (!exiting) { e.Cancel = true; panel.Hide(); }
            };
        }

        void TogglePanel()
        {
            if (panel.Visible) { panel.Hide(); return; }
            var wa = Screen.PrimaryScreen.WorkingArea;
            panel.Show();
            panel.Location = new Point(wa.Right - panel.Width - 8, wa.Bottom - panel.Height - 8);
            panel.Activate();
        }

        void StartReader()
        {
            var t = new Thread(ReaderLoop) { IsBackground = true };
            t.Start();
        }

        void ReaderLoop()
        {
            while (!exiting)
            {
                try
                {
                    var psi = new ProcessStartInfo("nvidia-smi",
                        "--query-gpu=index,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw,fan.speed " +
                        "--format=csv,noheader,nounits -l 1")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    smiProc = Process.Start(psi);
                    string line;
                    while (!exiting && (line = smiProc.StandardOutput.ReadLine()) != null)
                        ParseLine(line);
                }
                catch { }
                if (exiting) break;
                lock (statsLock) foreach (var s in stats) s.Valid = false;
                Thread.Sleep(3000); // nvidia-smi упал — пробуем снова
            }
        }

        void ParseLine(string line)
        {
            var parts = line.Split(',');
            if (parts.Length < 7) return;
            int idx;
            if (!int.TryParse(parts[0].Trim(), out idx)) return;
            lock (statsLock)
            {
                if (idx < 0 || idx >= stats.Length) return;
                var s = stats[idx];
                double v;
                s.Temp = TryNum(parts[1], out v) ? v : s.Temp;
                s.Util = TryNum(parts[2], out v) ? v : s.Util;
                s.MemUsed = TryNum(parts[3], out v) ? v : s.MemUsed;
                s.MemTotal = TryNum(parts[4], out v) ? v : s.MemTotal;
                s.PowerOk = TryNum(parts[5], out v); if (s.PowerOk) s.Power = v;
                s.FanOk = TryNum(parts[6], out v); if (s.FanOk) s.Fan = v;
                s.Valid = true;
            }
        }

        static bool TryNum(string raw, out double v)
        {
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        void Redraw()
        {
            GpuStat[] snapshot;
            lock (statsLock)
            {
                snapshot = new GpuStat[stats.Length];
                for (int i = 0; i < stats.Length; i++)
                    snapshot[i] = new GpuStat
                    {
                        Name = stats[i].Name, Valid = stats[i].Valid,
                        Temp = stats[i].Temp, Util = stats[i].Util,
                        MemUsed = stats[i].MemUsed, MemTotal = stats[i].MemTotal,
                        Power = stats[i].Power, Fan = stats[i].Fan,
                        PowerOk = stats[i].PowerOk, FanOk = stats[i].FanOk
                    };
            }
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i].Rpms = NvFan.GetRpms(i);

            for (int i = 0; i < icons.Count; i++)
            {
                int g = iconMap[i].Item1;
                var metric = iconMap[i].Item2;
                if (g >= snapshot.Length) continue;
                var s = snapshot[g];

                var old = icons[i].Icon;
                icons[i].Icon = RenderIcon(metric.Value(s), metric.TextColor(s),
                    g == 0 ? BgGpu0 : BgGpu1, metric.Strip);
                if (old != null) old.Dispose();

                string tip = s.Name + " · " + metric.RuName + ": " + TipValue(metric, s);
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                icons[i].Text = tip;
            }

            if (panel != null && panel.Visible)
                panelLabel.Text = PanelText(snapshot);
        }

        static string TipValue(Metric m, GpuStat s)
        {
            if (m.Key == "F") return FanTipValue(s);
            if (!s.Valid) return "нет данных";
            switch (m.Key)
            {
                case "T": return ((int)s.Temp) + " °C";
                case "G": return ((int)s.Util) + " %";
                case "M": return (s.MemUsed / 1024).ToString("0.0") + " / " + (s.MemTotal / 1024).ToString("0.0") + " ГБ";
                case "P": return s.PowerOk ? ((int)Math.Round(s.Power)) + " Вт" : "н/д";
            }
            return "";
        }

        string PanelText(GpuStat[] snap)
        {
            var cols = new List<string[]>();
            cols.Add(new[] { "", "Температура", "Загрузка GPU", "VRAM", "Мощность", "Вентиляторы" });
            foreach (var s in snap)
            {
                if (!s.Valid && s.Rpms == null) { cols.Add(new[] { s.Name, "–", "–", "–", "–", "–" }); continue; }
                cols.Add(new[]
                {
                    s.Name,
                    s.Valid ? ((int)s.Temp) + " °C" : "–",
                    s.Valid ? ((int)s.Util) + " %" : "–",
                    s.Valid ? (s.MemUsed / 1024).ToString("0.0") + "/" + (s.MemTotal / 1024).ToString("0.0") + " ГБ" : "–",
                    s.Valid && s.PowerOk ? ((int)Math.Round(s.Power)) + " Вт" : "н/д",
                    FanTipValue(s)
                });
            }
            var widths = new int[cols.Count];
            for (int c = 0; c < cols.Count; c++)
                foreach (var cell in cols[c]) widths[c] = Math.Max(widths[c], cell.Length);
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < 6; r++)
            {
                for (int c = 0; c < cols.Count; c++)
                    sb.Append(cols[c][r].PadRight(widths[c] + 3));
                if (r < 5) sb.AppendLine();
            }
            return sb.ToString();
        }

        Icon RenderIcon(string text, Color textColor, Color bg, Color strip)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var gr = Graphics.FromImage(bmp))
            {
                gr.Clear(bg);
                using (var b = new SolidBrush(strip))
                    gr.FillRectangle(b, 0, 27, 32, 5);
                gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float size = text.Length <= 2 ? 19f : text.Length == 3 ? 16f : 12f;
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(textColor))
                {
                    var sz = gr.MeasureString(text, f);
                    gr.DrawString(text, f, b, (32 - sz.Width) / 2f, (27 - sz.Height) / 2f);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }

        bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
        }

        void ExitApp()
        {
            exiting = true;
            timer.Stop();
            try { if (smiProc != null && !smiProc.HasExited) smiProc.Kill(); } catch { }
            foreach (var ni in icons) { ni.Visible = false; ni.Dispose(); }
            if (panel != null) panel.Close();
            ExitThread();
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "GpuTrayMonitor_SingleInstance", out created))
            {
                if (!created) return; // уже запущено
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
