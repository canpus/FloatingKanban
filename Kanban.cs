// Kanban.cs — 悬浮任务看板（纯代码 WPF，无 XAML；兼容系统自带 csc / C# 5）
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Web.Script.Serialization;

// 版本信息（写入 exe 属性，降低杀软误报概率）
[assembly: System.Reflection.AssemblyTitle("Floating Kanban")]
[assembly: System.Reflection.AssemblyDescription("Floating desktop kanban task board widget")]
[assembly: System.Reflection.AssemblyCompany("canpus")]
[assembly: System.Reflection.AssemblyProduct("Floating Kanban")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (C) 2026 canpus")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace FloatingKanban
{
    // ======================================================================
    // 数据
    // ======================================================================

    public class TaskItem
    {
        public string id { get; set; }
        public string title { get; set; }
        public int status { get; set; }          // 0 待办 1 进行中 2 已完成
        public string createdAt { get; set; }    // 录入时间
        public string completedAt { get; set; }  // 最近一次变为已完成的时间
    }

    public class SaveData
    {
        public int v { get; set; }               // 数据版本：0/缺省=旧版，2=当前
        public List<TaskItem> tasks { get; set; }
        public List<TaskItem> archive { get; set; }   // 已归档的完成记录（移出看板但保留，供导出）
        // 旧版字段（读取兼容）
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        // 展开状态的卡片位置与尺寸（屏幕 DIP）
        public double ex { get; set; }
        public double ey { get; set; }
        public double ew { get; set; }
        public double eh { get; set; }
        // 胶囊位置与贴边
        public double px { get; set; }
        public double py { get; set; }
        public bool hasPill { get; set; }
        public int dock { get; set; }
        public int opacity { get; set; }
        public bool topmost { get; set; }
        public bool expanded { get; set; }
        public string bg { get; set; }
        public int addTarget { get; set; }
        public string exportDir { get; set; }
        public string lang { get; set; }
    }

    // ======================================================================
    // 多语言：zh-TW（默认）/ zh-CN / en-US
    // ======================================================================

    public static class L
    {
        public static int Lang = 0;     // 0 zh-TW, 1 zh-CN, 2 en-US
        public static readonly string[] Codes = { "zh-TW", "zh-CN", "en-US" };
        public static readonly string[] Names = { "繁體中文（台灣）", "简体中文", "English (US)" };

        public static int FromCode(string code)
        {
            for (int i = 0; i < Codes.Length; i++)
                if (string.Equals(Codes[i], code, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        public static string T(string key)
        {
            string[] v;
            if (S.TryGetValue(key, out v)) return v[Lang];
            return key;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(Culture, T(key), args);
        }

        public static CultureInfo Culture
        {
            get { return Lang == 2 ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.InvariantCulture; }
        }

        public static FontFamily Font
        {
            get
            {
                if (Lang == 0) return new FontFamily("Microsoft JhengHei UI, Microsoft YaHei UI");
                if (Lang == 1) return new FontFamily("Microsoft YaHei UI, Microsoft JhengHei UI");
                return new FontFamily("Segoe UI, Microsoft YaHei UI");
            }
        }

        public static string MonthTitle(DateTime d)
        {
            if (Lang == 2) return d.ToString("MMMM yyyy", Culture);
            return string.Format("{0}年{1}月", d.Year, d.Month);
        }

        public static string TodayTitle(DateTime d)
        {
            if (Lang == 2) return d.ToString("ddd, MMM d", Culture);
            string[] wd = Lang == 0
                ? new[] { "週日", "週一", "週二", "週三", "週四", "週五", "週六" }
                : new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return string.Format("{0}月{1}日 {2}", d.Month, d.Day, wd[(int)d.DayOfWeek]);
        }

        public static string[] WeekHeads()
        {
            if (Lang == 2) return new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
            return new[] { "一", "二", "三", "四", "五", "六", "日" };
        }

        static readonly Dictionary<string, string[]> S = new Dictionary<string, string[]>
        {
            { "app.title", new[] { "懸浮任務看板", "悬浮任务看板", "Floating Kanban" } },
            { "app.running", new[] { "懸浮任務看板已經在執行了。", "悬浮任务看板已经在运行了。", "Floating Kanban is already running." } },
            { "app.notice", new[] { "提示", "提示", "Notice" } },
            { "brand", new[] { "任務看板", "任务看板", "Task Board" } },
            { "col0", new[] { "待辦", "待办", "To do" } },
            { "col1", new[] { "進行中", "进行中", "In progress" } },
            { "col2", new[] { "已完成", "已完成", "Done" } },
            { "pill0", new[] { "待辦", "待办", "To do" } },
            { "pill1", new[] { "進行", "进行", "Doing" } },
            { "pill2", new[] { "完成", "完成", "Done" } },
            { "tip.pinOn", new[] { "取消置頂", "取消置顶", "Unpin from top" } },
            { "tip.pinOff", new[] { "視窗置頂", "窗口置顶", "Keep on top" } },
            { "tip.opacity", new[] { "不透明度", "不透明度", "Opacity" } },
            { "tip.color", new[] { "背景顏色", "背景颜色", "Background color" } },
            { "tip.lang", new[] { "語言 / Language", "语言 / Language", "Language" } },
            { "tip.export", new[] { "匯出已完成任務", "导出已完成任务", "Export completed tasks" } },
            { "tip.collapse", new[] { "收合為膠囊", "收起为胶囊", "Collapse to pill" } },
            { "tip.close", new[] { "結束", "退出", "Quit" } },
            { "input.ph", new[] { "新增任務，按 Enter 確認", "添加新任务，按 Enter 确认", "Add a task, press Enter" } },
            { "tip.target", new[] { "新任務加入：{0}", "新任务加入：{0}", "New tasks go to: {0}" } },
            { "tip.add", new[] { "新增（Enter）", "添加（Enter）", "Add (Enter)" } },
            { "menu.addTo", new[] { "新增到", "添加到", "Add to" } },
            { "archive", new[] { "歸檔", "归档", "Archive" } },
            { "tip.archive", new[] { "將已完成任務移出看板\n記錄會保留，仍可匯出", "将已完成任务移出看板\n记录会保留，仍可导出", "Move done tasks off the board\nRecords are kept for export" } },
            { "empty", new[] { "暫無任務", "暂无任务", "No tasks" } },
            { "tip.markDone", new[] { "標記為已完成", "标记为已完成", "Mark as done" } },
            { "tip.markUndone", new[] { "標記為未完成", "标记为未完成", "Mark as not done" } },
            { "tip.card", new[] { "雙擊編輯 · 右鍵更多\n建立：{0}", "双击编辑 · 右键更多\n创建：{0}", "Double-click to edit · Right-click for more\nCreated: {0}" } },
            { "tip.cardDone", new[] { "\n完成：{0}", "\n完成：{0}", "\nDone: {0}" } },
            { "unknown", new[] { "未知", "未知", "unknown" } },
            { "m.pinTop", new[] { "置頂", "置顶", "Move to top" } },
            { "m.pinTopN", new[] { "置頂選取的 {0} 項", "置顶选中的 {0} 项", "Move {0} selected to top" } },
            { "m.edit", new[] { "編輯", "编辑", "Edit" } },
            { "m.delete", new[] { "刪除", "删除", "Delete" } },
            { "m.deleteN", new[] { "刪除選取的 {0} 項", "删除选中的 {0} 项", "Delete {0} selected" } },
            { "toast.deleted", new[] { "已刪除 {0} 項", "已删除 {0} 项", "Deleted {0} item(s)" } },
            { "toast.archived", new[] { "已歸檔 {0} 項，匯出時仍會包含", "已归档 {0} 项，导出时仍会包含", "Archived {0} item(s), still included in exports" } },
            { "undo", new[] { "復原", "撤销", "Undo" } },
            { "m.colorHead", new[] { "莫蘭迪色系", "莫兰迪色系", "Muted palette" } },
            { "m.customColor", new[] { "自訂顏色…", "自定义颜色…", "Custom color…" } },
            { "m.langHead", new[] { "介面語言", "界面语言", "Interface language" } },
            { "m.exportHead", new[] { "匯出已完成任務 · CSV", "导出已完成任务 · CSV", "Export completed tasks · CSV" } },
            { "r.week", new[] { "本週", "本周", "This week" } },
            { "r.lastWeek", new[] { "上週", "上周", "Last week" } },
            { "r.month", new[] { "本月", "本月", "This month" } },
            { "r.lastMonth", new[] { "上個月", "上月", "Last month" } },
            { "r.quarter", new[] { "本季", "本季度", "This quarter" } },
            { "r.custom", new[] { "自訂時間範圍…", "自定义时间段…", "Custom range…" } },
            { "count", new[] { "{0} 筆", "{0} 条", "{0}" } },
            { "toast.none", new[] { "這段時間內沒有已完成的任務", "该时间段内没有已完成的任务", "No completed tasks in this range" } },
            { "toast.exported", new[] { "已匯出 {0} 筆任務", "已导出 {0} 条任务", "Exported {0} task(s)" } },
            { "openFolder", new[] { "開啟資料夾", "打开文件夹", "Show in folder" } },
            { "toast.exportFail", new[] { "匯出失敗：檔案可能正被其他程式使用", "导出失败：文件可能被其他程序占用", "Export failed: the file may be in use" } },
            { "csv.filter", new[] { "CSV 檔案 (*.csv)|*.csv", "CSV 文件 (*.csv)|*.csv", "CSV files (*.csv)|*.csv" } },
            { "csv.file", new[] { "已完成任務_{0:yyyyMMdd}_至_{1:yyyyMMdd}.csv", "已完成任务_{0:yyyyMMdd}_至_{1:yyyyMMdd}.csv", "completed_tasks_{0:yyyyMMdd}_{1:yyyyMMdd}.csv" } },
            { "csv.head", new[] { "完成時間,任務內容,建立時間,用時(天)", "完成时间,任务内容,录入时间,用时(天)", "Completed,Task,Created,Days taken" } },
            { "dlg.export", new[] { "匯出已完成任務", "导出已完成任务", "Export completed tasks" } },
            { "dlg.exportOk", new[] { "匯出", "导出", "Export" } },
            { "cancel", new[] { "取消", "取消", "Cancel" } },
            { "ok", new[] { "確定", "确定", "OK" } },
            { "dlg.pickEnd", new[] { "請點選結束日期", "请点击结束日期", "Now pick an end date" } },
            { "dlg.summary", new[] { "{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd} · 共 {2} 筆", "{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd} · 共 {2} 条", "{0:yyyy-MM-dd} – {1:yyyy-MM-dd} · {2} task(s)" } },
            { "dlg.color", new[] { "自訂背景顏色", "自定义背景颜色", "Custom background color" } },
            { "dlg.hex", new[] { "色碼", "色值", "Hex" } },
            { "tray.toggle", new[] { "顯示 / 隱藏", "显示 / 隐藏", "Show / Hide" } },
            { "tray.pin", new[] { "視窗置頂", "窗口置顶", "Always on top" } },
            { "tray.quit", new[] { "結束", "退出", "Quit" } },
            { "seed1", new[] { "雙擊卡片可修改內容，右鍵有更多操作", "双击卡片可修改内容，右键有更多操作", "Double-click a card to edit, right-click for more" } },
            { "seed2", new[] { "在欄位空白處拖曳，可框選多張卡片", "在列的空白处拖动，可框选多张卡片", "Drag on empty space to box-select cards" } },
            { "seed3", new[] { "拖曳卡片可排序或跨欄移動，多選可一起拖", "拖动卡片可排序或跨列移动，多选可一起拖", "Drag cards to reorder or move, selections move together" } },
            { "seed4", new[] { "已完成的任務可在右上角匯出為 CSV", "已完成的任务可在右上角导出为 CSV", "Export done tasks to CSV from the top-right" } },
            { "pal0", new[] { "雲白（預設）", "云白（默认）", "Cloud white (default)" } },
            { "pal1", new[] { "米灰", "米灰", "Greige" } },
            { "pal2", new[] { "亞麻", "亚麻", "Linen" } },
            { "pal3", new[] { "燕麥", "燕麦", "Oat" } },
            { "pal4", new[] { "豆沙粉", "豆沙粉", "Dusty rose" } },
            { "pal5", new[] { "藕粉", "藕粉", "Lotus pink" } },
            { "pal6", new[] { "陶土", "陶土", "Clay" } },
            { "pal7", new[] { "藕紫", "藕紫", "Lilac" } },
            { "pal8", new[] { "霧霾藍", "雾霾蓝", "Haze blue" } },
            { "pal9", new[] { "霧藍", "雾蓝", "Mist blue" } },
            { "pal10", new[] { "灰豆綠", "灰豆绿", "Sage" } },
            { "pal11", new[] { "灰綠", "灰绿", "Grey green" } },
            { "pal12", new[] { "青瓷", "青瓷", "Celadon" } },
        };
    }

    // ======================================================================
    // 主题、图标、缓动
    // ======================================================================

    public static class Th
    {
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public const string GPin = "";
        public const string GOpacity = "";
        public const string GColor = "";
        public const string GGlobe = "";
        public const string GExport = "";
        public const string GCollapse = "";
        public const string GClose = "";
        public const string GAdd = "";
        public const string GCheck = "";
        public const string GChevron = "";
        public const string GDelete = "";
        public const string GEdit = "";
        public const string GUp = "";
        public const string GArchive = "";
        public const string GCalendar = "";
        public const string GLeft = "";
        public const string GRight = "";

        public static readonly Color Accent = C(0x4F, 0x6E, 0xF7);
        public static readonly Color AccentDeep = C(0x3D, 0x5A, 0xE0);
        public static readonly Color[] Lane = { C(0x4F, 0x6E, 0xF7), C(0xF5, 0x9E, 0x0B), C(0x1F, 0xB2, 0x7A) };

        public static readonly SolidColorBrush AccentBrush = Solid(Accent);
        public static readonly SolidColorBrush AccentDeepBrush = Solid(AccentDeep);
        public static readonly SolidColorBrush AccentSoft = Tint(Accent, 0x1C);
        public static readonly SolidColorBrush AccentLine = Tint(Accent, 0xB0);
        public static readonly SolidColorBrush TextStrong = S(0xFF, 0x1F, 0x24, 0x30);
        public static readonly SolidColorBrush Text = S(0xFF, 0x3A, 0x41, 0x50);
        public static readonly SolidColorBrush Muted = S(0xFF, 0x6E, 0x78, 0x8A);
        public static readonly SolidColorBrush Faint = S(0xFF, 0xA0, 0xA8, 0xB5);
        public static readonly SolidColorBrush Outline = S(0x22, 0x1F, 0x24, 0x30);
        public static readonly SolidColorBrush CardLine = S(0x16, 0x1F, 0x24, 0x30);
        public static readonly SolidColorBrush CardLineHover = S(0x34, 0x1F, 0x24, 0x30);
        public static readonly SolidColorBrush Hover = S(0x14, 0x1F, 0x24, 0x30);
        public static readonly SolidColorBrush DangerBrush = S(0xFF, 0xE5, 0x48, 0x4D);
        public static readonly SolidColorBrush MenuHover = S(0xFF, 0xEE, 0xF1, 0xFE);
        public static readonly SolidColorBrush MenuLine = S(0xFF, 0xE4, 0xE7, 0xEE);
        public static readonly SolidColorBrush Chip = S(0xFF, 0xF1, 0xF3, 0xF7);
        public static readonly SolidColorBrush TipBg = S(0xF2, 0x22, 0x27, 0x33);

        public static Color C(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); }

        public static SolidColorBrush S(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        public static SolidColorBrush Solid(Color c)
        {
            var br = new SolidColorBrush(c);
            br.Freeze();
            return br;
        }

        public static SolidColorBrush Tint(Color c, byte a)
        {
            return S(a, c.R, c.G, c.B);
        }

        // 颜色混合：t=0 返回 a，t=1 返回 b
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static DropShadowEffect Shadow(double blur, double depth, double opacity)
        {
            return new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = C(0x1A, 0x22, 0x3A) };
        }

        public static TextBlock Icon(string glyph, double size, Brush fg)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = size,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
        }

        // 与 app.ico 同款的矢量标志：圆角方块 + 三根高低柱
        public static FrameworkElement Logo(double s)
        {
            var g = new Grid { Width = s, Height = s, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(s * 0.28),
                Background = new LinearGradientBrush(C(0x6B, 0x86, 0xFF), Accent, 50)
            });
            var c = new Canvas();
            double pad = s * 0.22, bw = s * 0.15, gap = (s - 2 * pad - 3 * bw) / 2;
            double[] hs = { 0.40, 0.60, 0.28 };
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle { Width = bw, Height = s * hs[i], RadiusX = bw / 2, RadiusY = bw / 2, Fill = Brushes.White };
                Canvas.SetLeft(r, pad + i * (bw + gap));
                Canvas.SetTop(r, s * 0.20);
                c.Children.Add(r);
            }
            g.Children.Add(c);
            return g;
        }

        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        public static double Clamp01(double t) { return t < 0 ? 0 : (t > 1 ? 1 : t); }
        public static double InOutCubic(double t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        public static double OutCubic(double t) { return 1 - Math.Pow(1 - t, 3); }
        public static double OutQuart(double t) { return 1 - Math.Pow(1 - t, 4); }
    }

    // 逐帧补间：挂在 CompositionTarget.Rendering 上，与 WPF 渲染同步，
    // 让窗口位移、尺寸、透明度在同一帧里一起更新
    public sealed class Tween
    {
        readonly Stopwatch _sw = new Stopwatch();
        readonly double _ms;
        readonly Func<double, double> _ease;
        readonly Action<double> _frame;
        readonly Action _done;
        bool _stopped;

        Tween(double ms, Func<double, double> ease, Action<double> frame, Action done)
        {
            _ms = Math.Max(1, ms); _ease = ease; _frame = frame; _done = done;
        }

        public static Tween Run(double ms, Func<double, double> ease, Action<double> frame, Action done)
        {
            var tw = new Tween(ms, ease, frame, done);
            if (ms <= 0)
            {
                frame(1);
                if (done != null) done();
                tw._stopped = true;
                return tw;
            }
            frame(ease(0));
            tw._sw.Start();
            CompositionTarget.Rendering += tw.Tick;
            return tw;
        }

        void Tick(object s, EventArgs e)
        {
            if (_stopped) return;
            double t = Math.Min(1, _sw.Elapsed.TotalMilliseconds / _ms);
            _frame(_ease(t));
            if (t >= 1)
            {
                Stop();
                if (_done != null) _done();
            }
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            CompositionTarget.Rendering -= Tick;
        }
    }

    static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    }

    // ======================================================================
    // 统一的菜单样式
    // ======================================================================

    public static class Menus
    {
        static Style _menuStyle;
        static ControlTemplate _itemTmpl, _sepTmpl, _headTmpl;

        public static ContextMenu New()
        {
            if (_menuStyle == null)
            {
                var tmpl = new ControlTemplate(typeof(ContextMenu));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.SetValue(Border.BackgroundProperty, Brushes.White);
                bd.SetValue(Border.BorderBrushProperty, Th.MenuLine);
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                bd.SetValue(Border.PaddingProperty, new Thickness(4));
                bd.SetValue(FrameworkElement.MarginProperty, new Thickness(10));
                bd.SetValue(UIElement.EffectProperty, Th.Shadow(14, 3, 0.16));
                var host = new FrameworkElementFactory(typeof(StackPanel));
                host.SetValue(Panel.IsItemsHostProperty, true);
                bd.AppendChild(host);
                tmpl.VisualTree = bd;

                var st = new Style(typeof(ContextMenu));
                st.Setters.Add(new Setter(Control.TemplateProperty, tmpl));
                st.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
                st.Setters.Add(new Setter(Control.ForegroundProperty, Th.TextStrong));
                st.Setters.Add(new Setter(ContextMenu.HasDropShadowProperty, false));
                _menuStyle = st;
            }
            var m = new ContextMenu { Style = _menuStyle, FontFamily = L.Font };
            TextOptions.SetTextFormattingMode(m, TextFormattingMode.Display);
            return m;
        }

        public static MenuItem Item(object header, string glyph, bool check, bool danger, Action click)
        {
            if (_itemTmpl == null)
            {
                var t = new ControlTemplate(typeof(MenuItem));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.Name = "bd";
                bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(Border.PaddingProperty, new Thickness(8, 6, 14, 6));
                bd.SetValue(FrameworkElement.MinWidthProperty, 172.0);
                var dp = new FrameworkElementFactory(typeof(DockPanel));
                var ic = new FrameworkElementFactory(typeof(ContentPresenter));
                ic.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
                ic.SetValue(FrameworkElement.WidthProperty, 26.0);
                ic.SetValue(DockPanel.DockProperty, Dock.Left);
                ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                var hd = new FrameworkElementFactory(typeof(ContentPresenter));
                hd.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                hd.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                dp.AppendChild(ic);
                dp.AppendChild(hd);
                bd.AppendChild(dp);
                t.VisualTree = bd;

                var hl = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
                var hs = new Setter(Border.BackgroundProperty, Th.MenuHover) { TargetName = "bd" };
                hl.Setters.Add(hs);
                t.Triggers.Add(hl);
                var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42) { TargetName = "bd" });
                t.Triggers.Add(dis);
                _itemTmpl = t;
            }
            var mi = new MenuItem { Header = header, Template = _itemTmpl };
            string g = check ? Th.GCheck : glyph;
            if (g != null) mi.Icon = Th.Icon(g, 13, danger ? Th.DangerBrush : (check ? Th.AccentBrush : Th.Muted));
            if (danger) mi.Foreground = Th.DangerBrush;
            if (click != null) mi.Click += delegate { click(); };
            return mi;
        }

        public static Separator Sep()
        {
            if (_sepTmpl == null)
            {
                var t = new ControlTemplate(typeof(Separator));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.SetValue(FrameworkElement.HeightProperty, 1.0);
                bd.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 4, 10, 4));
                bd.SetValue(Border.BackgroundProperty, Th.MenuLine);
                t.VisualTree = bd;
                _sepTmpl = t;
            }
            return new Separator { Template = _sepTmpl, Focusable = false };
        }

        public static Separator Head(string text)
        {
            if (_headTmpl == null)
            {
                var t = new ControlTemplate(typeof(Separator));
                var tb = new FrameworkElementFactory(typeof(TextBlock));
                tb.SetBinding(TextBlock.TextProperty, new Binding("Tag") { RelativeSource = RelativeSource.TemplatedParent });
                tb.SetValue(TextBlock.FontSizeProperty, 11.0);
                tb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
                tb.SetValue(TextBlock.ForegroundProperty, Th.Faint);
                tb.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 6, 10, 4));
                t.VisualTree = tb;
                _headTmpl = t;
            }
            return new Separator { Template = _headTmpl, Tag = text, Focusable = false };
        }

        public static void OpenBelow(ContextMenu m, FrameworkElement target)
        {
            m.PlacementTarget = target;
            m.Placement = PlacementMode.Bottom;
            m.HorizontalOffset = -10;    // 抵消模板里为阴影预留的边距
            m.VerticalOffset = -6;
            m.IsOpen = true;
        }

        public static void OpenAtMouse(ContextMenu m, FrameworkElement target)
        {
            m.PlacementTarget = target;
            m.Placement = PlacementMode.MousePoint;
            m.HorizontalOffset = -6;
            m.VerticalOffset = -6;
            m.IsOpen = true;
        }
    }

    // 标题栏图标按钮
    class IconButton
    {
        public readonly Border Root;
        public readonly TextBlock Glyph;
        readonly bool _danger;
        bool _active, _hover;

        public IconButton(string glyph, double size, bool danger, Action click)
        {
            _danger = danger;
            Root = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand
            };
            Glyph = Th.Icon(glyph, size, Th.Muted);
            Root.Child = Glyph;
            Root.MouseEnter += delegate { _hover = true; Update(); };
            Root.MouseLeave += delegate { _hover = false; Update(); };
            Root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            Root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (Root.IsMouseOver) click();
            };
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; Update(); }
        }

        void Update()
        {
            if (_danger && _hover)
            {
                Root.Background = Th.DangerBrush;
                Glyph.Foreground = Brushes.White;
                return;
            }
            Root.Background = _hover ? Th.Hover : (_active ? Th.AccentSoft : (Brush)Brushes.Transparent);
            Glyph.Foreground = _active ? Th.AccentBrush : (_hover ? Th.TextStrong : Th.Muted);
        }
    }

    class CardInfo
    {
        public TaskItem Task;
        public Border Card;
        public TextBlock Text;
        public Grid Row;
        public bool Hover;
    }

    // ======================================================================
    // 入口
    // ======================================================================

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "FloatingKanban_SingleInstance", out createdNew);
            if (!createdNew)
            {
                L.Lang = KanbanWindow.PeekLanguage();
                MessageBox.Show(L.T("app.running"), L.T("app.notice"));
                return;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            Application app = new Application();
            KanbanWindow win = new KanbanWindow();
            app.SessionEnding += delegate { win.SaveNow(); };
            win.Show();
            app.Run();
            GC.KeepAlive(mutex);
        }
    }

    // ======================================================================
    // 主窗口
    // ======================================================================

    public class KanbanWindow : Window
    {
        const double M = 16;          // 窗口四周为阴影预留的透明边距
        const double PillH = 38;      // 胶囊高度
        const double Sliver = 5;      // 贴边隐藏时露出的宽度
        const double RExp = 14;       // 展开时的圆角
        const double MinW = 380, MinH = 420;
        static readonly double[] OpLevels = { 1.0, 0.85, 0.70, 0.55 };

        List<TaskItem> _tasks = new List<TaskItem>();
        List<TaskItem> _archive = new List<TaskItem>();
        string _dir, _file;

        // —— 窗口状态（屏幕 DIP） ——
        Rect _eRect = Rect.Empty;     // 展开时卡片区域
        Point _pPos;                  // 胶囊左上角
        bool _hasPill;
        int _dock;                    // 0 无 1 左 2 右 3 上 4 下
        bool _expanded = true, _animating, _peeking;
        double _pillW = 200;
        int _addTarget, _opacityIndex = 0;
        bool _topmost = true;
        Color _bgColor = Colors.White;
        string _exportDir;

        // —— 视觉树 ——
        Grid _host, _stage, _inner, _main, _pillContent;
        StackPanel _pillStack;
        TextBlock[] _pillCounts;
        readonly TranslateTransform _dockTx = new TranslateTransform();
        Border _shadow, _shell, _dockHandle;
        readonly RectangleGeometry _clipGeo = new RectangleGeometry();
        SolidColorBrush _shellBg = new SolidColorBrush(Colors.White);
        SolidColorBrush _laneBg = new SolidColorBrush(Colors.White);
        SolidColorBrush _cardBg, _cardDoneBg, _cardSelBg;
        Canvas _overlay;
        Border _tip; TextBlock _tipText;
        FrameworkElement _tipOwner; Func<string> _tipFn;
        Border _toast; TextBlock _toastText, _toastAction; Action _toastCallback;
        Border[] _lanes; StackPanel[] _colPanels; ScrollViewer[] _scrolls; TextBlock[] _countTexts;
        TextBox _input; TextBlock _placeholder, _dateText; Border _inputBox, _targetBtn;
        IconButton _pinBtn, _opacityBtn, _colorBtn, _langBtn, _exportBtn;

        System.Windows.Forms.NotifyIcon _tray;
        System.Windows.Forms.ToolStripMenuItem _trayToggleItem, _trayPinItem, _trayQuitItem;

        // —— 计时器与动画 ——
        readonly DispatcherTimer _saveTimer, _tipTimer, _toastTimer, _dockTimer, _scrollTimer;
        Tween _morph, _dockTween, _glide;

        // —— 选择与拖动 ——
        HashSet<TaskItem> _selected = new HashSet<TaskItem>();
        readonly Dictionary<TaskItem, CardInfo> _cards = new Dictionary<TaskItem, CardInfo>();
        int _press;                   // 0 无 1 按在卡片上 2 框选
        TaskItem _pressTask;
        Point _pressPt, _lastPt;
        bool _pressCtrl, _pressWasSelected;
        bool _dragging, _dropping, _marquee;
        List<TaskItem> _dragItems;
        FrameworkElement _ghost;
        Point _grabOffset;
        Rectangle _ph, _marqueeRect;
        int _phCol = -1, _phIdx = -1;
        double _phHeight;
        HashSet<TaskItem> _marqueeBase;
        Grid _editing;

        public KanbanWindow()
        {
            _dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingKanban");
            _file = System.IO.Path.Combine(_dir, "data.json");

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += delegate { SaveNow(); };
            _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _tipTimer.Tick += delegate { _tipTimer.Stop(); ShowTip(); };
            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += delegate { HideToast(); };
            _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _dockTimer.Tick += delegate
            {
                _dockTimer.Stop();
                if (!_host.IsMouseOver && _dock != 0 && !_expanded && !_animating) DockIn(true);
            };
            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            _scrollTimer.Tick += delegate { if (_dragging && !_dropping && AutoScroll(_lastPt)) UpdateDrag(_lastPt); };

            LoadState();
            Topmost = _topmost;
            ApplyLanguageBasics();

            BuildUi();
            ApplyBackgroundColor(_bgColor, false);
            Refresh(false);
            ApplyInitialGeometry();

            MouseMove += OnWinMouseMove;
            MouseLeftButtonUp += OnWinMouseUp;
            LostMouseCapture += OnWinLostCapture;
            PreviewKeyDown += OnWinKey;
            Deactivated += delegate { HideTip(); };

            SetupTray();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveNow();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            base.OnClosed(e);
        }

        void ApplyLanguageBasics()
        {
            Title = L.T("app.title");
            FontFamily = L.Font;
        }

        // ================= 构建界面 =================

        void BuildUi()
        {
            _host = new Grid();
            _host.MouseEnter += HostEnter;
            _host.MouseLeave += HostLeave;

            _stage = new Grid { RenderTransform = _dockTx };
            _shadow = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(M),
                Background = _shellBg,
                CornerRadius = new CornerRadius(RExp),
                Effect = Th.Shadow(16, 3, 0.17),
                IsHitTestVisible = false
            };
            _shell = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(M),
                Background = _shellBg,
                BorderBrush = Th.Outline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(RExp)
            };
            _inner = new Grid { Clip = _clipGeo };
            _shell.Child = _inner;
            _stage.Children.Add(_shadow);
            _stage.Children.Add(_shell);
            _host.Children.Add(_stage);

            _dockHandle = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(2),
                Background = Th.AccentBrush,
                Opacity = 0,
                IsHitTestVisible = false
            };
            _host.Children.Add(_dockHandle);

            _main = new Grid { Margin = new Thickness(14, 8, 14, 14) };
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _inner.Children.Add(_main);

            BuildTitleBar();
            BuildInputRow();
            BuildLanes();
            BuildResizeGrip();
            BuildPill();
            BuildOverlay();
            BuildToast();

            Content = _host;
        }

        void BuildTitleBar()
        {
            var bar = new Grid { Background = Brushes.Transparent, Height = 40 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_animating) return;
                HideTip();
                try { DragMove(); } catch { }
                SyncGeometry();
                SaveSoon();
            };

            var brand = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = true, Margin = new Thickness(2, 0, 8, 0) };
            var logo = Th.Logo(18);
            DockPanel.SetDock(logo, Dock.Left);
            brand.Children.Add(logo);
            var title = new TextBlock { Text = L.T("brand"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Th.TextStrong, Margin = new Thickness(9, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left);
            brand.Children.Add(title);
            _dateText = new TextBlock { FontSize = 11.5, Foreground = Th.Faint, Margin = new Thickness(9, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            brand.Children.Add(_dateText);
            bar.Children.Add(brand);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _pinBtn = new IconButton(Th.GPin, 13, false, TogglePin);
            _pinBtn.Active = _topmost;
            Tip(_pinBtn.Root, delegate { return L.T(_topmost ? "tip.pinOn" : "tip.pinOff"); });
            _opacityBtn = new IconButton(Th.GOpacity, 14, false, ShowOpacityMenu);
            Tip(_opacityBtn.Root, delegate { return L.T("tip.opacity"); });
            _colorBtn = new IconButton(Th.GColor, 14, false, ShowColorMenu);
            Tip(_colorBtn.Root, delegate { return L.T("tip.color"); });
            _langBtn = new IconButton(Th.GGlobe, 14, false, ShowLanguageMenu);
            Tip(_langBtn.Root, delegate { return L.T("tip.lang"); });
            _exportBtn = new IconButton(Th.GExport, 14, false, ShowExportMenu);
            Tip(_exportBtn.Root, delegate { return L.T("tip.export"); });
            var collapse = new IconButton(Th.GCollapse, 13, false, Collapse);
            Tip(collapse.Root, delegate { return L.T("tip.collapse"); });
            var close = new IconButton(Th.GClose, 12, true, delegate { Close(); });
            Tip(close.Root, delegate { return L.T("tip.close"); });

            btns.Children.Add(_pinBtn.Root);
            btns.Children.Add(_opacityBtn.Root);
            btns.Children.Add(_colorBtn.Root);
            btns.Children.Add(_langBtn.Root);
            btns.Children.Add(_exportBtn.Root);
            btns.Children.Add(new Border { Width = 1, Height = 16, Background = Th.CardLine, Margin = new Thickness(5, 0, 5, 0) });
            btns.Children.Add(collapse.Root);
            btns.Children.Add(close.Root);
            Grid.SetColumn(btns, 1);
            bar.Children.Add(btns);

            _main.Children.Add(bar);
        }

        void BuildInputRow()
        {
            _inputBox = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = _laneBg,
                BorderBrush = Th.CardLine,
                BorderThickness = new Thickness(1),
                Height = 40,
                Margin = new Thickness(0, 4, 0, 12),
                Padding = new Thickness(5, 0, 5, 0)
            };
            Grid.SetRow(_inputBox, 1);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _targetBtn = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 0, 7, 0), Height = 28, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            _targetBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; ShowTargetMenu(); };
            Tip(_targetBtn, delegate { return L.F("tip.target", L.T("col" + _addTarget)); });
            g.Children.Add(_targetBtn);
            UpdateTargetBtn();

            var box = new Grid { Margin = new Thickness(8, 0, 4, 0) };
            _input = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Padding = new Thickness(0),
                Foreground = Th.TextStrong,
                CaretBrush = Th.AccentBrush,
                SelectionBrush = Th.AccentBrush,
                FocusVisualStyle = null
            };
            _placeholder = new TextBlock { Text = L.T("input.ph"), Foreground = Th.Faint, FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            _input.TextChanged += delegate { _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            _input.GotKeyboardFocus += delegate { _inputBox.BorderBrush = Th.AccentLine; };
            _input.LostKeyboardFocus += delegate { _inputBox.BorderBrush = Th.CardLine; };
            _input.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; AddTask(); } };
            box.Children.Add(_input);
            box.Children.Add(_placeholder);
            Grid.SetColumn(box, 1);
            g.Children.Add(box);

            var add = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Th.AccentBrush, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            add.Child = Th.Icon(Th.GAdd, 12, Brushes.White);
            add.MouseEnter += delegate { add.Background = Th.AccentDeepBrush; };
            add.MouseLeave += delegate { add.Background = Th.AccentBrush; };
            add.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; AddTask(); };
            Tip(add, delegate { return L.T("tip.add"); });
            Grid.SetColumn(add, 2);
            g.Children.Add(add);

            _inputBox.Child = g;
            _inputBox.MouseLeftButtonDown += delegate { _input.Focus(); };
            _main.Children.Add(_inputBox);
        }

        void UpdateTargetBtn()
        {
            Color c = Th.Lane[_addTarget];
            _targetBtn.Background = Th.Tint(c, 0x1E);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Th.Solid(c), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = L.T("col" + _addTarget), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Th.Solid(Th.Mix(c, Colors.Black, 0.18)), Margin = new Thickness(6, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(Th.Icon(Th.GChevron, 9, Th.Solid(c)));
            _targetBtn.Child = sp;
        }

        void BuildLanes()
        {
            var cols = new Grid();
            Grid.SetRow(cols, 2);
            for (int i = 0; i < 3; i++)
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _lanes = new Border[3];
            _colPanels = new StackPanel[3];
            _scrolls = new ScrollViewer[3];
            _countTexts = new TextBlock[3];

            for (int i = 0; i < 3; i++)
            {
                Color c = Th.Lane[i];
                var lane = new Border { CornerRadius = new CornerRadius(11), Background = _laneBg, Padding = new Thickness(6, 6, 6, 0) };
                lane.Margin = i == 0 ? new Thickness(0, 0, 4, 0) : (i == 1 ? new Thickness(4, 0, 4, 0) : new Thickness(4, 0, 0, 0));
                var dp = new DockPanel();

                var header = new DockPanel { LastChildFill = false, Height = 26, Margin = new Thickness(4, 0, 2, 4), Background = Brushes.Transparent };
                header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    if (_animating) return;
                    HideTip();
                    try { DragMove(); } catch { }
                    SyncGeometry();
                    SaveSoon();
                };
                var hp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                hp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Th.Solid(c), VerticalAlignment = VerticalAlignment.Center });
                hp.Children.Add(new TextBlock { Text = L.T("col" + i), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Th.TextStrong, Margin = new Thickness(7, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center });
                var chip = new Border { CornerRadius = new CornerRadius(8), Background = Th.Tint(c, 0x22), Padding = new Thickness(6, 0, 6, 1), MinWidth = 20, VerticalAlignment = VerticalAlignment.Center };
                _countTexts[i] = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Th.Solid(Th.Mix(c, Colors.Black, 0.15)), Text = "0", HorizontalAlignment = HorizontalAlignment.Center };
                chip.Child = _countTexts[i];
                hp.Children.Add(chip);
                DockPanel.SetDock(hp, Dock.Left);
                header.Children.Add(hp);

                if (i == 2)
                {
                    var arc = TextButton(Th.GArchive, L.T("archive"), ArchiveDone);
                    Tip(arc, delegate { return L.T("tip.archive"); });
                    DockPanel.SetDock(arc, Dock.Right);
                    header.Children.Add(arc);
                    // 列太窄时只留图标，避免文字被截断
                    var label = (TextBlock)((StackPanel)arc.Child).Children[1];
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double need = label.DesiredSize.Width + 32;
                    header.SizeChanged += delegate
                    {
                        label.Visibility = header.ActualWidth - hp.ActualWidth - 6 < need ? Visibility.Collapsed : Visibility.Visible;
                    };
                }

                DockPanel.SetDock(header, Dock.Top);
                dp.Children.Add(header);

                var sv = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false
                };
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                sv.Content = panel;
                dp.Children.Add(sv);
                lane.Child = dp;
                lane.MouseLeftButtonDown += LaneDown;

                _lanes[i] = lane;
                _colPanels[i] = panel;
                _scrolls[i] = sv;
                Grid.SetColumn(lane, i);
                cols.Children.Add(lane);
            }
            _main.Children.Add(cols);
        }

        Border TextButton(string glyph, string text, Action click)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 7, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Th.Icon(glyph, 11, Th.Muted);
            var tb = new TextBlock { Text = text, FontSize = 11.5, Foreground = Th.Muted, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(ic);
            sp.Children.Add(tb);
            b.Child = sp;
            b.MouseEnter += delegate { b.Background = Th.Hover; ic.Foreground = Th.TextStrong; tb.Foreground = Th.TextStrong; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; ic.Foreground = Th.Muted; tb.Foreground = Th.Muted; };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (b.IsMouseOver) click(); };
            return b;
        }

        // 右下角缩放手柄
        void BuildResizeGrip()
        {
            var icon = new Canvas { Width = 10, Height = 10, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -9, -9) };
            double[][] dots = { new double[] { 7, 7 }, new double[] { 3.5, 7 }, new double[] { 7, 3.5 } };
            foreach (var d in dots)
            {
                var e = new Ellipse { Width = 2.2, Height = 2.2, Fill = Th.Faint };
                Canvas.SetLeft(e, d[0]);
                Canvas.SetTop(e, d[1]);
                icon.Children.Add(e);
            }
            var tmpl = new ControlTemplate(typeof(Thumb));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            tmpl.VisualTree = bd;
            var grip = new Thumb
            {
                Width = 18,
                Height = 18,
                Cursor = Cursors.SizeNWSE,
                Template = tmpl,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -13, -13)
            };
            grip.DragDelta += delegate(object s, DragDeltaEventArgs e)
            {
                if (_animating) return;
                double w = Math.Max(MinW, _shell.Width + e.HorizontalChange);
                double h = Math.Max(MinH, _shell.Height + e.VerticalChange);
                SetShellSize(w, h, RExp);
                Width = w + 2 * M;
                Height = h + 2 * M;
            };
            grip.DragCompleted += delegate { SyncGeometry(); SaveSoon(); };
            Grid.SetRow(grip, 2);
            Grid.SetRow(icon, 2);
            _main.Children.Add(icon);
            _main.Children.Add(grip);
        }

        void BuildPill()
        {
            _pillContent = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _pillStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _pillStack.Children.Add(Th.Logo(16));
            _pillCounts = new TextBlock[3];
            for (int i = 0; i < 3; i++)
            {
                _pillStack.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Th.Solid(Th.Lane[i]), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 11 : 12, 0, 5, 0) });
                _pillStack.Children.Add(new TextBlock { Text = L.T("pill" + i), FontSize = 12, Foreground = Th.Muted, VerticalAlignment = VerticalAlignment.Center });
                _pillCounts[i] = new TextBlock { Text = "0", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Th.TextStrong, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                _pillStack.Children.Add(_pillCounts[i]);
            }
            _pillContent.Children.Add(_pillStack);
            _pillContent.MouseLeftButtonDown += PillDown;
            _inner.Children.Add(_pillContent);
        }

        void BuildOverlay()
        {
            _overlay = new Canvas { IsHitTestVisible = false };
            _inner.Children.Add(_overlay);
            _tipText = new TextBlock { Foreground = Brushes.White, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 240, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            _tip = new Border { Background = Th.TipBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 5), Child = _tipText, Visibility = Visibility.Collapsed };
            _overlay.Children.Add(_tip);
        }

        void BuildToast()
        {
            _toastText = new TextBlock { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
            _toastAction = new TextBlock { Foreground = Th.S(0xFF, 0xA9, 0xBC, 0xFF), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            _toastAction.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                var cb = _toastCallback;
                HideToast();
                if (cb != null) cb();
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(_toastText);
            sp.Children.Add(_toastAction);
            _toast = new Border
            {
                Background = Th.S(0xF2, 0x23, 0x28, 0x34),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 8, 14, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(16, 0, 16, 20),
                Visibility = Visibility.Collapsed,
                RenderTransform = new TranslateTransform(),
                Child = sp
            };
            _inner.Children.Add(_toast);
        }

        // ================= 浮动提示（自绘，窗口未激活时也能显示） =================

        void Tip(FrameworkElement el, Func<string> fn)
        {
            Tip(el, fn, 450);
        }

        void Tip(FrameworkElement el, Func<string> fn, int delay)
        {
            el.MouseEnter += delegate
            {
                if (_press != 0 || _animating || !_expanded) return;
                _tipOwner = el;
                _tipFn = fn;
                _tip.Visibility = Visibility.Collapsed;
                _tipTimer.Stop();
                _tipTimer.Interval = TimeSpan.FromMilliseconds(delay);
                _tipTimer.Start();
            };
            el.MouseLeave += delegate { if (_tipOwner == el) HideTip(); };
            el.PreviewMouseDown += delegate { HideTip(); };
        }

        void ShowTip()
        {
            if (_tipOwner == null || _tipFn == null || !_tipOwner.IsMouseOver || !_tipOwner.IsVisible || _press != 0 || !_expanded || _animating) return;
            _tipText.Text = _tipFn();
            _tip.Visibility = Visibility.Visible;
            _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size ts = _tip.DesiredSize;
            Rect ob;
            try { ob = _tipOwner.TransformToVisual(_inner).TransformBounds(new Rect(_tipOwner.RenderSize)); }
            catch { HideTip(); return; }
            double W = _inner.ActualWidth, H = _inner.ActualHeight;
            double x = ob.Left + ob.Width / 2 - ts.Width / 2;
            double y = ob.Bottom + 6;
            if (y + ts.Height > H - 6) y = ob.Top - ts.Height - 6;
            x = Math.Max(6, Math.Min(x, W - ts.Width - 6));
            y = Math.Max(4, y);
            Canvas.SetLeft(_tip, Math.Round(x));
            Canvas.SetTop(_tip, Math.Round(y));
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        void HideTip()
        {
            _tipTimer.Stop();
            _tipOwner = null;
            if (_tip != null) _tip.Visibility = Visibility.Collapsed;
        }

        // ================= 底部提示条 =================

        void Toast(string msg, string action, Action cb)
        {
            if (!_expanded) return;
            _toastText.Text = msg;
            _toastAction.Text = action ?? "";
            _toastAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            _toastCallback = cb;
            _toast.Visibility = Visibility.Visible;
            var tt = (TranslateTransform)_toast.RenderTransform;
            _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(action == null ? 3200 : 6000);
            _toastTimer.Start();
        }

        void HideToast()
        {
            _toastTimer.Stop();
            _toastCallback = null;
            if (_toast == null || _toast.Visibility != Visibility.Visible) return;
            var a = new DoubleAnimation(_toast.Opacity, 0, TimeSpan.FromMilliseconds(160));
            a.Completed += delegate { if (_toastCallback == null && !_toastTimer.IsEnabled) _toast.Visibility = Visibility.Collapsed; };
            _toast.BeginAnimation(OpacityProperty, a);
        }

        // ================= 卡片 =================

        Border MakeCard(TaskItem t)
        {
            bool done = t.status == 2;
            var ci = new CardInfo { Task = t };
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Tag = ci
            };
            ci.Card = card;

            var g = new Grid();
            ci.Row = g;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var circle = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            var tick = Th.Icon(Th.GCheck, 9, Brushes.White);
            circle.Child = tick;
            Action paintCircle = delegate
            {
                if (done)
                {
                    circle.Background = Th.Solid(Th.Lane[2]);
                    circle.BorderThickness = new Thickness(0);
                    tick.Foreground = Brushes.White;
                    tick.Opacity = 1;
                }
                else
                {
                    bool h = circle.IsMouseOver;
                    circle.Background = h ? Th.Tint(Th.Lane[2], 0x22) : Brushes.Transparent;
                    circle.BorderBrush = h ? Th.Solid(Th.Lane[2]) : Th.S(0xFF, 0xB4, 0xBB, 0xC7);
                    circle.BorderThickness = new Thickness(1.5);
                    tick.Foreground = Th.Solid(Th.Lane[2]);
                    tick.Opacity = h ? 1 : 0;
                }
            };
            paintCircle();
            circle.MouseEnter += delegate { paintCircle(); };
            circle.MouseLeave += delegate { paintCircle(); };
            circle.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (_animating || _dropping) return;
                t.status = done ? 0 : 2;
                if (t.status == 2) t.completedAt = NowIso();
                Refresh();
            };
            Tip(circle, delegate { return L.T(done ? "tip.markUndone" : "tip.markDone"); });

            var txt = new TextBlock
            {
                Text = t.title,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = 36,                         // 平时最多两行，悬停展开
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 2, 0),
                Foreground = done ? Th.Faint : Th.TextStrong
            };
            if (done) txt.TextDecorations = StrikePen();
            ci.Text = txt;
            txt.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount >= 2 && !_animating) { e.Handled = true; StartEdit(g, txt, t); }
            };
            Tip(txt, delegate
            {
                string s = L.F("tip.card", Fmt(t.createdAt));
                if (t.status == 2) s += L.F("tip.cardDone", Fmt(t.completedAt));
                return s;
            }, 900);

            Grid.SetColumn(circle, 0);
            Grid.SetColumn(txt, 1);
            g.Children.Add(circle);
            g.Children.Add(txt);
            if (done)
            {
                var outer = new StackPanel();
                outer.Children.Add(g);
                outer.Children.Add(new TextBlock { Text = FmtShort(t.completedAt), FontSize = 10.5, Foreground = Th.Faint, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 1, 0) });
                card.Child = outer;
            }
            else card.Child = g;

            card.MouseEnter += delegate
            {
                if (_press != 0 || _dragging) return;
                ci.Hover = true;
                txt.MaxHeight = double.PositiveInfinity;
                txt.TextTrimming = TextTrimming.None;
                PaintCard(ci);
            };
            card.MouseLeave += delegate
            {
                ci.Hover = false;
                txt.MaxHeight = 36;
                txt.TextTrimming = TextTrimming.CharacterEllipsis;
                PaintCard(ci);
            };
            card.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { CardDown(ci, e); };
            card.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (_animating || _dropping || _press != 0) return;
                if (!_selected.Contains(t)) { _selected.Clear(); _selected.Add(t); PaintAllCards(); }
                OpenCardMenu(ci);
            };

            PaintCard(ci);
            return card;
        }

        void PaintCard(CardInfo ci)
        {
            bool sel = _selected.Contains(ci.Task);
            bool done = ci.Task.status == 2;
            ci.Card.Background = sel ? _cardSelBg : (done ? _cardDoneBg : _cardBg);
            ci.Card.BorderBrush = sel ? Th.AccentLine : (ci.Hover ? Th.CardLineHover : Th.CardLine);
        }

        void PaintAllCards()
        {
            foreach (var ci in _cards.Values) PaintCard(ci);
        }

        void OpenCardMenu(CardInfo ci)
        {
            var t = ci.Task;
            var list = SelectedInOrder();
            if (!list.Contains(t)) list = new List<TaskItem> { t };
            var m = Menus.New();
            if (list.Count > 1)
            {
                m.Items.Add(Menus.Item(L.F("m.pinTopN", list.Count), Th.GUp, false, false, delegate { PinToTop(list); }));
                m.Items.Add(Menus.Sep());
                m.Items.Add(Menus.Item(L.F("m.deleteN", list.Count), Th.GDelete, false, true, delegate { DeleteTasks(list); }));
            }
            else
            {
                m.Items.Add(Menus.Item(L.T("m.pinTop"), Th.GUp, false, false, delegate { PinToTop(list); }));
                m.Items.Add(Menus.Item(L.T("m.edit"), Th.GEdit, false, false, delegate { StartEdit(ci.Row, ci.Text, t); }));
                m.Items.Add(Menus.Sep());
                m.Items.Add(Menus.Item(L.T("m.delete"), Th.GDelete, false, true, delegate { DeleteTasks(list); }));
            }
            Menus.OpenAtMouse(m, ci.Card);
        }

        void StartEdit(Grid g, TextBlock txt, TaskItem t)
        {
            if (_editing != null || !g.Children.Contains(txt)) return;
            HideTip();
            bool finished = false;
            _editing = g;
            var ed = new TextBox
            {
                Text = t.title,
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 0, 2, 0),
                Foreground = Th.TextStrong,
                CaretBrush = Th.AccentBrush,
                SelectionBrush = Th.AccentBrush,
                TextWrapping = TextWrapping.Wrap,
                FocusVisualStyle = null
            };
            Action commit = delegate
            {
                if (finished) return;
                finished = true;
                _editing = null;
                string nt = ed.Text.Trim();
                if (nt.Length > 0) t.title = nt;
                Refresh();
            };
            Action cancel = delegate
            {
                if (finished) return;
                finished = true;
                _editing = null;
                Refresh();
            };
            ed.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; cancel(); }
            };
            ed.LostKeyboardFocus += delegate { commit(); };
            Grid.SetColumn(ed, 1);
            int idx = g.Children.IndexOf(txt);
            g.Children.RemoveAt(idx);
            g.Children.Insert(idx, ed);
            ed.Focus();
            ed.SelectAll();
        }

        // ================= 选择、框选、拖动 =================

        List<TaskItem> SelectedInOrder()
        {
            var list = new List<TaskItem>();
            foreach (var t in _tasks) if (_selected.Contains(t)) list.Add(t);
            return list;
        }

        static bool CtrlDown()
        {
            return (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        }

        void CardDown(CardInfo ci, MouseButtonEventArgs e)
        {
            if (_editing == ci.Row || _animating || _dropping || _press != 0) return;
            e.Handled = true;
            HideTip();
            if (_editing != null) Keyboard.ClearFocus();
            var t = ci.Task;
            bool ctrl = CtrlDown();
            _pressWasSelected = _selected.Contains(t);
            if (ctrl)
            {
                if (_pressWasSelected) _selected.Remove(t); else _selected.Add(t);
                PaintAllCards();
            }
            else if (!_pressWasSelected)
            {
                _selected.Clear();
                _selected.Add(t);
                PaintAllCards();
            }
            _press = 1;
            _pressTask = t;
            _pressCtrl = ctrl;
            _pressPt = e.GetPosition(_inner);
            CaptureMouse();
        }

        void LaneDown(object sender, MouseButtonEventArgs e)
        {
            if (_animating || _dropping || _press != 0) return;
            e.Handled = true;
            HideTip();
            if (_editing != null) Keyboard.ClearFocus();
            bool ctrl = CtrlDown();
            _marqueeBase = ctrl ? new HashSet<TaskItem>(_selected) : new HashSet<TaskItem>();
            if (!ctrl && _selected.Count > 0) { _selected.Clear(); PaintAllCards(); }
            _press = 2;
            _marquee = false;
            _pressPt = e.GetPosition(_inner);
            CaptureMouse();
        }

        void OnWinMouseMove(object sender, MouseEventArgs e)
        {
            if (_press == 0 || _dropping) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndPress(false); return; }
            var pos = e.GetPosition(_inner);
            _lastPt = pos;
            if (_press == 1)
            {
                if (!_dragging)
                {
                    if (Math.Abs(pos.X - _pressPt.X) < 4 && Math.Abs(pos.Y - _pressPt.Y) < 4) return;
                    if (!_selected.Contains(_pressTask)) { EndPress(false); return; }   // Ctrl 点击取消选中后不拖动
                    BeginDrag();
                }
                UpdateDrag(pos);
            }
            else if (_press == 2) UpdateMarquee(pos);
        }

        void OnWinMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_press == 0) return;
            EndPress(true);
        }

        void OnWinLostCapture(object sender, MouseEventArgs e)
        {
            if (_press != 0) EndPress(false);
        }

        void EndPress(bool commit)
        {
            int press = _press;
            _press = 0;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (press == 1)
            {
                if (_dragging)
                {
                    if (commit) DropCards(); else FinishDrop(false);
                }
                else if (commit && !_pressCtrl && _pressWasSelected && _selected.Count > 1)
                {
                    // 在多选中单击某张卡片：收窄为只选它
                    _selected.Clear();
                    _selected.Add(_pressTask);
                    PaintAllCards();
                }
            }
            else if (press == 2)
            {
                if (_marqueeRect != null) _overlay.Children.Remove(_marqueeRect);
                _marqueeRect = null;
                _marquee = false;
            }
        }

        void OnWinKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (_dragging && !_dropping)
            {
                e.Handled = true;
                _press = 0;
                if (IsMouseCaptured) ReleaseMouseCapture();
                FinishDrop(false);
            }
            else if (_editing == null && _selected.Count > 0)
            {
                e.Handled = true;
                _selected.Clear();
                PaintAllCards();
            }
        }

        void UpdateMarquee(Point pos)
        {
            if (!_marquee)
            {
                if (Math.Abs(pos.X - _pressPt.X) < 3 && Math.Abs(pos.Y - _pressPt.Y) < 3) return;
                _marquee = true;
                _marqueeRect = new Rectangle { Fill = Th.Tint(Th.Accent, 0x18), Stroke = Th.Tint(Th.Accent, 0x99), StrokeThickness = 1, RadiusX = 3, RadiusY = 3 };
                _overlay.Children.Add(_marqueeRect);
            }
            var r = new Rect(_pressPt, pos);
            Canvas.SetLeft(_marqueeRect, r.X);
            Canvas.SetTop(_marqueeRect, r.Y);
            _marqueeRect.Width = r.Width;
            _marqueeRect.Height = r.Height;

            var vps = new Rect[3];
            for (int i = 0; i < 3; i++) vps[i] = BoundsIn(_scrolls[i]);
            var sel = new HashSet<TaskItem>(_marqueeBase);
            foreach (var kv in _cards)
            {
                var card = kv.Value.Card;
                if (!card.IsVisible) continue;
                Rect b = BoundsIn(card);
                b.Intersect(vps[Norm(kv.Key.status)]);   // 滚动到可视区外的卡片不算
                if (!b.IsEmpty && b.IntersectsWith(r)) sel.Add(kv.Key);
            }
            if (!sel.SetEquals(_selected))
            {
                _selected = sel;
                PaintAllCards();
            }
        }

        Rect BoundsIn(FrameworkElement el)
        {
            return el.TransformToVisual(_inner).TransformBounds(new Rect(el.RenderSize));
        }

        void BeginDrag()
        {
            _dragging = true;
            HideTip();
            _dragItems = SelectedInOrder();
            var grabbed = _cards[_pressTask];

            // 先把悬停展开的文字收回，再截图
            foreach (var t in _dragItems)
            {
                var ci = _cards[t];
                ci.Hover = false;
                ci.Text.MaxHeight = 36;
                ci.Text.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            _main.UpdateLayout();

            _grabOffset = _inner.TranslatePoint(_pressPt, grabbed.Card);
            _phHeight = grabbed.Card.ActualHeight;

            var shots = new List<FrameworkElement> { grabbed.Card };
            foreach (var t in _dragItems)
            {
                if (shots.Count >= 3) break;
                if (t != _pressTask) shots.Add(_cards[t].Card);
            }
            _ghost = BuildGhost(shots, _dragItems.Count);
            _overlay.Children.Add(_ghost);

            foreach (var t in _dragItems) _cards[t].Card.Visibility = Visibility.Collapsed;
            _phCol = -1; _phIdx = -1;
            _scrollTimer.Start();
        }

        FrameworkElement BuildGhost(List<FrameworkElement> cards, int total)
        {
            var root = new Grid { IsHitTestVisible = false, Opacity = 0.97 };
            for (int i = cards.Count - 1; i >= 0; i--)
            {
                var c = cards[i];
                var img = new Image
                {
                    Source = Snapshot(c),
                    Width = c.ActualWidth,
                    Height = c.ActualHeight,
                    Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(i * 5, i * 5, 0, 0),
                    Opacity = i == 0 ? 1 : 0.7
                };
                root.Children.Add(img);
            }
            root.Effect = Th.Shadow(14, 4, 0.22);
            if (total > 1)
            {
                var badge = new Border
                {
                    Background = Th.AccentBrush,
                    CornerRadius = new CornerRadius(10),
                    MinWidth = 20,
                    Height = 20,
                    Padding = new Thickness(6, 0, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -8, -8, 0),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1.5),
                    Child = new TextBlock { Text = total.ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                root.Children.Add(badge);
            }
            return root;
        }

        BitmapSource Snapshot(FrameworkElement el)
        {
            double s = DpiScale();
            int w = Math.Max(1, (int)Math.Ceiling(el.ActualWidth * s));
            int h = Math.Max(1, (int)Math.Ceiling(el.ActualHeight * s));
            var rtb = new RenderTargetBitmap(w, h, 96 * s, 96 * s, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        int LaneAt(Point pos)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                Rect b = BoundsIn(_lanes[i]);
                if (pos.X >= b.Left && pos.X <= b.Right) return i;
                double d = Math.Abs(pos.X - (b.Left + b.Width / 2));
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        void UpdateDrag(Point pos)
        {
            if (_ghost == null) return;
            Canvas.SetLeft(_ghost, Math.Round(pos.X - _grabOffset.X));
            Canvas.SetTop(_ghost, Math.Round(pos.Y - _grabOffset.Y));

            int col = LaneAt(pos);
            var panel = _colPanels[col];
            Point pp = _inner.TranslatePoint(pos, panel);
            // 以“没有占位块时”的布局计算落点，避免占位块移动引起抖动
            int idx = 0; double shift = 0;
            foreach (UIElement ch in panel.Children)
            {
                var fe = ch as FrameworkElement;
                if (fe == null) continue;
                if (IsPlaceholder(fe)) { shift += fe.ActualHeight + fe.Margin.Bottom; continue; }
                if (!(fe.Tag is CardInfo) || fe.Visibility != Visibility.Visible) continue;
                double mid = fe.TranslatePoint(new Point(0, fe.ActualHeight / 2), panel).Y - shift;
                if (pp.Y > mid) idx++;
            }
            if (col != _phCol || idx != _phIdx) MovePlaceholder(col, idx);
        }

        static bool IsPlaceholder(FrameworkElement fe)
        {
            return fe.Tag as string == "ph";
        }

        void MovePlaceholder(int col, int idx)
        {
            if (_ph != null)
            {
                var old = _ph;
                var shrink = new DoubleAnimation(old.ActualHeight, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                shrink.Completed += delegate
                {
                    var p = old.Parent as Panel;
                    if (p != null) p.Children.Remove(old);
                };
                old.BeginAnimation(HeightProperty, shrink);
            }
            _ph = new Rectangle
            {
                Tag = "ph",
                Height = 0,
                Margin = new Thickness(0, 0, 0, 6),
                RadiusX = 8,
                RadiusY = 8,
                Fill = Th.Tint(Th.Accent, 0x12),
                Stroke = Th.Tint(Th.Accent, 0x80),
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false
            };
            var panel = _colPanels[col];
            int insertAt = panel.Children.Count, seen = 0;
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var fe = panel.Children[i] as FrameworkElement;
                if (fe == null || !(fe.Tag is CardInfo) || fe.Visibility != Visibility.Visible) continue;
                if (seen == idx) { insertAt = i; break; }
                seen++;
            }
            panel.Children.Insert(insertAt, _ph);
            _ph.BeginAnimation(HeightProperty, new DoubleAnimation(0, _phHeight, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _phCol = col;
            _phIdx = idx;
        }

        bool AutoScroll(Point pos)
        {
            if (_phCol < 0) return false;
            var sv = _scrolls[_phCol];
            Rect b = BoundsIn(sv);
            double off = sv.VerticalOffset;
            if (pos.Y < b.Top + 28) sv.ScrollToVerticalOffset(off - 9);
            else if (pos.Y > b.Bottom - 28) sv.ScrollToVerticalOffset(off + 9);
            else return false;
            sv.UpdateLayout();
            return Math.Abs(sv.VerticalOffset - off) > 0.1;
        }

        void DropCards()
        {
            if (_ph == null || _ghost == null) { FinishDrop(false); return; }
            _dropping = true;
            _scrollTimer.Stop();
            // 幽灵卡片飞向落点后再提交
            Point target;
            try { target = _ph.TranslatePoint(new Point(0, 0), _inner); }
            catch { FinishDrop(true); return; }
            double x0 = Canvas.GetLeft(_ghost), y0 = Canvas.GetTop(_ghost);
            var ghost = _ghost;
            Tween.Run(150, Th.OutCubic, delegate(double t)
            {
                Canvas.SetLeft(ghost, Th.Lerp(x0, target.X, t));
                Canvas.SetTop(ghost, Th.Lerp(y0, target.Y, t));
            }, delegate { FinishDrop(true); });
        }

        void FinishDrop(bool commit)
        {
            _scrollTimer.Stop();
            if (_ghost != null) _overlay.Children.Remove(_ghost);
            _ghost = null;
            var items = _dragItems;
            int col = _phCol, idx = _phIdx;
            _ph = null; _phCol = -1; _phIdx = -1;
            _dragItems = null;
            _dragging = false;
            _dropping = false;
            if (commit && items != null && col >= 0) CommitDrop(items, col, idx);
            else Refresh(false);
        }

        static int Norm(int s) { return s < 0 ? 0 : (s > 2 ? 2 : s); }

        void CommitDrop(List<TaskItem> items, int col, int idx)
        {
            foreach (var it in items) _tasks.Remove(it);
            int seen = 0, insertAt = -1, lastOfCol = -1;
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (Norm(_tasks[i].status) != col) continue;
                if (seen == idx) { insertAt = i; break; }
                seen++;
                lastOfCol = i;
            }
            if (insertAt < 0) insertAt = lastOfCol >= 0 ? lastOfCol + 1 : _tasks.Count;
            foreach (var it in items)
            {
                if (col == 2 && it.status != 2) it.completedAt = NowIso();
                it.status = col;
            }
            _tasks.InsertRange(insertAt, items);
            Refresh();
        }

        // ================= 数据操作 =================

        void Refresh() { Refresh(true); }

        void Refresh(bool save)
        {
            if (_colPanels == null) return;
            _selected.RemoveWhere(delegate(TaskItem t) { return !_tasks.Contains(t); });
            _cards.Clear();
            int[] counts = new int[3];
            for (int i = 0; i < 3; i++) _colPanels[i].Children.Clear();
            foreach (TaskItem t in _tasks)
            {
                int s = Norm(t.status);
                var card = MakeCard(t);
                _colPanels[s].Children.Add(card);
                _cards[t] = (CardInfo)card.Tag;
                counts[s]++;
            }
            for (int i = 0; i < 3; i++)
            {
                _countTexts[i].Text = counts[i].ToString();
                _pillCounts[i].Text = counts[i].ToString();
                if (counts[i] == 0)
                    _colPanels[i].Children.Add(new TextBlock { Text = L.T("empty"), Foreground = Th.Faint, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), IsHitTestVisible = false });
            }
            _dateText.Text = L.TodayTitle(DateTime.Today);
            if (_expanded) MeasurePill();
            if (save) SaveSoon();
        }

        void AddTask()
        {
            string s = _input.Text.Trim();
            if (s.Length == 0) { _input.Focus(); return; }
            var t = new TaskItem { id = NewId(), title = s, status = _addTarget, createdAt = NowIso() };
            if (_addTarget == 2) t.completedAt = t.createdAt;
            _tasks.Add(t);
            _input.Text = "";
            Refresh();
            var sv = _scrolls[_addTarget];
            Dispatcher.BeginInvoke(new Action(delegate { sv.ScrollToEnd(); }), DispatcherPriority.Loaded);
            _input.Focus();
        }

        void PinToTop(List<TaskItem> items)
        {
            foreach (var it in items) _tasks.Remove(it);
            for (int k = items.Count - 1; k >= 0; k--)
            {
                var t = items[k];
                int insertAt = _tasks.Count;
                for (int i = 0; i < _tasks.Count; i++)
                    if (Norm(_tasks[i].status) == Norm(t.status)) { insertAt = i; break; }
                _tasks.Insert(insertAt, t);
            }
            Refresh();
        }

        void DeleteTasks(List<TaskItem> items)
        {
            var before = new List<TaskItem>(_tasks);
            foreach (var it in items) _tasks.Remove(it);
            Refresh();
            Toast(L.F("toast.deleted", items.Count), L.T("undo"), delegate { _tasks = before; Refresh(); });
        }

        // 归档：已完成任务移出看板，记录保留在 archive 里供导出
        void ArchiveDone()
        {
            var done = new List<TaskItem>();
            foreach (var t in _tasks) if (t.status == 2) done.Add(t);
            if (done.Count == 0) return;
            var before = new List<TaskItem>(_tasks);
            var beforeArc = new List<TaskItem>(_archive);
            _archive.AddRange(done);
            _tasks.RemoveAll(delegate(TaskItem t) { return t.status == 2; });
            Refresh();
            Toast(L.F("toast.archived", done.Count), L.T("undo"), delegate { _tasks = before; _archive = beforeArc; Refresh(); });
        }

        // ================= 托盘 =================

        void SetupTray()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    Icon = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    _tray = new System.Windows.Forms.NotifyIcon();
                    _tray.Icon = (System.Drawing.Icon)ic.Clone();
                    _tray.Visible = true;
                    var strip = new System.Windows.Forms.ContextMenuStrip();
                    _trayToggleItem = new System.Windows.Forms.ToolStripMenuItem();
                    _trayToggleItem.Click += delegate { ToggleFromTray(); };
                    _trayPinItem = new System.Windows.Forms.ToolStripMenuItem();
                    _trayPinItem.Checked = _topmost;
                    _trayPinItem.Click += delegate { TogglePin(); };
                    _trayQuitItem = new System.Windows.Forms.ToolStripMenuItem();
                    _trayQuitItem.Click += delegate { Close(); };
                    strip.Items.Add(_trayToggleItem);
                    strip.Items.Add(_trayPinItem);
                    strip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                    strip.Items.Add(_trayQuitItem);
                    _tray.ContextMenuStrip = strip;
                    _tray.DoubleClick += delegate { ToggleFromTray(); };
                    UpdateTrayTexts();
                }
            }
            catch { }
        }

        void UpdateTrayTexts()
        {
            if (_tray == null) return;
            _tray.Text = L.T("app.title");
            _trayToggleItem.Text = L.T("tray.toggle");
            _trayPinItem.Text = L.T("tray.pin");
            _trayQuitItem.Text = L.T("tray.quit");
        }

        void ToggleFromTray()
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        }

        // ================= 窗口几何、DPI、屏幕 =================

        double DpiScale()
        {
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
        }

        List<Rect> WorkAreas()
        {
            double s = DpiScale();
            var list = new List<Rect>();
            foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            {
                var w = sc.WorkingArea;
                list.Add(new Rect(w.X / s, w.Y / s, w.Width / s, w.Height / s));
            }
            if (list.Count == 0) list.Add(SystemParameters.WorkArea);
            return list;
        }

        Rect WorkAreaFor(Rect r)
        {
            var areas = WorkAreas();
            Rect best = areas[0]; double bestArea = -1;
            foreach (var a in areas)
            {
                var i = Rect.Intersect(a, r);
                double ar = i.IsEmpty ? 0 : i.Width * i.Height;
                if (ar > bestArea) { bestArea = ar; best = a; }
            }
            if (bestArea > 0) return best;
            Point c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            double bd = double.MaxValue;
            foreach (var a in areas)
            {
                double dx = Math.Max(a.Left - c.X, Math.Max(0, c.X - a.Right));
                double dy = Math.Max(a.Top - c.Y, Math.Max(0, c.Y - a.Bottom));
                double d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        Rect FitIntoWorkArea(Rect r)
        {
            var wa = WorkAreaFor(r);
            double w = Math.Min(Math.Max(r.Width, MinW), wa.Width - 8);
            double h = Math.Min(Math.Max(r.Height, MinH), wa.Height - 8);
            double x = Math.Max(wa.Left + 4, Math.Min(r.X, wa.Right - w - 4));
            double y = Math.Max(wa.Top + 4, Math.Min(r.Y, wa.Bottom - h - 4));
            return new Rect(x, y, w, h);
        }

        Rect DefaultERect()
        {
            var wa = SystemParameters.WorkArea;
            return new Rect(wa.Right - 480 - 28, wa.Top + 90, 480, 580);
        }

        Point ClampPillPos(Point p, Rect wa, int dock)
        {
            double x = Math.Max(wa.Left, Math.Min(p.X, wa.Right - _pillW));
            double y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - PillH));
            if (dock == 1) x = wa.Left;
            else if (dock == 2) x = wa.Right - _pillW;
            else if (dock == 3) y = wa.Top;
            else if (dock == 4) y = wa.Bottom - PillH;
            return new Point(x, y);
        }

        void SetShellSize(double w, double h, double r)
        {
            _shell.Width = w; _shell.Height = h;
            _shadow.Width = w; _shadow.Height = h;
            var cr = new CornerRadius(r);
            _shell.CornerRadius = cr;
            _shadow.CornerRadius = cr;
            _clipGeo.Rect = new Rect(0, 0, Math.Max(0, w - 2), Math.Max(0, h - 2));
            _clipGeo.RadiusX = _clipGeo.RadiusY = Math.Max(0, r - 1);
        }

        // 用一次 SetWindowPos 同时改 X/Y，避免分别设置 Left/Top 造成的阶梯抖动
        void MoveWindowTo(double x, double y)
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            if (src == null) { Left = x; Top = y; return; }
            double s = DpiScale();
            Native.SetWindowPos(src.Handle, IntPtr.Zero, (int)Math.Round(x * s), (int)Math.Round(y * s), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // 动画期间冻结主内容尺寸：内容不重排，只被收缩的外框裁切
        void FreezeMain(Rect e)
        {
            _main.Width = Math.Max(0, e.Width - 2 - 28);
            _main.Height = Math.Max(0, e.Height - 2 - 22);
            _main.HorizontalAlignment = HorizontalAlignment.Left;
            _main.VerticalAlignment = VerticalAlignment.Top;
        }

        void UnfreezeMain()
        {
            _main.Width = double.NaN;
            _main.Height = double.NaN;
            _main.HorizontalAlignment = HorizontalAlignment.Stretch;
            _main.VerticalAlignment = VerticalAlignment.Stretch;
        }

        void MeasurePill()
        {
            _pillStack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _pillW = Math.Max(160, Math.Ceiling(_pillStack.DesiredSize.Width + 32));
            _pillContent.Width = _pillW - 2;
            _pillContent.Height = PillH - 2;
        }

        void SyncGeometry()
        {
            if (_animating || _shell == null) return;
            if (_expanded) _eRect = new Rect(Left + M, Top + M, _shell.Width, _shell.Height);
            else { _pPos = new Point(Left + M, Top + M); _hasPill = true; }
        }

        bool RectOnScreen(Rect r)
        {
            foreach (var a in WorkAreas())
            {
                var i = Rect.Intersect(a, r);
                if (!i.IsEmpty && i.Width >= 60 && i.Height >= 30) return true;
            }
            return false;
        }

        void ApplyInitialGeometry()
        {
            if (_eRect.IsEmpty || !RectOnScreen(_eRect)) _eRect = DefaultERect();
            _eRect = FitIntoWorkArea(_eRect);
            MeasurePill();
            if (_expanded)
            {
                SetShellSize(_eRect.Width, _eRect.Height, RExp);
                Left = _eRect.X - M; Top = _eRect.Y - M;
                Width = _eRect.Width + 2 * M; Height = _eRect.Height + 2 * M;
                _main.Visibility = Visibility.Visible;
                _pillContent.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (!_hasPill || !RectOnScreen(new Rect(_pPos, new Size(_pillW, PillH))))
                {
                    _pPos = new Point(_eRect.Right - _pillW, _eRect.Top);
                    _dock = 0;
                }
                var wa = WorkAreaFor(new Rect(_pPos, new Size(_pillW, PillH)));
                _pPos = ClampPillPos(_pPos, wa, _dock);
                _hasPill = true;
                SetShellSize(_pillW, PillH, PillH / 2);
                Left = _pPos.X - M; Top = _pPos.Y - M;
                Width = _pillW + 2 * M; Height = PillH + 2 * M;
                _main.Visibility = Visibility.Collapsed;
                _pillContent.Visibility = Visibility.Visible;
                _pillContent.Opacity = 1;
                if (_dock != 0) DockIn(false);
            }
            _host.Opacity = OpLevels[_opacityIndex];
        }

        // ================= 收起 / 展开（单一外框形变 + 窗口滑移） =================

        void CancelInteractions()
        {
            HideTip();
            HideToast();
            if (_press != 0) EndPress(false);
            if (_dragging) FinishDrop(false);
            if (_editing != null) Keyboard.ClearFocus();
        }

        void Collapse()
        {
            if (!_expanded || _animating) return;
            CancelInteractions();
            SyncGeometry();
            Rect E = _eRect;
            MeasurePill();
            Point target;
            if (_hasPill)
            {
                var wa = WorkAreaFor(new Rect(_pPos, new Size(_pillW, PillH)));
                target = ClampPillPos(_pPos, wa, _dock);
            }
            else
            {
                _dock = 0;
                target = ClampPillPos(new Point(E.Right - _pillW, E.Top), WorkAreaFor(E), 0);
            }

            _expanded = false;
            _animating = true;
            FreezeMain(E);
            _pillContent.Visibility = Visibility.Visible;
            _pillContent.Opacity = 0;
            double x0 = Left, y0 = Top, x1 = target.X - M, y1 = target.Y - M;
            double w0 = E.Width, h0 = E.Height, pw = _pillW;

            _morph = Tween.Run(380, Th.InOutCubic, delegate(double t)
            {
                SetShellSize(Th.Lerp(w0, pw, t), Th.Lerp(h0, PillH, t), Th.Lerp(RExp, PillH / 2, t));
                MoveWindowTo(Th.Lerp(x0, x1, t), Th.Lerp(y0, y1, t));
                _main.Opacity = 1 - Th.Clamp01(t / 0.55);
                _pillContent.Opacity = Th.Clamp01((t - 0.45) / 0.55);
            }, delegate
            {
                _main.Visibility = Visibility.Collapsed;
                UnfreezeMain();
                _main.Opacity = 1;
                _pillContent.Opacity = 1;
                Width = pw + 2 * M;        // 左上角不变，只缩小窗口：画面不会错位
                Height = PillH + 2 * M;
                Left = x1; Top = y1;
                _pPos = target;
                _hasPill = true;
                _animating = false;
                if (_dock != 0) DockIn(true);
                SaveSoon();
            });
        }

        void Expand()
        {
            if (_expanded || _animating) return;
            StopDock();
            Rect E = FitIntoWorkArea(_eRect);
            _animating = true;
            _expanded = true;
            double x0 = Left, y0 = Top, x1 = E.X - M, y1 = E.Y - M, pw = _pillW;
            // 先在原左上角把窗口放大（胶囊仍在左上角，画面不变），再边形变边滑到目标位置
            Width = E.Width + 2 * M;
            Height = E.Height + 2 * M;
            FreezeMain(E);
            _main.Visibility = Visibility.Visible;
            _main.Opacity = 0;

            _morph = Tween.Run(420, Th.OutQuart, delegate(double t)
            {
                SetShellSize(Th.Lerp(pw, E.Width, t), Th.Lerp(PillH, E.Height, t), Th.Lerp(PillH / 2, RExp, t));
                MoveWindowTo(Th.Lerp(x0, x1, t), Th.Lerp(y0, y1, t));
                _pillContent.Opacity = 1 - Th.Clamp01(t / 0.3);
                _main.Opacity = Th.Clamp01((t - 0.12) / 0.6);
            }, delegate
            {
                _pillContent.Visibility = Visibility.Collapsed;
                _pillContent.Opacity = 1;
                UnfreezeMain();
                _main.Opacity = 1;
                Left = x1; Top = y1;
                _eRect = E;
                _animating = false;
                SaveSoon();
            });
        }

        void Glide(Point win, int ms, Action done)
        {
            if (_glide != null) _glide.Stop();
            double x0 = Left, y0 = Top;
            _animating = true;
            _glide = Tween.Run(ms, Th.OutCubic, delegate(double t)
            {
                MoveWindowTo(Th.Lerp(x0, win.X, t), Th.Lerp(y0, win.Y, t));
            }, delegate
            {
                Left = win.X; Top = win.Y;
                _animating = false;
                if (done != null) done();
            });
        }

        // ================= 胶囊拖动与贴边隐藏 =================

        void PillDown(object sender, MouseButtonEventArgs e)
        {
            if (_animating || _expanded) return;
            e.Handled = true;
            int wasDock = _dock;
            StopDock();
            _dock = 0;
            double l0 = Left, t0 = Top;
            try { DragMove(); } catch { }
            if (Math.Abs(Left - l0) < 1 && Math.Abs(Top - t0) < 1)
            {
                _dock = wasDock;       // 单击展开：保留贴边记忆，下次收起回到原处
                Expand();
            }
            else SnapPill();
        }

        void SnapPill()
        {
            var pill = new Rect(Left + M, Top + M, _pillW, PillH);
            var wa = WorkAreaFor(pill);
            const double T = 16;
            int dock = 0;
            if (pill.Left <= wa.Left + T) dock = 1;
            else if (pill.Right >= wa.Right - T) dock = 2;
            else if (pill.Top <= wa.Top + T) dock = 3;
            else if (pill.Bottom >= wa.Bottom - T) dock = 4;
            Point target = ClampPillPos(pill.TopLeft, wa, dock);
            _dock = dock;
            _pPos = target;
            _hasPill = true;
            Glide(new Point(target.X - M, target.Y - M), 170, delegate
            {
                if (_dock != 0 && !_host.IsMouseOver) DockIn(true);
                else if (_dock != 0) ApplyDockClip();
            });
            SaveSoon();
        }

        void StopDock()
        {
            _dockTimer.Stop();
            if (_dockTween != null) _dockTween.Stop();
            _dockTx.X = 0; _dockTx.Y = 0;
            _host.Clip = null;
            _dockHandle.Opacity = 0;
            _peeking = false;
        }

        // 只保留贴边一侧之外的区域可见，防止滑出的胶囊画到相邻显示器上
        void ApplyDockClip()
        {
            double W = _pillW + 2 * M, H = PillH + 2 * M;
            Rect clip;
            if (_dock == 1) clip = new Rect(M, 0, W - M, H);
            else if (_dock == 2) clip = new Rect(0, 0, W - M, H);
            else if (_dock == 3) clip = new Rect(0, M, W, H - M);
            else clip = new Rect(0, 0, W, H - M);
            _host.Clip = new RectangleGeometry(clip);

            bool vertical = _dock == 1 || _dock == 2;
            _dockHandle.Width = vertical ? 3 : 26;
            _dockHandle.Height = vertical ? 22 : 3;
            double hx, hy;
            if (_dock == 1) { hx = M + 1; hy = M + (PillH - 22) / 2; }
            else if (_dock == 2) { hx = M + _pillW - 4; hy = M + (PillH - 22) / 2; }
            else if (_dock == 3) { hx = M + (_pillW - 26) / 2; hy = M + 1; }
            else { hx = M + (_pillW - 26) / 2; hy = M + PillH - 4; }
            _dockHandle.Margin = new Thickness(hx, hy, 0, 0);
        }

        void DockIn(bool animate)
        {
            if (_dock == 0 || _expanded) return;
            _peeking = false;
            ApplyDockClip();
            double tx = 0, ty = 0;
            if (_dock == 1) tx = -(_pillW - Sliver);
            else if (_dock == 2) tx = _pillW - Sliver;
            else if (_dock == 3) ty = -(PillH - Sliver);
            else ty = PillH - Sliver;
            AnimateDock(tx, ty, 1, animate ? 260 : 0);
        }

        void DockPeek()
        {
            if (_dock == 0 || _expanded) return;
            _peeking = true;
            AnimateDock(0, 0, 0, 200);
        }

        void AnimateDock(double tx, double ty, double handle, int ms)
        {
            if (_dockTween != null) _dockTween.Stop();
            double x0 = _dockTx.X, y0 = _dockTx.Y, h0 = _dockHandle.Opacity;
            _dockTween = Tween.Run(ms, Th.OutCubic, delegate(double t)
            {
                _dockTx.X = Th.Lerp(x0, tx, t);
                _dockTx.Y = Th.Lerp(y0, ty, t);
                _dockHandle.Opacity = Th.Lerp(h0, handle, t);
            }, null);
        }

        void HostEnter(object sender, MouseEventArgs e)
        {
            if (_expanded || _animating || _dock == 0) return;
            _dockTimer.Stop();
            if (!_peeking) DockPeek();
        }

        void HostLeave(object sender, MouseEventArgs e)
        {
            if (_expanded || _dock == 0) return;
            _dockTimer.Stop();
            _dockTimer.Start();
        }

        // ================= 标题栏按钮 =================

        void TogglePin()
        {
            _topmost = !_topmost;
            Topmost = _topmost;
            _pinBtn.Active = _topmost;
            if (_trayPinItem != null) _trayPinItem.Checked = _topmost;
            SaveSoon();
        }

        void ShowOpacityMenu()
        {
            var m = Menus.New();
            m.Items.Add(Menus.Head(L.T("tip.opacity")));
            for (int i = 0; i < OpLevels.Length; i++)
            {
                int idx = i;
                m.Items.Add(Menus.Item(Math.Round(OpLevels[i] * 100) + "%", null, i == _opacityIndex, false, delegate
                {
                    _opacityIndex = idx;
                    _host.Opacity = OpLevels[idx];
                    SaveSoon();
                }));
            }
            Menus.OpenBelow(m, _opacityBtn.Root);
        }

        void ShowTargetMenu()
        {
            var m = Menus.New();
            m.Items.Add(Menus.Head(L.T("menu.addTo")));
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var mi = Menus.Item(L.T("col" + i), null, false, false, delegate
                {
                    _addTarget = idx;
                    UpdateTargetBtn();
                    SaveSoon();
                    _input.Focus();
                });
                mi.Icon = i == _addTarget ? (UIElement)Th.Icon(Th.GCheck, 12, Th.Solid(Th.Lane[i])) : new Ellipse { Width = 8, Height = 8, Fill = Th.Solid(Th.Lane[i]), HorizontalAlignment = HorizontalAlignment.Center };
                m.Items.Add(mi);
            }
            Menus.OpenBelow(m, _targetBtn);
        }

        void ShowLanguageMenu()
        {
            var m = Menus.New();
            m.Items.Add(Menus.Head(L.T("m.langHead")));
            for (int i = 0; i < L.Codes.Length; i++)
            {
                int idx = i;
                var head = new DockPanel();
                var code = new TextBlock { Text = L.Codes[i], Foreground = Th.Faint, FontSize = 11.5, Margin = new Thickness(16, 0, 0, 0), FontFamily = new FontFamily("Segoe UI") };
                DockPanel.SetDock(code, Dock.Right);
                head.Children.Add(code);
                head.Children.Add(new TextBlock { Text = L.Names[i], FontFamily = i == 2 ? new FontFamily("Segoe UI") : (i == 0 ? new FontFamily("Microsoft JhengHei UI") : new FontFamily("Microsoft YaHei UI")) });
                m.Items.Add(Menus.Item(head, null, i == L.Lang, false, delegate { SetLanguage(idx); }));
            }
            Menus.OpenBelow(m, _langBtn.Root);
        }

        // 切换语言：界面全部由代码生成，直接重建一遍即可
        void SetLanguage(int lang)
        {
            if (lang == L.Lang) return;
            CancelInteractions();
            L.Lang = lang;
            ApplyLanguageBasics();
            string pending = _input != null ? _input.Text : "";
            BuildUi();
            ApplyBackgroundColor(_bgColor, false);
            Refresh(false);
            _input.Text = pending;
            SetShellSize(Width - 2 * M, Height - 2 * M, RExp);   // 语言按钮只在展开状态可用
            _host.Opacity = OpLevels[_opacityIndex];
            UpdateTrayTexts();
            SaveSoon();
        }

        // ================= 导出 =================

        static DateTime StartOfWeek()
        {
            var d = DateTime.Today;
            return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        }

        List<TaskItem> CollectDone(DateTime from, DateTime to)
        {
            DateTime end = to.Date.AddDays(1);
            var rows = new List<TaskItem>();
            var seen = new HashSet<TaskItem>();
            foreach (var src in new[] { _tasks, _archive })
            {
                foreach (TaskItem t in src)
                {
                    if (t.status != 2 || !seen.Add(t)) continue;
                    DateTime cd = ParseTime(t.completedAt);
                    if (cd >= from.Date && cd < end) rows.Add(t);
                }
            }
            rows.Sort(delegate(TaskItem a, TaskItem b) { return ParseTime(a.completedAt).CompareTo(ParseTime(b.completedAt)); });
            return rows;
        }

        int CountDone(DateTime from, DateTime to) { return CollectDone(from, to).Count; }

        void ShowExportMenu()
        {
            var m = Menus.New();
            m.Items.Add(Menus.Head(L.T("m.exportHead")));
            DateTime ws = StartOfWeek();
            DateTime ms = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            DateTime qs = new DateTime(ms.Year, (ms.Month - 1) / 3 * 3 + 1, 1);
            AddRangeItem(m, L.T("r.week"), ws, ws.AddDays(6));
            AddRangeItem(m, L.T("r.lastWeek"), ws.AddDays(-7), ws.AddDays(-1));
            AddRangeItem(m, L.T("r.month"), ms, ms.AddMonths(1).AddDays(-1));
            AddRangeItem(m, L.T("r.lastMonth"), ms.AddMonths(-1), ms.AddDays(-1));
            AddRangeItem(m, L.T("r.quarter"), qs, qs.AddMonths(3).AddDays(-1));
            m.Items.Add(Menus.Sep());
            m.Items.Add(Menus.Item(L.T("r.custom"), Th.GCalendar, false, false, delegate
            {
                var dlg = new ExportDialog(CountDone);
                dlg.Owner = this;
                if (dlg.ShowDialog() == true) ExportRange(dlg.FromDate, dlg.ToDate);
            }));
            Menus.OpenBelow(m, _exportBtn.Root);
        }

        void AddRangeItem(ContextMenu m, string label, DateTime from, DateTime to)
        {
            int n = CountDone(from, to);
            var dp = new DockPanel { MinWidth = 220 };
            var cnt = new TextBlock { Text = L.F("count", n), Foreground = n > 0 ? Th.AccentBrush : Th.Faint, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cnt, Dock.Right);
            dp.Children.Add(cnt);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = label, MinWidth = L.Lang == 2 ? 86 : 50, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = string.Format(CultureInfo.InvariantCulture, "{0:M/d} – {1:M/d}", from, to), Foreground = Th.Faint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            dp.Children.Add(sp);
            var mi = Menus.Item(dp, Th.GExport, false, false, delegate { ExportRange(from, to); });
            mi.IsEnabled = n > 0;
            m.Items.Add(mi);
        }

        void ExportRange(DateTime from, DateTime to)
        {
            var rows = CollectDone(from, to);
            if (rows.Count == 0) { Toast(L.T("toast.none"), null, null); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = string.Format(CultureInfo.InvariantCulture, L.T("csv.file"), from, to);
            dlg.Filter = L.T("csv.filter");
            dlg.DefaultExt = ".csv";
            dlg.InitialDirectory = !string.IsNullOrEmpty(_exportDir) && Directory.Exists(_exportDir)
                ? _exportDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (dlg.ShowDialog(this) != true) return;

            var sb = new StringBuilder();
            sb.AppendLine(L.T("csv.head"));
            foreach (TaskItem t in rows)
            {
                DateTime cd = ParseTime(t.completedAt);
                DateTime cr = ParseTime(t.createdAt);
                string days = "";
                if (cr != DateTime.MinValue && cd >= cr)
                {
                    double dd = (cd - cr).TotalDays;
                    days = dd < 1 ? "0" : Math.Round(dd, 1).ToString(CultureInfo.InvariantCulture);
                }
                string title = (t.title ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
                string created = cr == DateTime.MinValue ? "" : cr.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm},\"{1}\",{2},{3}", cd, title, created, days));
            }
            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 直接打开不乱码
            }
            catch
            {
                Toast(L.T("toast.exportFail"), null, null);
                return;
            }
            _exportDir = System.IO.Path.GetDirectoryName(dlg.FileName);
            SaveSoon();
            string path = dlg.FileName;
            Toast(L.F("toast.exported", rows.Count), L.T("openFolder"), delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
            });
        }

        // ================= 背景颜色 =================

        static readonly Color[] Palette = {
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE7, 0xE3, 0xDC), Color.FromRgb(0xEB, 0xE4, 0xD8),
            Color.FromRgb(0xE9, 0xE1, 0xD2), Color.FromRgb(0xE0, 0xCF, 0xCB), Color.FromRgb(0xD9, 0xC7, 0xC7),
            Color.FromRgb(0xCD, 0xB9, 0xAD), Color.FromRgb(0xD8, 0xD0, 0xE0), Color.FromRgb(0xC9, 0xD6, 0xE3),
            Color.FromRgb(0xB0, 0xBF, 0xD1), Color.FromRgb(0xCB, 0xD6, 0xC6), Color.FromRgb(0xB7, 0xC4, 0xB1),
            Color.FromRgb(0xC9, 0xD8, 0xD4)
        };

        void ShowColorMenu()
        {
            var m = Menus.New();
            m.Items.Add(Menus.Head(L.T("m.colorHead")));
            for (int i = 0; i < Palette.Length; i++)
            {
                Color c = Palette[i];
                var head = new DockPanel();
                if (c == _bgColor)
                {
                    var ck = Th.Icon(Th.GCheck, 12, Th.AccentBrush);
                    DockPanel.SetDock(ck, Dock.Right);
                    ck.Margin = new Thickness(16, 0, 0, 0);
                    head.Children.Add(ck);
                }
                head.Children.Add(new TextBlock { Text = L.T("pal" + i) });
                var mi = Menus.Item(head, null, false, false, delegate { ApplyBackgroundColor(c, true); });
                mi.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = Th.Solid(c), BorderBrush = Th.S(0x40, 0x1F, 0x24, 0x30), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center };
                m.Items.Add(mi);
            }
            m.Items.Add(Menus.Sep());
            m.Items.Add(Menus.Item(L.T("m.customColor"), Th.GColor, false, false, delegate
            {
                var dlg = new ColorDialog(_bgColor);
                dlg.Owner = this;
                if (dlg.ShowDialog() == true) ApplyBackgroundColor(dlg.Selected, true);
            }));
            Menus.OpenBelow(m, _colorBtn.Root);
        }

        void ApplyBackgroundColor(Color c, bool refresh)
        {
            _bgColor = c;
            _shellBg.Color = c;
            Color lane = Th.Mix(c, Color.FromRgb(0x5B, 0x66, 0x7A), 0.065);
            _laneBg.Color = lane;
            Color card = Th.Mix(c, Colors.White, 0.8);
            _cardBg = Th.Solid(card);
            _cardDoneBg = Th.Solid(Th.Mix(lane, Colors.White, 0.45));
            _cardSelBg = Th.Solid(Th.Mix(card, Th.Accent, 0.08));
            if (refresh) Refresh();
        }

        // ================= 存取 =================

        static JavaScriptSerializer Ser()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        void SaveSoon()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            try
            {
                SyncGeometry();
                var d = new SaveData
                {
                    v = 2,
                    tasks = _tasks,
                    archive = _archive,
                    ex = _eRect.IsEmpty ? 0 : _eRect.X,
                    ey = _eRect.IsEmpty ? 0 : _eRect.Y,
                    ew = _eRect.IsEmpty ? 0 : _eRect.Width,
                    eh = _eRect.IsEmpty ? 0 : _eRect.Height,
                    px = _pPos.X,
                    py = _pPos.Y,
                    hasPill = _hasPill,
                    dock = _dock,
                    opacity = _opacityIndex,
                    topmost = _topmost,
                    expanded = _expanded,
                    bg = string.Format("#{0:X2}{1:X2}{2:X2}", _bgColor.R, _bgColor.G, _bgColor.B),
                    addTarget = _addTarget,
                    exportDir = _exportDir,
                    lang = L.Codes[L.Lang]
                };
                string json = Ser().Serialize(d);
                Directory.CreateDirectory(_dir);
                // 先写临时文件再替换，写到一半断电也不会毁掉原数据；同时留一份 .bak
                string tmp = _file + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_file))
                {
                    try { File.Replace(tmp, _file, _file + ".bak", true); }
                    catch { File.Copy(tmp, _file, true); File.Delete(tmp); }
                }
                else File.Move(tmp, _file);
            }
            catch { }
        }

        static SaveData TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Ser().Deserialize<SaveData>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        public static int PeekLanguage()
        {
            string f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingKanban", "data.json");
            var d = TryRead(f);
            return d != null ? L.FromCode(d.lang) : 0;
        }

        void LoadState()
        {
            string bak = _file + ".bak";
            SaveData d = TryRead(_file);
            if (d == null && File.Exists(_file))
            {
                // 主文件损坏：保留现场副本，再尝试从备份恢复
                try { File.Copy(_file, System.IO.Path.Combine(_dir, "data.corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json"), true); } catch { }
                d = TryRead(bak);
            }
            if (d == null)
            {
                if (!File.Exists(_file) && !File.Exists(bak)) { L.Lang = 0; Seed(); }
                return;
            }

            L.Lang = L.FromCode(d.lang);
            if (d.tasks != null) _tasks = d.tasks;
            if (d.archive != null) _archive = d.archive;
            DateTime legacy = File.Exists(_file) ? File.GetLastWriteTime(_file) : DateTime.Now;
            string ts = legacy.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var list in new[] { _tasks, _archive })
            {
                list.RemoveAll(delegate(TaskItem t) { return t == null; });
                foreach (TaskItem t in list)
                {
                    if (string.IsNullOrEmpty(t.id)) t.id = NewId();
                    if (string.IsNullOrEmpty(t.createdAt)) t.createdAt = ts;
                    if (t.status == 2 && string.IsNullOrEmpty(t.completedAt)) t.completedAt = ts;
                }
            }

            _opacityIndex = d.opacity;
            if (_opacityIndex < 0 || _opacityIndex >= OpLevels.Length) _opacityIndex = 0;
            _topmost = d.topmost;
            _expanded = d.expanded;
            _addTarget = d.addTarget >= 0 && d.addTarget <= 2 ? d.addTarget : 0;
            _exportDir = d.exportDir;
            _dock = d.dock >= 0 && d.dock <= 4 ? d.dock : 0;

            if (d.v >= 2)
            {
                if (d.ew > 100 && d.eh > 100) _eRect = new Rect(d.ex, d.ey, d.ew, d.eh);
                if (d.hasPill) { _pPos = new Point(d.px, d.py); _hasPill = true; }
            }
            else
            {
                // 旧版：x/y 是当前窗口位置，ex/ey 是展开时位置，px/py 是胶囊位置
                double ex = (d.ex != 0 || d.ey != 0) ? d.ex : d.x;
                double ey = (d.ex != 0 || d.ey != 0) ? d.ey : d.y;
                if (ex != 0 || ey != 0) _eRect = new Rect(ex, ey, d.w > 150 ? d.w : 480, d.h > 150 ? d.h : 580);
                if (d.px != 0 || d.py != 0) { _pPos = new Point(d.px, d.py); _hasPill = true; }
                else if (!d.expanded && (d.x != 0 || d.y != 0)) { _pPos = new Point(d.x, d.y); _hasPill = true; }
                _dock = 0;
            }
            if (!string.IsNullOrEmpty(d.bg))
            {
                try { _bgColor = (Color)ColorConverter.ConvertFromString(d.bg); }
                catch { _bgColor = Colors.White; }
            }
        }

        void Seed()
        {
            string now = NowIso();
            _tasks.Add(new TaskItem { id = NewId(), title = L.T("seed1"), status = 0, createdAt = now });
            _tasks.Add(new TaskItem { id = NewId(), title = L.T("seed2"), status = 0, createdAt = now });
            _tasks.Add(new TaskItem { id = NewId(), title = L.T("seed3"), status = 1, createdAt = now });
            _tasks.Add(new TaskItem { id = NewId(), title = L.T("seed4"), status = 2, createdAt = now, completedAt = now });
        }

        // ================= 小工具 =================

        static string NewId() { return Guid.NewGuid().ToString("N"); }

        static string NowIso() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        static DateTime ParseTime(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return DateTime.TryParse(s, out d) ? d : DateTime.MinValue;
        }

        static string Fmt(string s)
        {
            var d = ParseTime(s);
            return d == DateTime.MinValue ? L.T("unknown") : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        static string FmtShort(string s)
        {
            var d = ParseTime(s);
            return d == DateTime.MinValue ? "" : d.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        static TextDecorationCollection StrikePen()
        {
            var td = new TextDecoration { Location = TextDecorationLocation.Strikethrough, Pen = new Pen(Th.Faint, 1), PenThicknessUnit = TextDecorationUnit.FontRecommended };
            return new TextDecorationCollection { td };
        }
    }

    // ======================================================================
    // 统一风格的对话框
    // ======================================================================

    public class ModernDialog : Window
    {
        protected readonly StackPanel Body;
        readonly StackPanel _btns;
        Action _primary;

        public ModernDialog(string title, double width)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = L.Font;
            FontSize = 13;
            UseLayoutRounding = true;
            Title = title;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var shell = new Border
            {
                Margin = new Thickness(20),
                Width = width,
                CornerRadius = new CornerRadius(14),
                Background = Brushes.White,
                BorderBrush = Th.Outline,
                BorderThickness = new Thickness(1),
                Effect = Th.Shadow(22, 4, 0.2)
            };
            var root = new DockPanel();
            var header = new DockPanel { Margin = new Thickness(18, 12, 10, 0), Background = Brushes.Transparent, Height = 32 };
            header.MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
            var close = new IconButton(Th.GClose, 11, false, delegate { DialogResult = false; });
            DockPanel.SetDock(close.Root, Dock.Right);
            header.Children.Add(close.Root);
            header.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Th.TextStrong, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 16, 18, 16) };
            DockPanel.SetDock(_btns, Dock.Bottom);
            root.Children.Add(_btns);

            Body = new StackPanel { Margin = new Thickness(18, 8, 18, 0) };
            root.Children.Add(Body);
            shell.Child = root;
            Content = shell;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
                else if (e.Key == Key.Enter && _primary != null) { e.Handled = true; _primary(); }
            };
        }

        protected Border AddButton(string text, bool primary, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 6, 18, 7),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                MinWidth = 76,
                Background = primary ? (Brush)Th.AccentBrush : Th.Chip
            };
            var tb = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = primary ? Brushes.White : Th.TextStrong, HorizontalAlignment = HorizontalAlignment.Center };
            b.Child = tb;
            var hover = primary ? (Brush)Th.AccentDeepBrush : Th.S(0xFF, 0xE6, 0xE9, 0xEF);
            var normal = b.Background;
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = normal; };
            b.MouseLeftButtonUp += delegate { if (b.IsMouseOver) click(); };
            if (primary) _primary = click;
            _btns.Children.Add(b);
            return b;
        }

        protected static Border Chip(string text, bool active, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = active ? Th.AccentSoft : Th.Chip,
                Child = new TextBlock { Text = text, FontSize = 12, Foreground = active ? Th.AccentDeepBrush : Th.Text }
            };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }
    }

    // 自定义导出时间段：日历上点两下选起止日期
    public class ExportDialog : ModernDialog
    {
        public DateTime FromDate, ToDate;
        readonly Func<DateTime, DateTime, int> _count;
        DateTime _month, _anchor;
        bool _picking;
        DateTime? _hover;
        readonly WrapPanel _chips;
        readonly TextBlock _monthText, _summary;
        readonly UniformGrid _days;

        public ExportDialog(Func<DateTime, DateTime, int> count) : base(L.T("dlg.export"), 330)
        {
            _count = count;
            var today = DateTime.Today;
            FromDate = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            ToDate = today;
            _month = new DateTime(today.Year, today.Month, 1);

            _chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            Body.Children.Add(_chips);

            var nav = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
            var prev = new IconButton(Th.GLeft, 11, false, delegate { _month = _month.AddMonths(-1); Render(); });
            var next = new IconButton(Th.GRight, 11, false, delegate { _month = _month.AddMonths(1); Render(); });
            DockPanel.SetDock(prev.Root, Dock.Left);
            DockPanel.SetDock(next.Root, Dock.Right);
            nav.Children.Add(prev.Root);
            nav.Children.Add(next.Root);
            _monthText = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Th.TextStrong, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            nav.Children.Add(_monthText);
            Body.Children.Add(nav);

            var heads = new UniformGrid { Columns = 7 };
            foreach (var h in L.WeekHeads())
                heads.Children.Add(new TextBlock { Text = h, FontSize = 11, Foreground = Th.Faint, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            Body.Children.Add(heads);

            _days = new UniformGrid { Columns = 7, Rows = 6 };
            _days.MouseLeave += delegate { if (_hover != null) { _hover = null; Render(); } };
            Body.Children.Add(_days);

            _summary = new TextBlock { FontSize = 12, Foreground = Th.Muted, Margin = new Thickness(2, 10, 0, 0) };
            Body.Children.Add(_summary);

            AddButton(L.T("cancel"), false, delegate { DialogResult = false; });
            AddButton(L.T("dlg.exportOk"), true, delegate
            {
                if (_picking) { _picking = false; ToDate = FromDate; }
                DialogResult = true;
            });
            Render();
        }

        void SetRange(DateTime a, DateTime b)
        {
            FromDate = a; ToDate = b; _picking = false; _hover = null;
            _month = new DateTime(b.Year, b.Month, 1);
            Render();
        }

        void Render()
        {
            var today = DateTime.Today;
            var ws = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var ms = new DateTime(today.Year, today.Month, 1);
            _chips.Children.Clear();
            AddChip(L.T("r.week"), ws, ws.AddDays(6));
            AddChip(L.T("r.lastWeek"), ws.AddDays(-7), ws.AddDays(-1));
            AddChip(L.T("r.month"), ms, ms.AddMonths(1).AddDays(-1));
            AddChip(L.T("r.lastMonth"), ms.AddMonths(-1), ms.AddDays(-1));

            _monthText.Text = L.MonthTitle(_month);
            DateTime a = FromDate, b = ToDate;
            if (_picking)
            {
                a = _anchor; b = _hover ?? _anchor;
                if (b < a) { var t = a; a = b; b = t; }
            }

            _days.Children.Clear();
            DateTime start = _month.AddDays(-(((int)_month.DayOfWeek + 6) % 7));
            for (int i = 0; i < 42; i++)
            {
                DateTime d = start.AddDays(i);
                bool inMonth = d.Month == _month.Month;
                bool isA = d == a, isB = d == b, inRange = d > a && d < b;
                int dow = ((int)d.DayOfWeek + 6) % 7;
                var cell = new Border { Height = 30, Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var tb = new TextBlock { Text = d.Day.ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                tb.Foreground = inMonth ? Th.TextStrong : Th.S(0xFF, 0xC3, 0xC9, 0xD2);
                if (isA || isB)
                {
                    cell.Background = Th.AccentBrush;
                    tb.Foreground = Brushes.White;
                    tb.FontWeight = FontWeights.SemiBold;
                    double l = isA ? 8 : 0, r = isB ? 8 : 0;
                    cell.CornerRadius = new CornerRadius(l, r, r, l);
                }
                else if (inRange)
                {
                    cell.Background = Th.AccentSoft;
                    double l = dow == 0 ? 8 : 0, r = dow == 6 ? 8 : 0;
                    cell.CornerRadius = new CornerRadius(l, r, r, l);
                }
                else
                {
                    cell.CornerRadius = new CornerRadius(8);
                    var c = cell;
                    c.MouseEnter += delegate { if (!_picking) c.Background = Th.Chip; };
                    c.MouseLeave += delegate { if (!_picking) c.Background = Brushes.Transparent; };
                }
                if (d == today && !isA && !isB) { tb.Foreground = Th.AccentBrush; tb.FontWeight = FontWeights.Bold; }
                cell.Child = tb;
                DateTime day = d;
                cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Pick(day); };
                cell.MouseEnter += delegate { if (_picking && _hover != day) { _hover = day; Dispatcher.BeginInvoke(new Action(Render)); } };
                _days.Children.Add(cell);
            }

            if (_picking) _summary.Text = L.T("dlg.pickEnd");
            else _summary.Text = L.F("dlg.summary", FromDate, ToDate, _count(FromDate, ToDate));
        }

        void AddChip(string text, DateTime a, DateTime b)
        {
            bool active = !_picking && FromDate == a && ToDate == b;
            _chips.Children.Add(Chip(text, active, delegate { SetRange(a, b); }));
        }

        void Pick(DateTime d)
        {
            if (!_picking)
            {
                _picking = true;
                _anchor = d;
                _hover = d;
            }
            else
            {
                _picking = false;
                FromDate = d < _anchor ? d : _anchor;
                ToDate = d < _anchor ? _anchor : d;
                _hover = null;
            }
            Render();
        }
    }

    // 自定义背景颜色：柔和色板 + 色值输入 + 预览
    public class ColorDialog : ModernDialog
    {
        public Color Selected;
        readonly WrapPanel _swatches;
        readonly Border _preview;
        readonly TextBox _hex;
        bool _syncing;

        public ColorDialog(Color initial) : base(L.T("dlg.color"), 330)
        {
            Selected = initial;
            _swatches = new WrapPanel();
            Body.Children.Add(_swatches);

            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            _preview = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), BorderBrush = Th.Outline, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(_preview, Dock.Left);
            row.Children.Add(_preview);
            var lbl = new TextBlock { Text = L.T("dlg.hex"), Foreground = Th.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(lbl, Dock.Left);
            row.Children.Add(lbl);
            var box = new Border { CornerRadius = new CornerRadius(8), BorderBrush = Th.CardLineHover, BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 0), Height = 30, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            _hex = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = 13, CaretBrush = Th.AccentBrush, FocusVisualStyle = null };
            _hex.TextChanged += delegate { TryHex(); };
            _hex.GotKeyboardFocus += delegate { box.BorderBrush = Th.AccentLine; };
            _hex.LostKeyboardFocus += delegate { box.BorderBrush = Th.CardLineHover; };
            box.Child = _hex;
            row.Children.Add(box);
            Body.Children.Add(row);

            AddButton(L.T("cancel"), false, delegate { DialogResult = false; });
            AddButton(L.T("ok"), true, delegate { DialogResult = true; });
            SetColor(initial, true);
        }

        static Color Hsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        void RenderSwatches()
        {
            _swatches.Children.Clear();
            var list = new List<Color>
            {
                Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF6, 0xF7, 0xF9), Color.FromRgb(0xEE, 0xF0, 0xF3),
                Color.FromRgb(0xE4, 0xE7, 0xEB), Color.FromRgb(0xD8, 0xDC, 0xE1), Color.FromRgb(0xCB, 0xD0, 0xD7)
            };
            double[] ls = { 0.95, 0.90, 0.84 };
            double[] ss = { 0.45, 0.38, 0.30 };
            for (int k = 0; k < 3; k++)
                for (int h = 0; h < 360; h += 30) list.Add(Hsl(h, ss[k], ls[k]));
            foreach (var c0 in list)
            {
                Color c = c0;
                bool sel = c == Selected;
                var sw = new Border
                {
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(0, 0, 3.5, 3.5),
                    CornerRadius = new CornerRadius(6),
                    Background = Th.Solid(c),
                    BorderBrush = sel ? Th.AccentBrush : Th.S(0x30, 0x1F, 0x24, 0x30),
                    BorderThickness = new Thickness(sel ? 2 : 1),
                    Cursor = Cursors.Hand
                };
                sw.MouseLeftButtonUp += delegate { SetColor(c, true); };
                _swatches.Children.Add(sw);
            }
        }

        void SetColor(Color c, bool updateHex)
        {
            Selected = c;
            _preview.Background = Th.Solid(c);
            if (updateHex)
            {
                _syncing = true;
                _hex.Text = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
                _syncing = false;
            }
            RenderSwatches();
        }

        void TryHex()
        {
            if (_syncing) return;
            string t = _hex.Text.Trim().TrimStart('#');
            if (t.Length != 6) return;
            try { SetColor((Color)ColorConverter.ConvertFromString("#" + t), false); }
            catch { }
        }
    }
}
