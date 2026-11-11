using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        public GpuStat Clone() { return (GpuStat)MemberwiseClone(); }

        public uint MaxRpm()
        {
            uint mx = 0;
            if (Rpms != null) foreach (var r in Rpms) if (r > mx) mx = r;
            return mx;
        }
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
        public string RuName;       // для тултипа и панели
        public Color Strip;         // цвет метрики: полоска на значке и график на панели
        public Func<GpuStat, string> Value;        // текст на значке в трее
        public Func<GpuStat, Color> TextColor;
        public Func<GpuStat, string> Big;          // крупное значение на панели
        public Func<GpuStat, string> Unit;         // подпись единиц рядом с ним
        public Func<GpuStat, double> Sample;       // точка графика (NaN — нет данных)
        public Func<GpuStat, double> Hi;           // верх шкалы графика; 0 — автомасштаб
        public double AutoMin;                     // минимальный верх шкалы при автомасштабе
        public Func<GpuStat, double, string> Fmt;  // формат пикового значения

        public static Metric[] CreateAll()
        {
            Func<GpuStat, Color> plain = s => s.Valid ? Color.White : Color.Gray;
            return new Metric[]
            {
                new Metric { Key = "T", RuName = "Температура", Strip = Color.FromArgb(255, 82, 82),
                    Value = s => !s.Valid ? "–" : ((int)s.Temp) + "°",
                    TextColor = s => !s.Valid ? Color.Gray : s.Temp >= 85 ? Color.FromArgb(255, 90, 90)
                                   : s.Temp >= 75 ? Color.FromArgb(255, 180, 80) : Color.White,
                    Big = s => !s.Valid ? "–" : ((int)s.Temp).ToString(),
                    Unit = s => s.Valid ? "°C" : "",
                    Sample = s => s.Valid ? s.Temp : double.NaN,
                    Hi = s => 100,
                    Fmt = (s, v) => ((int)v) + "°" },
                new Metric { Key = "G", RuName = "Загрузка GPU", Strip = Color.FromArgb(68, 214, 44),
                    Value = s => !s.Valid ? "–" : ((int)s.Util) + "%",
                    TextColor = plain,
                    Big = s => !s.Valid ? "–" : ((int)s.Util).ToString(),
                    Unit = s => s.Valid ? "%" : "",
                    Sample = s => s.Valid ? s.Util : double.NaN,
                    Hi = s => 100,
                    Fmt = (s, v) => ((int)v) + "%" },
                new Metric { Key = "M", RuName = "VRAM", Strip = Color.FromArgb(176, 96, 255),
                    Value = s => !s.Valid || s.MemTotal <= 0 ? "–" : ((int)Math.Round(s.MemUsed * 100.0 / s.MemTotal)) + "%",
                    TextColor = plain,
                    Big = s => !s.Valid ? "–" : (s.MemUsed / 1024).ToString("0.0"),
                    Unit = s => s.Valid ? "/ " + (s.MemTotal / 1024).ToString("0.0") + " ГБ" : "",
                    Sample = s => s.Valid && s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : double.NaN,
                    Hi = s => 100,
                    Fmt = (s, v) => ((int)Math.Round(v)) + "%" },
                new Metric { Key = "P", RuName = "Мощность", Strip = Color.FromArgb(255, 184, 0),
                    Value = s => !s.Valid || !s.PowerOk ? "–" : ((int)Math.Round(s.Power)).ToString(),
                    TextColor = plain,
                    Big = s => !s.Valid || !s.PowerOk ? "–" : ((int)Math.Round(s.Power)).ToString(),
                    Unit = s => s.Valid && s.PowerOk ? "Вт" : "",
                    Sample = s => s.Valid && s.PowerOk ? s.Power : double.NaN,
                    Hi = s => 0, AutoMin = 100,
                    Fmt = (s, v) => ((int)Math.Round(v)) + " Вт" },
                new Metric { Key = "F", RuName = "Вентиляторы", Strip = Color.FromArgb(0, 212, 255),
                    Value = s => FanIconValue(s),
                    TextColor = s => (s.Rpms != null || s.Valid) ? Color.White : Color.Gray,
                    Big = s => HasRpm(s) ? s.MaxRpm().ToString() : !s.Valid || !s.FanOk ? "–" : ((int)s.Fan).ToString(),
                    Unit = s => HasRpm(s) ? "RPM" : s.Valid && s.FanOk ? "%" : "",
                    Sample = s => HasRpm(s) ? s.MaxRpm() : s.Valid && s.FanOk ? s.Fan : double.NaN,
                    Hi = s => HasRpm(s) ? 0 : 100, AutoMin = 1000,
                    Fmt = (s, v) => HasRpm(s) ? ((int)v).ToString() : ((int)v) + "%" },
            };
        }

        static bool HasRpm(GpuStat s) { return s.Rpms != null && s.Rpms.Length > 0; }

        // На значке — максимальные обороты по вентиляторам карты (реальный тахометр).
        // Если NvAPI недоступен, откатываемся на % из nvidia-smi.
        static string FanIconValue(GpuStat s)
        {
            if (HasRpm(s))
            {
                uint mx = s.MaxRpm();
                return mx >= 10000 ? "10k+" : mx.ToString();
            }
            return !s.Valid || !s.FanOk ? "–" : ((int)s.Fan) + "%";
        }
    }

    // Кольцевой буфер истории одной метрики для графика
    class History
    {
        public const int Cap = 121; // 120 интервалов по 0,5 с = окно в 60 секунд
        readonly double[] buf = new double[Cap];
        int head;
        public long Total;

        public History() { for (int i = 0; i < Cap; i++) buf[i] = double.NaN; }

        public void Add(double v) { buf[head] = v; head = (head + 1) % Cap; Total++; }

        // 0 — самая старая точка, Cap-1 — самая новая
        public double At(int i) { return buf[(head + i) % Cap]; }
    }

    static class Theme
    {
        public static readonly string[] AccentNames = { "Зелёный", "Красный", "Голубой" };
        static readonly Color[] Accents =
        {
            Color.FromArgb(68, 214, 44),
            Color.FromArgb(255, 45, 66),
            Color.FromArgb(0, 224, 255),
        };
        const string Key = @"Software\GpuTrayMonitor";

        public static int AccentIndex;
        public static Color Accent { get { return Accents[AccentIndex]; } }

        public static readonly Color Back = Color.FromArgb(10, 10, 12);
        public static readonly Color Card = Color.FromArgb(17, 17, 21);
        public static readonly Color Tile = Color.FromArgb(23, 23, 28);
        public static readonly Color Line = Color.FromArgb(42, 42, 50);
        public static readonly Color Dim = Color.FromArgb(138, 138, 150);

        public static void Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return;
                    int v = Convert.ToInt32(k.GetValue("Accent", 0));
                    if (v >= 0 && v < Accents.Length) AccentIndex = v;
                }
            }
            catch { }
        }

        public static void SetAccent(int index)
        {
            AccentIndex = index;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue("Accent", index, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    // Отрисовка панели: тёмная «геймерская» тема, плитки метрик с живыми графиками
    class PanelRenderer : IDisposable
    {
        public const int TitleH = 46;
        const int Pad = 14, CardPad = 12, CardHdr = 36, CardGap = 10, TileW = 164, TileGap = 8, Cut = 12;

        readonly Metric[] metrics;
        int tileH = 128;
        public GpuStat[] Snap = new GpuStat[0];
        public History[][] Hist = new History[0][];
        public bool HoverClose;
        public bool Blink;

        readonly Font fTitle = MakeFont(17f, FontStyle.Bold);
        readonly Font fTag = MakeFont(12f, FontStyle.Bold);
        readonly Font fName = MakeFont(13f, FontStyle.Bold);
        readonly Font fLabel = MakeFont(10.5f, FontStyle.Bold);
        readonly Font fSmall = MakeFont(10.5f, FontStyle.Regular);
        readonly Font fValue = MakeFont(27f, FontStyle.Bold);
        readonly Font fUnit = MakeFont(12f, FontStyle.Regular);
        readonly StringFormat sf;

        public PanelRenderer(Metric[] metrics)
        {
            this.metrics = metrics;
            sf = new StringFormat(StringFormat.GenericTypographic);
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
        }

        static Font MakeFont(float px, FontStyle style)
        {
            try { return new Font("Segoe UI", px, style, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
        }

        public void Dispose()
        {
            fTitle.Dispose(); fTag.Dispose(); fName.Dispose(); fLabel.Dispose();
            fSmall.Dispose(); fValue.Dispose(); fUnit.Dispose(); sf.Dispose();
        }

        int CardH { get { return CardHdr + tileH + CardPad; } }

        // Размер панели; при большом числе карт плитки сжимаются, чтобы окно влезло в экран
        public Size Measure(int gpus, int maxHeight)
        {
            if (gpus < 1) gpus = 1;
            int fixedH = TitleH + 2 * Pad + (gpus - 1) * CardGap + gpus * (CardHdr + CardPad);
            tileH = Math.Max(84, Math.Min(128, (maxHeight - fixedH) / gpus));
            int w = 2 * Pad + 2 * CardPad + metrics.Length * TileW + (metrics.Length - 1) * TileGap;
            return new Size(w, fixedH + gpus * tileH);
        }

        public Rectangle CloseRect(int width) { return new Rectangle(width - Pad - 26, 10, 26, 26); }

        float Text(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, sf);
            return g.MeasureString(s, f, 10000, sf).Width;
        }

        void TextRight(Graphics g, string s, Font f, Color c, float right, float y)
        {
            float w = g.MeasureString(s, f, 10000, sf).Width;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, right - w, y, sf);
        }

        public void Render(Graphics g, int width, int height)
        {
            Color accent = Theme.Accent;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Theme.Back);

            // --- Заголовок: косые штрихи, название, индикатор LIVE, кнопка закрытия
            using (var b = new SolidBrush(accent))
                for (int i = 0; i < 3; i++)
                {
                    float x = Pad + i * 9;
                    g.FillPolygon(b, new[] { new PointF(x + 7, 13), new PointF(x + 12, 13), new PointF(x + 5, 33), new PointF(x, 33) });
                }
            float tx = Pad + 36;
            tx += Text(g, "GPU", fTitle, accent, tx, 11);
            Text(g, " MONITOR", fTitle, Color.White, tx, 11);

            bool live = false;
            foreach (var s in Snap) if (s.Valid) live = true;
            var close = CloseRect(width);
            string status = live ? "LIVE" : "НЕТ ДАННЫХ";
            Color statusColor = live ? accent : Color.FromArgb(255, 180, 80);
            float sw = g.MeasureString(status, fLabel, 10000, sf).Width;
            float sx = close.Left - 16 - sw;
            Text(g, status, fLabel, statusColor, sx, 16);
            using (var b = new SolidBrush(Color.FromArgb(live && Blink ? 255 : 90, statusColor)))
                g.FillEllipse(b, sx - 13, 19, 7, 7);
            TextRight(g, "ОКНО 60 С", fSmall, Theme.Dim, sx - 28, 16);

            if (HoverClose)
                using (var b = new SolidBrush(Color.FromArgb(200, 40, 50))) g.FillRectangle(b, close);
            using (var p = new Pen(HoverClose ? Color.White : Theme.Dim, 1.6f))
            {
                g.DrawLine(p, close.Left + 8, close.Top + 8, close.Right - 8, close.Bottom - 8);
                g.DrawLine(p, close.Right - 8, close.Top + 8, close.Left + 8, close.Bottom - 8);
            }

            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 0, TitleH - 0.5f, width, TitleH - 0.5f);
            using (var b = new SolidBrush(accent))
                g.FillPolygon(b, new[] { new PointF(0, TitleH - 3), new PointF(Pad + 150, TitleH - 3), new PointF(Pad + 147, TitleH), new PointF(0, TitleH) });

            // --- Карточки видеокарт
            int y = TitleH + Pad;
            for (int i = 0; i < Snap.Length; i++)
            {
                DrawCard(g, new Rectangle(Pad, y, width - 2 * Pad, CardH), i, accent);
                y += CardH + CardGap;
            }

            g.SmoothingMode = SmoothingMode.None;
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, width - 1, height - 1);
            using (var p = new Pen(accent)) g.DrawLine(p, 0, 0, width - 1, 0);
        }

        void DrawCard(Graphics g, Rectangle r, int index, Color accent)
        {
            var s = Snap[index];
            // Корпус со срезанными углами
            var shape = new[]
            {
                new PointF(r.Left, r.Top), new PointF(r.Right - Cut, r.Top), new PointF(r.Right, r.Top + Cut),
                new PointF(r.Right, r.Bottom), new PointF(r.Left + Cut, r.Bottom), new PointF(r.Left, r.Bottom - Cut)
            };
            using (var b = new SolidBrush(Theme.Card)) g.FillPolygon(b, shape);
            using (var p = new Pen(Theme.Line)) g.DrawPolygon(p, shape);
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, r.Left, r.Top, 3, r.Height - Cut);

            // Шапка: плашка «GPU N» и название карты
            string tag = "GPU " + index;
            float tw = g.MeasureString(tag, fTag, 10000, sf).Width;
            float tx = r.Left + CardPad + 4, ty = r.Top + 9;
            using (var b = new SolidBrush(accent))
                g.FillPolygon(b, new[] { new PointF(tx + 6, ty), new PointF(tx + tw + 22, ty), new PointF(tx + tw + 16, ty + 20), new PointF(tx, ty + 20) });
            Text(g, tag, fTag, Color.Black, tx + 11, ty + 2.5f);
            string name = string.IsNullOrEmpty(s.FullName) ? s.Name : s.FullName;
            Text(g, name.ToUpperInvariant(), fName, Color.White, tx + tw + 32, ty + 2);

            for (int m = 0; m < metrics.Length; m++)
            {
                var tile = new Rectangle(r.Left + CardPad + m * (TileW + TileGap), r.Top + CardHdr, TileW, tileH);
                DrawTile(g, tile, metrics[m], s, Hist[index][m]);
            }
        }

        void DrawTile(Graphics g, Rectangle r, Metric m, GpuStat s, History h)
        {
            using (var b = new SolidBrush(Theme.Tile)) g.FillRectangle(b, r);
            using (var b = new SolidBrush(m.Strip)) g.FillRectangle(b, r.Left, r.Top, r.Width, 2);

            // Шкала и пик за окно
            double peak = double.NaN;
            for (int i = 0; i < History.Cap; i++)
            {
                double v = h.At(i);
                if (!double.IsNaN(v) && (double.IsNaN(peak) || v > peak)) peak = v;
            }
            double hi = m.Hi(s);
            if (hi <= 0) hi = NiceCeil(Math.Max(m.AutoMin, double.IsNaN(peak) ? 0 : peak * 1.1));

            Text(g, m.RuName.ToUpper(), fLabel, Theme.Dim, r.Left + 9, r.Top + 9);
            if (!double.IsNaN(peak))
                TextRight(g, "ПИК " + m.Fmt(s, peak), fSmall, Theme.Dim, r.Right - 9, r.Top + 9);

            float vw = Text(g, m.Big(s), fValue, m.TextColor(s), r.Left + 8, r.Top + 23);
            Text(g, m.Unit(s), fUnit, Theme.Dim, r.Left + 8 + vw + 5, r.Top + 39);

            DrawGraph(g, new RectangleF(r.Left + 8, r.Top + 62, r.Width - 16, r.Height - 70), h, hi, m.Strip);
        }

        static double NiceCeil(double v)
        {
            double[] steps = { 1, 1.5, 2, 3, 4, 5, 6, 8, 10 };
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var st in steps) if (st * mag >= v) return st * mag;
            return 10 * mag;
        }

        void DrawGraph(Graphics g, RectangleF r, History h, double hi, Color c)
        {
            var state = g.Save();
            g.SetClip(r);
            float right = r.Right - 5;                       // запас справа под маркер текущей точки
            float step = (right - r.Left) / (History.Cap - 1);

            // Сетка: горизонтали по четвертям шкалы, вертикали каждые 15 с — едут вместе с графиком
            g.SmoothingMode = SmoothingMode.None;
            using (var p = new Pen(Color.FromArgb(16, 255, 255, 255)))
            {
                for (int i = 1; i < 4; i++)
                {
                    float gy = (float)Math.Round(r.Top + r.Height * i / 4f);
                    g.DrawLine(p, r.Left, gy, r.Right, gy);
                }
                for (float gx = right - ((h.Total + 29) % 30) * step; gx > r.Left; gx -= 30 * step)
                    g.DrawLine(p, (float)Math.Round(gx), r.Top, (float)Math.Round(gx), r.Bottom);
            }
            using (var p = new Pen(Color.FromArgb(40, 255, 255, 255)))
                g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var run = new List<PointF>();
            PointF last = PointF.Empty;
            bool hasLast = false;
            for (int i = 0; i <= History.Cap; i++)
            {
                double v = i < History.Cap ? h.At(i) : double.NaN;
                if (double.IsNaN(v))
                {
                    if (run.Count > 0) DrawRun(g, r, run, c);
                    run.Clear();
                    continue;
                }
                double t = Math.Max(0, Math.Min(1, v / hi));
                var pt = new PointF(right - (History.Cap - 1 - i) * step, (float)(r.Bottom - 2 - t * (r.Height - 6)));
                run.Add(pt);
                hasLast = i == History.Cap - 1;
                last = pt;
            }

            // Светящийся маркер текущего значения
            if (hasLast)
            {
                using (var b = new SolidBrush(Color.FromArgb(70, c))) g.FillEllipse(b, last.X - 5, last.Y - 5, 10, 10);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, last.X - 2, last.Y - 2, 4, 4);
            }
            g.Restore(state);
        }

        static void DrawRun(Graphics g, RectangleF r, List<PointF> run, Color c)
        {
            if (run.Count == 1) run.Add(new PointF(run[0].X + 0.5f, run[0].Y));
            var pts = run.ToArray();
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, r.Bottom, pts[0].X, r.Bottom);
                path.CloseFigure();
                var gr = new RectangleF(r.Left, r.Top - 1, r.Width, r.Height + 2);
                using (var b = new LinearGradientBrush(gr, Color.FromArgb(120, c), Color.FromArgb(0, c), LinearGradientMode.Vertical))
                    g.FillPath(b, path);
            }
            using (var p = new Pen(Color.FromArgb(45, c), 5f) { LineJoin = LineJoin.Round })
                g.DrawLines(p, pts);
            using (var p = new Pen(c, 1.7f) { LineJoin = LineJoin.Round })
                g.DrawLines(p, pts);
        }
    }

    // Окно панели: без рамки, перетаскивается за заголовок, закрывается крестиком или Esc
    class MonitorPanel : Form
    {
        public readonly PanelRenderer Renderer;
        public bool AllowClose;
        bool dragging;
        Point dragOffset;

        public MonitorPanel(Metric[] metrics)
        {
            Renderer = new PanelRenderer(metrics);
            Text = "GPU Monitor";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Back;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW — не показывать в Alt+Tab
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Renderer.Render(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < PanelRenderer.TitleH
                && !Renderer.CloseRect(ClientSize.Width).Contains(e.Location))
            {
                dragging = true;
                dragOffset = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                var p = Cursor.Position;
                Location = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
            }
            SetHover(Renderer.CloseRect(ClientSize.Width).Contains(e.Location));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasDragging = dragging;
            dragging = false;
            if (!wasDragging && e.Button == MouseButtons.Left
                && Renderer.CloseRect(ClientSize.Width).Contains(e.Location))
                Hide();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e) { SetHover(false); base.OnMouseLeave(e); }

        void SetHover(bool on)
        {
            if (Renderer.HoverClose == on) return;
            Renderer.HoverClose = on;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Renderer.Dispose();
            base.Dispose(disposing);
        }
    }

    // Тёмное контекстное меню в тон панели
    class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(20, 20, 24);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color MenuItemBorder { get { return Theme.Accent; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(36, 36, 44); } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Bg; } }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.Gainsboro : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.Gainsboro;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Accent, 2f))
                e.Graphics.DrawLines(p, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
        }
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
        MonitorPanel panel;
        History[][] history = new History[0][];
        string[] iconKeys; // что сейчас нарисовано на каждом значке — чтобы не пересоздавать без изменений
        bool blink;
        Process smiProc;
        volatile bool exiting;

        // Фон значка различает карты; при большем числе GPU палитра идёт по кругу
        static readonly Color[] GpuColors =
        {
            Color.FromArgb(18, 52, 86),   // GPU 0 — тёмно-синий
            Color.FromArgb(26, 60, 40),   // GPU 1 — тёмно-зелёный
            Color.FromArgb(70, 40, 80),   // GPU 2 — тёмно-фиолетовый
            Color.FromArgb(80, 45, 25),   // GPU 3 — тёмно-коричневый
        };

        readonly Metric[] metrics;

        public TrayApp()
        {
            Theme.Load();
            metrics = Metric.CreateAll();

            DetectGpus();
            BuildPanel();
            BuildIcons();
            StartReader();

            iconKeys = new string[icons.Count];
            timer = new System.Windows.Forms.Timer { Interval = 500 };
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
            history = new History[stats.Length][];
            for (int g = 0; g < stats.Length; g++)
            {
                history[g] = new History[metrics.Length];
                for (int m = 0; m < metrics.Length; m++) history[g][m] = new History();
            }
            var fullNames = new string[stats.Length];
            for (int i = 0; i < stats.Length; i++) fullNames[i] = stats[i].FullName;
            NvFan.Init(fullNames);
        }

        static string ShortName(string full)
        {
            return full.Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();
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
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ForeColor = Color.Gainsboro, ShowImageMargin = true };
            var showItem = new ToolStripMenuItem("Показать панель");
            showItem.Click += (s, e) => TogglePanel();
            var autoItem = new ToolStripMenuItem("Запускать при входе в Windows") { CheckOnClick = true };
            autoItem.Checked = IsAutoStart();
            autoItem.CheckedChanged += (s, e) => SetAutoStart(autoItem.Checked);
            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) => ExitApp();
            var accentItem = new ToolStripMenuItem("Цвет акцента");
            ((ToolStripDropDownMenu)accentItem.DropDown).Renderer = menu.Renderer;
            accentItem.DropDown.ForeColor = menu.ForeColor;
            for (int i = 0; i < Theme.AccentNames.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(Theme.AccentNames[i]) { Checked = i == Theme.AccentIndex };
                item.Click += (s, e) =>
                {
                    Theme.SetAccent(index);
                    foreach (ToolStripMenuItem other in accentItem.DropDownItems)
                        other.Checked = other == item;
                    panel.Invalidate();
                };
                accentItem.DropDownItems.Add(item);
            }
            menu.Items.Add(showItem);
            menu.Items.Add(accentItem);
            menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        void BuildPanel()
        {
            panel = new MonitorPanel(metrics);
            panel.Renderer.Hist = history;
        }

        void TogglePanel()
        {
            if (panel.Visible) { panel.Hide(); return; }
            var wa = Screen.PrimaryScreen.WorkingArea;
            panel.ClientSize = panel.Renderer.Measure(stats.Length, wa.Height - 16);
            panel.Location = new Point(wa.Right - panel.Width - 8, wa.Bottom - panel.Height - 8);
            panel.Show();
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
                        "--format=csv,noheader,nounits -lms 500")
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
                for (int i = 0; i < stats.Length; i++) snapshot[i] = stats[i].Clone();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].Rpms = NvFan.GetRpms(i);
                // История пишется всегда, чтобы при открытии панели графики уже были заполнены
                for (int m = 0; m < metrics.Length; m++)
                    history[i][m].Add(metrics[m].Sample(snapshot[i]));
            }

            for (int i = 0; i < icons.Count; i++)
            {
                int g = iconMap[i].Item1;
                var metric = iconMap[i].Item2;
                if (g >= snapshot.Length) continue;
                var s = snapshot[g];

                string text = metric.Value(s);
                Color color = metric.TextColor(s);
                string tip = s.Name + " · " + metric.RuName + ": " + TipValue(metric, s);
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                string key = text + "|" + color.ToArgb() + "|" + tip;
                if (key == iconKeys[i]) continue;
                iconKeys[i] = key;

                var old = icons[i].Icon;
                icons[i].Icon = RenderIcon(text, color, GpuColors[g % GpuColors.Length], metric.Strip);
                if (old != null) old.Dispose();
                icons[i].Text = tip;
            }

            blink = !blink;
            panel.Renderer.Snap = snapshot;
            panel.Renderer.Blink = blink;
            if (panel.Visible) panel.Invalidate();
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
            if (panel != null) { panel.AllowClose = true; panel.Close(); }
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
