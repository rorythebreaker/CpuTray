// CpuTray — иконка в трее: ЛКМ показывает температуру/загрузку CPU и RAM,
// ПКМ — меню режимов «Тихий» / «Обычный» (лимиты мощности через RyzenAdj + отключение Turbo Boost).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CpuTray
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint Low; public uint High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    }

    static class RyzenAdjApi
    {
        const string L = "libryzenadj.dll";
        const CallingConvention C = CallingConvention.Cdecl;
        [DllImport(L, CallingConvention = C)] public static extern IntPtr init_ryzenadj();
        [DllImport(L, CallingConvention = C)] public static extern void cleanup_ryzenadj(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern int init_table(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern int refresh_table(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern int set_stapm_limit(IntPtr ry, uint mw);
        [DllImport(L, CallingConvention = C)] public static extern int set_fast_limit(IntPtr ry, uint mw);
        [DllImport(L, CallingConvention = C)] public static extern int set_slow_limit(IntPtr ry, uint mw);
        [DllImport(L, CallingConvention = C)] public static extern int set_tctl_temp(IntPtr ry, uint degC);
        [DllImport(L, CallingConvention = C)] public static extern float get_stapm_limit(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern float get_fast_limit(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern float get_slow_limit(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern float get_tctl_temp(IntPtr ry);
        [DllImport(L, CallingConvention = C)] public static extern float get_tctl_temp_value(IntPtr ry);
    }

    /// <summary>Доступ к SMU процессора AMD через libryzenadj (нужны права администратора).</summary>
    class Ryzen : IDisposable
    {
        IntPtr ry;
        bool tableOk;
        public string Error { get; private set; }
        public bool Ok { get { return ry != IntPtr.Zero; } }

        public Ryzen()
        {
            try
            {
                ry = RyzenAdjApi.init_ryzenadj();
                if (ry == IntPtr.Zero)
                {
                    Error = "RyzenAdj не инициализировался: нужен запуск от администратора, " +
                            "либо антивирус заблокировал драйвер WinRing0x64.sys.";
                    return;
                }
                tableOk = RyzenAdjApi.init_table(ry) == 0;
            }
            catch (Exception ex)
            {
                ry = IntPtr.Zero;
                Error = "Не удалось загрузить libryzenadj.dll: " + ex.Message;
            }
        }

        public bool Refresh() { return Ok && tableOk && RyzenAdjApi.refresh_table(ry) == 0; }

        public float Temp { get { return tableOk ? RyzenAdjApi.get_tctl_temp_value(ry) : float.NaN; } }
        public float StapmW { get { return tableOk ? RyzenAdjApi.get_stapm_limit(ry) : float.NaN; } }
        public float FastW { get { return tableOk ? RyzenAdjApi.get_fast_limit(ry) : float.NaN; } }
        public float SlowW { get { return tableOk ? RyzenAdjApi.get_slow_limit(ry) : float.NaN; } }
        public float TctlLimit { get { return tableOk ? RyzenAdjApi.get_tctl_temp(ry) : float.NaN; } }

        /// <summary>Выставляет лимиты (Вт, °C). NaN — не трогать. Возвращает true, если все запросы приняты.</summary>
        public bool Apply(float stapmW, float fastW, float slowW, float tctl)
        {
            if (!Ok) return false;
            bool ok = true;
            if (Good(stapmW)) ok &= RyzenAdjApi.set_stapm_limit(ry, (uint)Math.Round(stapmW * 1000)) == 0;
            if (Good(fastW)) ok &= RyzenAdjApi.set_fast_limit(ry, (uint)Math.Round(fastW * 1000)) == 0;
            if (Good(slowW)) ok &= RyzenAdjApi.set_slow_limit(ry, (uint)Math.Round(slowW * 1000)) == 0;
            if (Good(tctl)) ok &= RyzenAdjApi.set_tctl_temp(ry, (uint)Math.Round(tctl)) == 0;
            return ok;
        }

        public static bool Good(float v) { return !float.IsNaN(v) && !float.IsInfinity(v) && v > 0; }

        public void Dispose()
        {
            if (ry != IntPtr.Zero) { RyzenAdjApi.cleanup_ryzenadj(ry); ry = IntPtr.Zero; }
        }
    }

    /// <summary>Настройка «Режим повышения производительности процессора» (Turbo Boost) текущей схемы питания.</summary>
    static class PowerCfg
    {
        const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        const string PerfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

        public static bool GetBoost(out int ac, out int dc)
        {
            ac = dc = -1;
            string output = Run("/qh SCHEME_CURRENT " + SubProcessor + " " + PerfBoostMode);
            if (output == null) return false;
            var values = new List<int>();
            foreach (string line in output.Split('\n'))
            {
                // Последние две строки вида "...: 0x00000002" — значения от сети и от батареи (язык системы не важен).
                int i = line.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (i < 0 || line.IndexOf(':') < 0 || line.IndexOf(':') > i) continue;
                int v;
                if (int.TryParse(line.Substring(i + 2).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) values.Add(v);
            }
            if (values.Count < 2) return false;
            ac = values[values.Count - 2];
            dc = values[values.Count - 1];
            return true;
        }

        public static void SetBoost(int ac, int dc)
        {
            Run("/setacvalueindex SCHEME_CURRENT " + SubProcessor + " " + PerfBoostMode + " " + ac);
            Run("/setdcvalueindex SCHEME_CURRENT " + SubProcessor + " " + PerfBoostMode + " " + dc);
            Run("/setactive SCHEME_CURRENT");
        }

        static string Run(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 ? o : null;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>Сохраняет исходные настройки, чтобы режим «Обычный» вернул всё как было (даже после сбоя/перезагрузки).</summary>
    class State
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CpuTray");
        static readonly string FilePath = Path.Combine(Dir, "original.ini");

        public int BoostAc = -1, BoostDc = -1;
        public float Stapm = float.NaN, Fast = float.NaN, Slow = float.NaN, Tctl = float.NaN;

        public static bool Exists { get { return File.Exists(FilePath); } }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var ci = CultureInfo.InvariantCulture;
            File.WriteAllLines(FilePath, new[] {
                "boostAc=" + BoostAc, "boostDc=" + BoostDc,
                "stapm=" + Stapm.ToString(ci), "fast=" + Fast.ToString(ci),
                "slow=" + Slow.ToString(ci), "tctl=" + Tctl.ToString(ci)
            });
        }

        public static State Load()
        {
            var s = new State();
            if (!Exists) return s;
            var ci = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                float f; int n;
                float.TryParse(v, NumberStyles.Float, ci, out f);
                int.TryParse(v, NumberStyles.Integer, ci, out n);
                switch (k)
                {
                    case "boostAc": s.BoostAc = n; break;
                    case "boostDc": s.BoostDc = n; break;
                    case "stapm": s.Stapm = f; break;
                    case "fast": s.Fast = f; break;
                    case "slow": s.Slow = f; break;
                    case "tctl": s.Tctl = f; break;
                }
            }
            return s;
        }

        public static void Delete() { try { File.Delete(FilePath); } catch { } }
    }

    class StatsPopup : Form
    {
        readonly Label text;
        public StatsPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(32, 32, 36);
            Padding = new Padding(14, 10, 14, 10);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            text = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f),
                Location = new Point(14, 10)
            };
            Controls.Add(text);
            Deactivate += (s, e) => Hide();
        }

        public void SetText(string s) { text.Text = s; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(80, 80, 90)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        public void ShowNearTray()
        {
            Show();
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Point c = Cursor.Position;
            int x = Math.Min(Math.Max(c.X - Width / 2, wa.Left + 8), wa.Right - Width - 8);
            int y = c.Y > wa.Top + wa.Height / 2 ? wa.Bottom - Height - 8 : wa.Top + 8;
            Location = new Point(x, y);
            Activate();
            Native.SetForegroundWindow(Handle);
        }
    }

    class TrayApp : ApplicationContext
    {
        // Параметры тихого режима для Ryzen 5 7535HS (штатно ~35 Вт STAPM / 45+ Вт кратковременно).
        const float QuietStapmW = 15f, QuietFastW = 20f, QuietSlowW = 15f, QuietTctl = 80f;

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem miQuiet, miNormal;
        readonly StatsPopup popup = new StatsPopup();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly Ryzen ryzen;
        bool quiet;
        ulong prevIdle, prevTotal;
        double cpuLoad;
        float temp = float.NaN;
        int tick;
        string lastWarning;

        public TrayApp(Ryzen ryzen)
        {
            this.ryzen = ryzen;

            miQuiet = new ToolStripMenuItem("Тихий", null, (s, e) => SetQuiet(true));
            miNormal = new ToolStripMenuItem("Обычный", null, (s, e) => SetQuiet(false));
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripLabel("Режим работы") { ForeColor = SystemColors.GrayText });
            menu.Items.Add(miQuiet);
            menu.Items.Add(miNormal);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => ExitThread()));

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "CpuTray" };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (popup.Visible) { popup.Hide(); return; }
                UpdateStats();
                popup.ShowNearTray();
            };

            // Если прошлый запуск завершился в тихом режиме (сбой/выключение) — сначала возвращаем исходные настройки.
            if (State.Exists) RestoreOriginal();
            UpdateMenu();

            SystemEvents.SessionEnding += (s, e) => { if (quiet) SetQuiet(false); };
            SystemEvents.PowerModeChanged += (s, e) => { if (quiet && e.Mode == PowerModes.Resume) ReapplyQuietIfNeeded(true); };

            ReadCpuTimes(out prevIdle, out prevTotal);
            timer.Tick += (s, e) => UpdateStats();
            timer.Start();
            UpdateStats();

            if (!ryzen.Ok)
                tray.ShowBalloonTip(8000, "CpuTray", ryzen.Error + "\nТемпература будет недоступна, тихий режим — только отключением Turbo Boost.", ToolTipIcon.Warning);
        }

        void SetQuiet(bool on)
        {
            if (on == quiet) return;
            if (on)
            {
                // Запоминаем текущие («обычные») значения до изменения.
                var st = new State();
                PowerCfg.GetBoost(out st.BoostAc, out st.BoostDc);
                if (ryzen.Refresh())
                {
                    st.Stapm = ryzen.StapmW; st.Fast = ryzen.FastW; st.Slow = ryzen.SlowW; st.Tctl = ryzen.TctlLimit;
                }
                try { st.Save(); } catch { }

                PowerCfg.SetBoost(0, 0); // 0 = Turbo Boost отключён
                lastWarning = null;
                if (ryzen.Ok && !ryzen.Apply(QuietStapmW, QuietFastW, QuietSlowW, QuietTctl))
                    lastWarning = "Процессор отклонил часть лимитов TDP, работает только отключение Boost.";
                quiet = true;
            }
            else
            {
                RestoreOriginal();
            }
            UpdateMenu();
            UpdateStats();
        }

        void RestoreOriginal()
        {
            State st = State.Load();
            if (st.BoostAc >= 0 && st.BoostDc >= 0) PowerCfg.SetBoost(st.BoostAc, st.BoostDc);
            if (ryzen.Ok) ryzen.Apply(st.Stapm, st.Fast, st.Slow, st.Tctl);
            State.Delete();
            quiet = false;
            lastWarning = null;
        }

        // Прошивка сбрасывает лимиты после сна / смены источника питания — возвращаем тихие значения.
        void ReapplyQuietIfNeeded(bool force)
        {
            if (!quiet || !ryzen.Ok) return;
            float stapm = ryzen.StapmW;
            if (force || (Ryzen.Good(stapm) && Math.Abs(stapm - QuietStapmW) > 1f))
                ryzen.Apply(QuietStapmW, QuietFastW, QuietSlowW, QuietTctl);
        }

        void UpdateMenu()
        {
            miQuiet.Checked = quiet;
            miNormal.Checked = !quiet;
        }

        static void ReadCpuTimes(out ulong idle, out ulong total)
        {
            Native.FILETIME i, k, u;
            Native.GetSystemTimes(out i, out k, out u);
            idle = i.Value;
            total = k.Value + u.Value; // kernel уже включает idle
        }

        void UpdateStats()
        {
            ulong idle, total;
            ReadCpuTimes(out idle, out total);
            ulong dTotal = total - prevTotal, dIdle = idle - prevIdle;
            if (dTotal > 0) cpuLoad = 100.0 * (dTotal - dIdle) / dTotal;
            prevIdle = idle; prevTotal = total;

            var mem = new Native.MEMORYSTATUSEX();
            Native.GlobalMemoryStatusEx(mem);
            double totalGb = mem.ullTotalPhys / 1073741824.0;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

            float stapm = float.NaN;
            if (ryzen.Refresh())
            {
                temp = ryzen.Temp;
                stapm = ryzen.StapmW;
                if (++tick % 20 == 0) ReapplyQuietIfNeeded(false);
            }

            var ci = CultureInfo.GetCultureInfo("ru-RU");
            string tempStr = Ryzen.Good(temp) ? temp.ToString("0", ci) + " °C" : "н/д";
            string info =
                "Температура CPU:   " + tempStr + "\n" +
                "Загрузка CPU:        " + cpuLoad.ToString("0", ci) + " %\n" +
                "RAM:                      " + usedGb.ToString("0.0", ci) + " / " + totalGb.ToString("0.0", ci) +
                " ГБ (" + mem.dwMemoryLoad + " %)\n\n" +
                "Режим: " + (quiet ? "тихий" : "обычный") +
                (Ryzen.Good(stapm) ? "   ·   лимит TDP " + stapm.ToString("0", ci) + " Вт" : "");
            if (!ryzen.Ok) info += "\n\n" + ryzen.Error;
            if (lastWarning != null) info += "\n\n" + lastWarning;
            popup.SetText(info);

            string tip = "CPU " + tempStr + ", " + cpuLoad.ToString("0") + "% · RAM " + mem.dwMemoryLoad + "%" + (quiet ? " · тихий" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            SetIcon(Ryzen.Good(temp) ? ((int)Math.Round(temp)).ToString() : ((int)cpuLoad).ToString());
        }

        void SetIcon(string label)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Color bg = quiet ? Color.FromArgb(46, 160, 67) : Color.FromArgb(0, 120, 215);
                    if (Ryzen.Good(temp) && temp >= 85) bg = Color.FromArgb(215, 50, 40);
                    using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, size, size);
                    float fs = size * (label.Length >= 3 ? 0.45f : 0.62f);
                    using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(label, f, Brushes.White, new RectangleF(0, 1, size, size), sf);
                }
                IntPtr h = bmp.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                if (old != null) old.Dispose();
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            if (quiet) SetQuiet(false); // при выходе всё возвращается как было
            tray.Visible = false;
            tray.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        static readonly string[] NativeFiles = { "libryzenadj.dll", "WinRing0x64.dll", "WinRing0x64.sys", "inpoutx64.dll" };

        /// <summary>Библиотеки RyzenAdj вшиты в exe и распаковываются рядом с ним
        /// (WinRing0 ищет свой драйвер .sys именно в папке exe).</summary>
        static void ExtractNative()
        {
            string bin = Path.GetDirectoryName(Application.ExecutablePath);
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in NativeFiles)
            {
                using (Stream s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) continue;
                    string path = Path.Combine(bin, name);
                    if (File.Exists(path) && new FileInfo(path).Length == s.Length) continue;
                    try { using (var f = File.Create(path)) s.CopyTo(f); } catch { }
                }
            }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "CpuTray_SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { ExtractNative(); } catch { }
                using (var ryzen = new Ryzen())
                    Application.Run(new TrayApp(ryzen));
            }
        }
    }
}
