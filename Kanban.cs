// Kanban.cs — 悬浮任务看板（纯代码 WPF，无 XAML；兼容系统自带 csc / C#5）
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Web.Script.Serialization;

// 版本信息（写入 exe 属性，降低杀软误报概率）
[assembly: System.Reflection.AssemblyTitle("Floating Kanban")]
[assembly: System.Reflection.AssemblyDescription("Floating desktop kanban task board widget")]
[assembly: System.Reflection.AssemblyCompany("FloatingKanban Project")]
[assembly: System.Reflection.AssemblyProduct("Floating Kanban")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (C) 2026 FloatingKanban Project")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace FloatingKanban
{
    public class TaskItem
    {
        public string title { get; set; }
        public int status { get; set; }          // 0 待办 1 进行中 2 已完成
        public string createdAt { get; set; }    // 录入时间
        public string completedAt { get; set; }  // 最近一次变为已完成的时间（反复重做会刷新）
    }

    public class SaveData
    {
        public List<TaskItem> tasks { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public int opacity { get; set; }
        public bool topmost { get; set; }
        public bool expanded { get; set; }
        public double ex { get; set; }   // 展开状态下的窗口位置（收起前的位置）
        public double ey { get; set; }
        public string bg { get; set; }   // 背景颜色 #RRGGBB
        public double px { get; set; }   // 胶囊上次被放置的位置
        public double py { get; set; }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "FloatingKanban_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("悬浮任务看板已经在运行了。", "提示");
                return;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            Application app = new Application();
            KanbanWindow win = new KanbanWindow();
            win.Show();
            app.Run();
            GC.KeepAlive(mutex);
        }
    }

    public class KanbanWindow : Window
    {
        List<TaskItem> _tasks = new List<TaskItem>();
        string _dir;
        string _file;

        int _addTarget = 0;
        int _opacityIndex = 1;
        bool _topmost = true;
        bool _expanded = true;
        bool _hint = false;
        bool _animating = false;
        double _savedW = 460, _savedH = 560;
        double _savedX = double.MinValue, _savedY = double.MinValue;
        double _lastPillX = double.MinValue, _lastPillY = double.MinValue;
        double[] _opLevels = new double[] { 1.0, 0.85, 0.70, 0.55 };

        Border _root;
        Grid _main;
        Grid _titleBar;
        Border _pill;
        TextBlock _pillText;
        StackPanel[] _colPanels;
        TextBlock[] _countTexts;
        TextBox _input;
        Border _targetBtn;
        Border _pinBtn;
        Border _exportBtn;
        ContextMenu _targetMenu;
        System.Windows.Forms.NotifyIcon _tray;
        System.Windows.Forms.ToolStripMenuItem _trayPinItem;
        Border _roller;
        SolidColorBrush _rootBg;
        SolidColorBrush _rootBorder;
        Border _colorBtn;
        Color _bgColor = Colors.White;

        TaskItem _dragTask;
        Border _dragCard;
        Point _dragStart;
        bool _dragArmed, _dragging;
        int _dropCol = -1, _dropIdx;
        Border _caret;
        Grid _editing;

        string[] _colNames = new string[] { "待办", "进行中", "已完成" };
        Color[] _colColors;
        SolidColorBrush _textDark;
        SolidColorBrush _textMid;
        SolidColorBrush _textFaint;

        public KanbanWindow()
        {
            _colColors = new Color[] { Col(0x4F, 0x8C, 0xFF), Col(0xFF, 0xA9, 0x40), Col(0x2E, 0xC2, 0x7B) };
            _textDark = Solid(0xFF, 0x2B, 0x30, 0x3A);
            _textMid = Solid(0xFF, 0x3A, 0x41, 0x4D);
            _textFaint = Solid(0xFF, 0x8A, 0x93, 0xA3);

            _dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingKanban");
            _file = System.IO.Path.Combine(_dir, "data.json");

            Title = "悬浮任务看板";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            ResizeMode = ResizeMode.NoResize;     // 无边框窗口：缩放用右下角自绘手柄
            MinWidth = 320;
            MinHeight = 420;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;

            LoadState();
            Topmost = _topmost;

            BuildUi();

            Width = _expanded ? _savedW : 200;
            Height = _expanded ? _savedH : 46;
            if (!_expanded) { MinWidth = 0; MinHeight = 0; ResizeMode = ResizeMode.NoResize; CenterPillInCollapsedWindow(); }
            _main.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            _pill.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
            _pinBtn.Opacity = _topmost ? 1.0 : 0.35;
            _root.Opacity = _opLevels[_opacityIndex];

            if (!OnScreen(Left, Top))
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + wa.Width - Width - 28;
                Top = wa.Top + 90;
            }

            Loaded += delegate(object s, RoutedEventArgs e) { _input.Focus(); };

            SetupIcons();
            Refresh();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveState();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            base.OnClosed(e);
        }

        // ================= UI =================

        void BuildUi()
        {
            _root = new Border();
            _root.CornerRadius = new CornerRadius(14);
            // 不冻结的画刷，便于收展动画时整体变淡/恢复
            _rootBg = new SolidColorBrush(Color.FromArgb(0xE2, 0xFF, 0xFF, 0xFF));
            _rootBorder = new SolidColorBrush(Color.FromArgb(0x30, 0x33, 0x44, 0x55));
            _root.Background = _rootBg;
            _root.BorderBrush = _rootBorder;
            _root.BorderThickness = new Thickness(1);
            _root.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(_root, TextFormattingMode.Display);

            var outer = new Grid();
            _root.Child = outer;

            _main = new Grid();
            _main.Margin = new Thickness(12, 10, 12, 12);
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.Children.Add(_main);

            BuildTitleBar();
            BuildInputRow();
            BuildColumns();
            BuildResizeGrip();
            BuildRoller(outer);
            BuildPill(outer);     // 胶囊与内容同层叠加（最后加入在最上层），收展时内容不因行高增减而跳动

            Content = _root;
        }

        void BuildPill(Grid outer)
        {
            _pill = new Border();
            _pill.CornerRadius = new CornerRadius(12);
            _pill.Background = Solid(_bgColor);
            _pill.BorderBrush = Solid(0x30, 0x33, 0x44, 0x55);
            _pill.BorderThickness = new Thickness(1);
            _pill.Padding = new Thickness(14, 7, 14, 8);
            _pill.Cursor = Cursors.Hand;
            _pill.HorizontalAlignment = HorizontalAlignment.Left;
            _pill.VerticalAlignment = VerticalAlignment.Top;
            _pill.Margin = new Thickness(1);
            _pill.MouseLeftButtonDown += PillDown;

            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "📋", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _pillText = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = _textMid, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(_pillText);
            _pill.Child = sp;
            outer.Children.Add(_pill);
        }

        void BuildTitleBar()
        {
            var bar = new Grid { Background = Brushes.Transparent };
            _titleBar = bar;
            Grid.SetRow(bar, 0);
            bar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                try { DragMove(); } catch { }
                SaveState();
            };

            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            brand.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Solid(_colColors[0]), Margin = new Thickness(4, 0, 0, 0) });
            brand.Children.Add(new TextBlock { Text = "任务看板", FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = _textDark, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(brand);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _pinBtn = IconBtn("📌", "置顶开关", TogglePin);
            btns.Children.Add(_pinBtn);
            btns.Children.Add(IconBtn("◐", "调节透明度", CycleOpacity));
            _colorBtn = IconBtn("🎨", "背景颜色", ShowColorMenu);
            btns.Children.Add(_colorBtn);
            _exportBtn = IconBtn("⇩", "导出已完成任务", ShowExportMenu);
            btns.Children.Add(_exportBtn);
            btns.Children.Add(IconBtn("—", "收起为小条", Collapse));
            btns.Children.Add(IconBtn("✕", "退出", delegate { Close(); }));
            bar.Children.Add(btns);

            _main.Children.Add(bar);
        }

        void BuildInputRow()
        {
            var row = new Grid();
            Grid.SetRow(row, 1);
            row.Margin = new Thickness(0, 8, 0, 10);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _targetBtn = new Border();
            _targetBtn.CornerRadius = new CornerRadius(12);
            _targetBtn.Padding = new Thickness(10, 0, 10, 0);
            _targetBtn.Margin = new Thickness(0, 0, 8, 0);
            _targetBtn.Cursor = Cursors.Hand;
            _targetBtn.MinHeight = 32;
            _targetMenu = NewMenu();
            var menuHint = NewItem("添加到");
            menuHint.IsEnabled = false;
            _targetMenu.Items.Add(menuHint);
            for (int i = 0; i < 3; i++)
            {
                var mi = NewItem(_colNames[i]);
                mi.Icon = new Ellipse { Width = 9, Height = 9, Fill = Solid(_colColors[i]) };
                var idx = i;
                mi.Click += delegate(object s, RoutedEventArgs e2)
                {
                    _addTarget = idx;
                    UpdateTargetBtn();
                };
                _targetMenu.Items.Add(mi);
            }
            _targetBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _targetMenu.PlacementTarget = _targetBtn;
                _targetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                _targetMenu.IsOpen = true;
            };
            Grid.SetColumn(_targetBtn, 0);
            row.Children.Add(_targetBtn);
            UpdateTargetBtn();

            _input = new TextBox();
            _input.BorderThickness = new Thickness(0);
            _input.Background = Brushes.Transparent;
            _input.VerticalContentAlignment = VerticalAlignment.Center;
            _input.FontSize = 12.5;
            _input.Margin = new Thickness(10, 0, 4, 0);
            _input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) AddTask();
            };
            _input.GotKeyboardFocus += delegate(object s, KeyboardFocusChangedEventArgs e)
            {
                if (_hint) { _hint = false; _input.Text = ""; _input.Foreground = _textDark; }
            };
            _input.LostKeyboardFocus += delegate(object s, KeyboardFocusChangedEventArgs e)
            {
                if (_input.Text.Length == 0) SetHint();
            };
            SetHint();

            var inputBorder = new Border();
            inputBorder.CornerRadius = new CornerRadius(10);
            inputBorder.Background = Brushes.White;
            inputBorder.BorderBrush = Solid(0x30, 0xD5, 0xDB, 0xE3);
            inputBorder.BorderThickness = new Thickness(1);
            inputBorder.MinHeight = 32;
            inputBorder.Child = _input;
            inputBorder.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { _input.Focus(); };
            Grid.SetColumn(inputBorder, 1);
            row.Children.Add(inputBorder);

            var addBtn = new Border();
            addBtn.CornerRadius = new CornerRadius(10);
            addBtn.MinHeight = 32;
            addBtn.Margin = new Thickness(8, 0, 0, 0);
            addBtn.Padding = new Thickness(14, 0, 14, 0);
            addBtn.Cursor = Cursors.Hand;
            addBtn.Style = HoverBg(Solid(_colColors[0]), Solid(0xFF, 0x3D, 0x76, 0xE0));
            addBtn.Child = new TextBlock { Text = "＋ 添加", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            addBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AddTask();
            };
            Grid.SetColumn(addBtn, 2);
            row.Children.Add(addBtn);

            _main.Children.Add(row);
        }

        void SetHint()
        {
            _hint = true;
            _input.Text = "输入新任务，回车添加…";
            _input.Foreground = _textFaint;
        }

        void BuildColumns()
        {
            var cols = new Grid();
            Grid.SetRow(cols, 2);
            for (int i = 0; i < 3; i++)
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _colPanels = new StackPanel[3];
            _countTexts = new TextBlock[3];

            MouseButtonEventHandler headerDrag = delegate(object s, MouseButtonEventArgs e)
            {
                try { DragMove(); } catch { }
                SaveState();
            };

            for (int i = 0; i < 3; i++)
            {
                var colWrap = new DockPanel();

                var header = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 0, 2, 6) };
                var hp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
                hp.MouseLeftButtonDown += headerDrag;
                hp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Solid(_colColors[i]), VerticalAlignment = VerticalAlignment.Center });
                hp.Children.Add(new TextBlock { Text = _colNames[i], FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = _textMid, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });

                var chip = new Border { CornerRadius = new CornerRadius(8), Background = Tint(_colColors[i], 0x30), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center };
                _countTexts[i] = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Solid(_colColors[i]), Text = "0" };
                chip.Child = _countTexts[i];
                hp.Children.Add(chip);
                DockPanel.SetDock(hp, Dock.Left);
                header.Children.Add(hp);

                if (i == 2)
                {
                    var clear = new TextBlock { Text = "清空", FontSize = 10.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
                    var cstyle = new Style(typeof(TextBlock));
                    cstyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, _textFaint));
                    var ctrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                    ctrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, Solid(0xFF, 0xE5, 0x53, 0x4B)));
                    cstyle.Triggers.Add(ctrig);
                    clear.Style = cstyle;
                    clear.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                    {
                        e.Handled = true;
                        ClearDone();
                    };
                    DockPanel.SetDock(clear, Dock.Right);
                    header.Children.Add(clear);
                }

                DockPanel.SetDock(header, Dock.Top);
                colWrap.Children.Add(header);

                var sv = new ScrollViewer();
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                var panel = new StackPanel { Margin = new Thickness(1, 0, 3, 0) };
                _colPanels[i] = panel;
                sv.Content = panel;
                colWrap.Children.Add(sv);

                if (i == 0) colWrap.Margin = new Thickness(0, 0, 6, 0);
                if (i == 1) colWrap.Margin = new Thickness(6, 0, 6, 0);
                if (i == 2) colWrap.Margin = new Thickness(6, 0, 0, 0);

                Grid.SetColumn(colWrap, i);
                cols.Children.Add(colWrap);
            }

            _main.Children.Add(cols);
        }

        // 卷轴效果：卷起/摊开时贴着窗口下边缘的“滚轴”阴影带，随边缘一起移动
        void BuildRoller(Grid outer)
        {
            var shade = new Rectangle();
            shade.Fill = new LinearGradientBrush(
                Color.FromArgb(0, 70, 95, 140),
                Color.FromArgb(95, 70, 95, 140),
                90);
            _roller = new Border
            {
                Height = 24,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 12, 12),
                Child = shade,
                IsHitTestVisible = false,
                Opacity = 0
            };
            outer.Children.Add(_roller);
        }

        // 让胶囊在 200x46 的收起状态窗口里居中（按实测尺寸算固定边距，全程不再移动）
        void CenterPillInCollapsedWindow()
        {
            _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double mx = Math.Max(1, (198 - _pill.DesiredSize.Width) / 2.0 + 1);
            double my = Math.Max(1, (44 - _pill.DesiredSize.Height) / 2.0 + 1);
            _pill.Margin = new Thickness(mx, my, 0, 0);
        }

        // 右下角自绘缩放手柄：透明命中区 + 贴角的三条斜线（不用系统 Thumb 默认的白底）
        void BuildResizeGrip()
        {
            var icon = new Canvas { Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 1, 1), IsHitTestVisible = false };
            icon.Children.Add(new Line { X1 = 1, Y1 = 10, X2 = 10, Y2 = 1, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            icon.Children.Add(new Line { X1 = 4, Y1 = 10, X2 = 10, Y2 = 4, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            icon.Children.Add(new Line { X1 = 7, Y1 = 10, X2 = 10, Y2 = 7, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            StrokeColor(icon, Solid(0xFF, 0xC4, 0xCD, 0xD8));

            var gripTmpl = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            gripTmpl.VisualTree = bd;

            var grip = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 20,
                Height = 20,
                Cursor = Cursors.SizeNWSE,
                Template = gripTmpl,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            grip.DragDelta += delegate(object s, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
            {
                Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
                Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
            };
            grip.DragCompleted += delegate(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e) { SaveState(); };
            grip.MouseEnter += delegate(object s, MouseEventArgs e) { StrokeColor(icon, Solid(_colColors[0])); };
            grip.MouseLeave += delegate(object s, MouseEventArgs e) { StrokeColor(icon, Solid(0xFF, 0xC4, 0xCD, 0xD8)); };

            Grid.SetRow(grip, 2);
            Grid.SetRow(icon, 2);
            _main.Children.Add(grip);
            _main.Children.Add(icon);
        }

        static void StrokeColor(Canvas canvas, SolidColorBrush br)
        {
            foreach (object child in canvas.Children)
            {
                var ln = child as Line;
                if (ln != null) ln.Stroke = br;
            }
        }

        // ============ 卡片 ============

        UIElement MakeCard(TaskItem t)
        {
            bool done = t.status == 2;
            // 卡片跟随背景色：普通卡片取浅色调（比面板亮），已完成卡片更接近背景色（弱化、后退感）
            var cardBg = done
                ? Solid(Mix(_bgColor, Color.FromRgb(0xEF, 0xF2, 0xF4), 0.45))
                : Solid(Mix(_bgColor, Colors.White, 0.55));

            var card = new Border();
            card.CornerRadius = new CornerRadius(9);
            card.Background = cardBg;
            card.BorderBrush = Solid(0x22, 0x10, 0x22, 0x33);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(8, 7, 6, 7);
            card.Margin = new Thickness(0, 0, 0, 6);
            card.Cursor = Cursors.Hand;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var circle = new Border();
            circle.Width = 18; circle.Height = 18;
            circle.CornerRadius = new CornerRadius(9);
            circle.VerticalAlignment = VerticalAlignment.Top;
            circle.Cursor = Cursors.Hand;
            if (done)
            {
                circle.Background = Solid(_colColors[2]);
                circle.Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(circle, "标记为未完成");
            }
            else
            {
                circle.Background = Brushes.White;
                circle.BorderBrush = Solid(0x88, 0x9A, 0xA3, 0xAF);
                circle.BorderThickness = new Thickness(1.5);
                ToolTipService.SetToolTip(circle, "标记为已完成");
            }
            circle.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                t.status = (t.status == 2) ? 0 : 2;
                if (t.status == 2) t.completedAt = NowIso();
                Refresh();
            };

            var txt = new TextBlock();
            txt.Text = t.title;
            txt.TextWrapping = TextWrapping.Wrap;
            txt.LineHeight = 17;
            txt.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            txt.MaxHeight = 34;                        // 平时只显示两行，超出省略
            txt.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.FontSize = 12.5;
            txt.VerticalAlignment = VerticalAlignment.Top;
            txt.Margin = new Thickness(4, 0, 2, 0);
            if (done) { txt.Foreground = _textFaint; txt.TextDecorations = StrikePen(); }
            else txt.Foreground = Solid(0xFF, 0x2F, 0x35, 0x3F);
            txt.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount >= 2) { e.Handled = true; StartEdit(g, txt, t); }
            };
            ToolTipService.SetToolTip(txt, "双击修改内容\n创建: " + Fmt(t.createdAt) + (done ? "\n完成: " + Fmt(t.completedAt) : ""));

            Grid.SetColumn(circle, 0);
            Grid.SetColumn(txt, 1);
            g.Children.Add(circle);
            g.Children.Add(txt);
            if (done)
            {
                // 已完成卡片右下角显示完成时间
                var outer = new StackPanel();
                outer.Children.Add(g);
                outer.Children.Add(new TextBlock { Text = FmtShort(t.completedAt), FontSize = 10, Foreground = _textFaint, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 2, 0) });
                card.Child = outer;
            }
            else card.Child = g;

            // 悬停展开全部内容（拖动中不展开，避免布局乱跳）
            card.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (_dragging) return;
                txt.MaxHeight = double.PositiveInfinity;
                txt.TextTrimming = TextTrimming.None;
            };
            card.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                txt.MaxHeight = 34;
                txt.TextTrimming = TextTrimming.CharacterEllipsis;
            };

            // 拖动排序/跨列：按住卡片移动，蓝色横线指示落点
            card.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_editing == g) return;
                _dragTask = t; _dragCard = card; _dragStart = e.GetPosition(this);
                _dragArmed = true; _dragging = false;
            };
            card.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_dragArmed && !_dragging) return;
                if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(false); return; }
                var pos = e.GetPosition(this);
                if (!_dragging)
                {
                    if (_editing == g) { _dragArmed = false; return; }
                    if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
                    _dragging = true;
                    _dragCard.CaptureMouse();
                    _dragCard.Opacity = 0.45;
                    _caret = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Solid(_colColors[0]), Margin = new Thickness(2, 0, 4, 4), IsHitTestVisible = false };
                }
                Mouse.SetCursor(Cursors.SizeAll);
                UpdateCaret(pos);
            };
            card.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                bool commit = _dragging;
                EndDrag(commit);
            };
            card.LostMouseCapture += delegate(object s, MouseEventArgs e)
            {
                if (_dragging) EndDrag(false);
            };

            // 右键菜单：置顶 / 编辑 / 删除
            var menu = NewMenu();
            var miTop = NewItem("置顶");
            miTop.Click += delegate(object s, RoutedEventArgs e2) { PinToTop(t); };
            var miEdit = NewItem("编辑");
            miEdit.Click += delegate(object s, RoutedEventArgs e2) { StartEdit(g, txt, t); };
            var miDel = NewItem("删除");
            miDel.Click += delegate(object s, RoutedEventArgs e2) { _tasks.Remove(t); Refresh(); };
            menu.Items.Add(miTop);
            menu.Items.Add(miEdit);
            menu.Items.Add(miDel);
            card.ContextMenu = menu;

            return card;
        }

        // ============ 拖动与右键 ============

        void EndDrag(bool commit)
        {
            // 先清标志再释放捕获：释放捕获会同步触发 LostMouseCapture，
            // 若 _dragging 还为 true 会导致递归取消提交
            bool wasDragging = _dragging;
            TaskItem task = _dragTask;
            int col = _dropCol, idx = _dropIdx;

            _dragging = false;
            _dragArmed = false;

            if (_caret != null)
            {
                var p = _caret.Parent as Panel;
                if (p != null) p.Children.Remove(_caret);
                _caret = null;
            }
            if (_dragCard != null)
            {
                _dragCard.Opacity = 1.0;
                if (_dragCard.IsMouseCaptured) _dragCard.ReleaseMouseCapture();
            }
            _dragTask = null; _dragCard = null; _dropCol = -1;

            if (commit && wasDragging && task != null && col >= 0)
                CommitDrag(task, col, idx);
        }

        void UpdateCaret(Point pos)
        {
            if (_caret == null) return;
            int col = 0; double best = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var p = _colPanels[i];
                var org = p.TransformToVisual(this).Transform(new Point(0, 0));
                double d = Math.Abs(pos.X - (org.X + p.ActualWidth / 2));
                if (d < best) { best = d; col = i; }
            }
            var panel = _colPanels[col];
            var oldParent = _caret.Parent as Panel;
            if (oldParent != null) oldParent.Children.Remove(_caret);
            int visualIdx = 0;
            foreach (UIElement child in panel.Children)
            {
                double mid = child.TransformToVisual(panel).Transform(new Point(0, child.RenderSize.Height / 2)).Y;
                if (pos.Y > mid) visualIdx++;
            }
            // 被拖的卡片本身若在目标列且位于落点之前，数据索引要减一（它会被先移除）
            int dragIdx = _dragCard != null ? panel.Children.IndexOf(_dragCard) : -1;
            panel.Children.Insert(visualIdx, _caret);
            int dataIdx = (dragIdx >= 0 && dragIdx < visualIdx) ? visualIdx - 1 : visualIdx;
            _dropCol = col;
            _dropIdx = dataIdx;
        }

        void CommitDrag(TaskItem t, int col, int idx)
        {
            int oldStatus = t.status;
            _tasks.Remove(t);
            int seen = 0, insertAt = _tasks.Count;
            for (int i = 0; i < _tasks.Count; i++)
            {
                int st = _tasks[i].status;
                if (st < 0) st = 0;
                if (st > 2) st = 2;
                if (st == col)
                {
                    if (seen == idx) { insertAt = i; break; }
                    seen++;
                }
            }
            if (col == 2 && oldStatus != 2) t.completedAt = NowIso();
            t.status = col;
            _tasks.Insert(insertAt, t);
            Refresh();
        }

        void PinToTop(TaskItem t)
        {
            _tasks.Remove(t);
            int insertAt = _tasks.Count;
            for (int i = 0; i < _tasks.Count; i++)
            {
                int st = _tasks[i].status;
                if (st < 0) st = 0;
                if (st > 2) st = 2;
                if (st == t.status) { insertAt = i; break; }
            }
            _tasks.Insert(insertAt, t);
            Refresh();
        }

        Border IconBtn(string glyph, string tip, Action onClick)
        {
            var b = new Border();
            b.Width = 26; b.Height = 26;
            b.CornerRadius = new CornerRadius(7);
            b.Margin = new Thickness(2, 0, 2, 0);
            b.Cursor = Cursors.Hand;
            b.Style = HoverBg(Brushes.Transparent, Solid(0x20, 0x10, 0x22, 0x33));
            b.Child = new TextBlock { Text = glyph, FontSize = 11, Foreground = Solid(0xFF, 0x55, 0x5D, 0x6B), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                onClick();
            };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        void StartEdit(Grid g, TextBlock txt, TaskItem t)
        {
            bool finished = false;
            _editing = g;
            var ed = new TextBox();
            ed.Text = t.title;
            ed.FontSize = 12.5;
            ed.BorderThickness = new Thickness(0);
            ed.Background = Brushes.Transparent;
            ed.VerticalContentAlignment = VerticalAlignment.Center;
            ed.Margin = new Thickness(4, 0, 2, 0);
            ed.Foreground = Solid(0xFF, 0x2F, 0x35, 0x3F);

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
                if (e.Key == Key.Enter) commit();
                else if (e.Key == Key.Escape) cancel();
            };
            ed.LostFocus += delegate(object s, RoutedEventArgs e) { commit(); };

            Grid.SetColumn(ed, 1);
            int idx = g.Children.IndexOf(txt);
            g.Children.RemoveAt(idx);
            g.Children.Insert(idx, ed);
            ed.Focus();
            ed.SelectAll();
        }

        // ============ 数据操作 ============

        void Refresh()
        {
            if (_colPanels == null) return;
            int[] counts = new int[3];
            for (int i = 0; i < 3; i++) _colPanels[i].Children.Clear();
            foreach (TaskItem t in _tasks)
            {
                int s = t.status;
                if (s < 0) s = 0;
                if (s > 2) s = 2;
                _colPanels[s].Children.Add(MakeCard(t));
                counts[s]++;
            }
            for (int i = 0; i < 3; i++) _countTexts[i].Text = counts[i].ToString();
            _pillText.Text = string.Format("待办 {0}    进行 {1}    完成 {2}", counts[0], counts[1], counts[2]);
            SaveState();
        }

        void AddTask()
        {
            if (_hint) { _input.Focus(); return; }
            string s = _input.Text.Trim();
            if (s.Length == 0) { _input.Focus(); return; }
            TaskItem t = new TaskItem();
            t.title = s;
            t.status = _addTarget;
            t.createdAt = NowIso();
            if (_addTarget == 2) t.completedAt = t.createdAt;
            _tasks.Add(t);
            _input.Text = "";
            Refresh();
            _input.Focus();
        }

        void ClearDone()
        {
            int n = 0;
            foreach (TaskItem t in _tasks) if (t.status == 2) n++;
            if (n == 0) return;
            var r = MessageBox.Show(this, string.Format("确定要清空 {0} 条已完成的任务吗？", n), "清空已完成", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK)
            {
                _tasks.RemoveAll(delegate(TaskItem t) { return t.status == 2; });
                Refresh();
            }
        }

        // ============ 图标与托盘 ============

        void SetupIcons()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                    _tray = new System.Windows.Forms.NotifyIcon();
                    _tray.Icon = (System.Drawing.Icon)ic.Clone();
                    _tray.Text = "悬浮任务看板";
                    _tray.Visible = true;

                    var strip = new System.Windows.Forms.ContextMenuStrip();
                    strip.Items.Add("显示 / 隐藏", null, delegate(object s2, EventArgs e2) { ToggleFromTray(); });
                    _trayPinItem = new System.Windows.Forms.ToolStripMenuItem("窗口置顶");
                    _trayPinItem.Checked = _topmost;
                    _trayPinItem.Click += delegate(object s2, EventArgs e2)
                    {
                        TogglePin();
                    };
                    strip.Items.Add(_trayPinItem);
                    strip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                    strip.Items.Add("退出", null, delegate(object s2, EventArgs e2) { Close(); });
                    _tray.ContextMenuStrip = strip;
                    _tray.DoubleClick += delegate(object s2, EventArgs e2) { ToggleFromTray(); };
                }
            }
            catch { }
        }

        void ToggleFromTray()
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        }

        // ============ 窗口控制 ============

        void Collapse()
        {
            if (!_expanded || _animating) return;
            _animating = true;
            _savedW = Width; _savedH = Height;
            _savedX = Left; _savedY = Top;        // 记住展开时的位置，恢复时回到这里
            _expanded = false;
            MinWidth = 0; MinHeight = 0;          // 解除最小尺寸限制，才能缩成胶囊

            // 冻结内容排版：内容保持原布局不重排，随窗口边缘收缩被直接裁掉
            _main.MinWidth = _savedW; _main.MinHeight = _savedH;
            _pill.Visibility = Visibility.Visible;
            CenterPillInCollapsedWindow();
            _pill.BeginAnimation(OpacityProperty, FadeAnim(0, 1, 350));

            // 整体向左上角收拢：窗口缩小的同时面板与内容一起变淡
            _main.BeginAnimation(OpacityProperty, FadeAnim(1, 0, 500));
            _rootBg.BeginAnimation(SolidColorBrush.OpacityProperty, FadeAnim(1, 0, 500));
            _rootBorder.BeginAnimation(SolidColorBrush.OpacityProperty, FadeAnim(1, 0, 500));
            _roller.BeginAnimation(OpacityProperty, FadeAnim(0, 1, 250));

            AnimateWindowSize(200, 46, 500, delegate
            {
                BeginAnimation(WidthProperty, null); Width = 200;
                BeginAnimation(HeightProperty, null); Height = 46;
                _main.BeginAnimation(OpacityProperty, null); _main.Opacity = 1;
                _main.MinWidth = 0; _main.MinHeight = 0;
                _main.Visibility = Visibility.Collapsed;
                    _rootBg.BeginAnimation(SolidColorBrush.OpacityProperty, null); _rootBg.Opacity = 0;
                    _rootBorder.BeginAnimation(SolidColorBrush.OpacityProperty, null); _rootBorder.Opacity = 0;
                    _pill.BeginAnimation(OpacityProperty, null); _pill.Opacity = 1;
                    SaveState();
                    _animating = false;
                    _roller.BeginAnimation(OpacityProperty, FadeAnim(1, 0, 200));
                    // 收起完成后，滑到上次胶囊被手动放置的位置
                    if (_lastPillX != double.MinValue)
                    {
                        var ml = new DoubleAnimation(Left, _lastPillX, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease() };
                        var mt = new DoubleAnimation(Top, _lastPillY, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease() };
                        ml.Completed += delegate(object s2, EventArgs e2)
                        {
                            BeginAnimation(LeftProperty, null); Left = _lastPillX;
                            BeginAnimation(TopProperty, null); Top = _lastPillY;
                            SaveState();
                        };
                        BeginAnimation(LeftProperty, ml);
                        BeginAnimation(TopProperty, mt);
                    }
            });
        }

        void Expand()
        {
            if (_expanded || _animating) return;
            _animating = true;
            _expanded = true;
            MinWidth = 0; MinHeight = 0;          // 动画期间允许从胶囊尺寸开始放大
            _main.Visibility = Visibility.Visible;
            _main.MinWidth = _savedW; _main.MinHeight = _savedH;
            _main.BeginAnimation(OpacityProperty, null); _main.Opacity = 0;
            _main.BeginAnimation(OpacityProperty, FadeAnim(0, 1, 500));
            _rootBg.BeginAnimation(SolidColorBrush.OpacityProperty, FadeAnim(0, 1, 500));
            _rootBorder.BeginAnimation(SolidColorBrush.OpacityProperty, FadeAnim(0, 1, 500));
            _pill.BeginAnimation(OpacityProperty, FadeAnim(1, 0, 300));
            if (_savedX != double.MinValue)
            {
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                Left = _savedX;
                Top = _savedY;
            }
            _pill.BeginAnimation(MarginProperty, null);
            _pill.Margin = new Thickness(1);
            BeginAnimation(WidthProperty, null); Width = 200;
            BeginAnimation(HeightProperty, null); Height = 46;
            _roller.BeginAnimation(OpacityProperty, FadeAnim(0, 1, 250));

            AnimateWindowSize(_savedW, _savedH, 500, delegate
            {
                BeginAnimation(WidthProperty, null); Width = _savedW;
                BeginAnimation(HeightProperty, null); Height = _savedH;
                _main.BeginAnimation(OpacityProperty, null); _main.Opacity = 1;
                _main.MinWidth = 0; _main.MinHeight = 0;
                _rootBg.BeginAnimation(SolidColorBrush.OpacityProperty, null); _rootBg.Opacity = 1;
                _rootBorder.BeginAnimation(SolidColorBrush.OpacityProperty, null); _rootBorder.Opacity = 1;
                _pill.BeginAnimation(OpacityProperty, null); _pill.Opacity = 0;
                _pill.Visibility = Visibility.Collapsed;
                MinWidth = 320; MinHeight = 420;
                ClampIntoWorkArea();
                SaveState();
                _animating = false;
                _roller.BeginAnimation(OpacityProperty, FadeAnim(1, 0, 200));
            });
        }

        static IEasingFunction Ease()
        {
            return new CubicEase { EasingMode = EasingMode.EaseInOut };   // 缓入缓出：两头慢中间快
        }

        // 淡入淡出：与尺寸动画同时长、同缓动，保证内容和窗口缩放同步结束，末尾不闪断
        static DoubleAnimation FadeAnim(double from, double to)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease() };
        }

        static DoubleAnimation FadeAnim(double from, double to, int ms)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease() };
        }

        // 窗口尺寸缓动动画（结束后清除动画并落定到目标值）
        void AnimateWindowSize(double w1, double h1, int ms, EventHandler done)
        {
            var aw = new DoubleAnimation(Width, w1, TimeSpan.FromMilliseconds(ms));
            var ah = new DoubleAnimation(Height, h1, TimeSpan.FromMilliseconds(ms));
            aw.EasingFunction = Ease();
            ah.EasingFunction = Ease();
            if (done != null) ah.Completed += done;
            BeginAnimation(WidthProperty, aw);
            BeginAnimation(HeightProperty, ah);
        }

        // 确保窗口完整落在工作区内（防止拖到角落展开后出屏）
        void ClampIntoWorkArea()
        {
            var wa = SystemParameters.WorkArea;
            if (Left + Width > wa.Right) Left = wa.Right - Width - 8;
            if (Top + Height > wa.Bottom) Top = wa.Bottom - Height - 8;
            if (Left < wa.Left) Left = wa.Left + 4;
            if (Top < wa.Top) Top = wa.Top + 4;
        }

        void PillDown(object sender, MouseButtonEventArgs e)
        {
            double l0 = Left, t0 = Top;
            try { DragMove(); } catch { }
            if (Math.Abs(Left - l0) < 0.5 && Math.Abs(Top - t0) < 0.5) Expand();
            else { _lastPillX = Left; _lastPillY = Top; SaveState(); }
        }

        void TogglePin()
        {
            _topmost = !_topmost;
            Topmost = _topmost;
            _pinBtn.Opacity = _topmost ? 1.0 : 0.35;
            if (_trayPinItem != null) _trayPinItem.Checked = _topmost;
            SaveState();
        }

        void CycleOpacity()
        {
            _opacityIndex = (_opacityIndex + 1) % _opLevels.Length;
            _root.Opacity = _opLevels[_opacityIndex];
            SaveState();
        }

        void UpdateTargetBtn()
        {
            _targetBtn.Background = Tint(_colColors[_addTarget], 0x2A);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Solid(_colColors[_addTarget]), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = _colNames[_addTarget] + " ▾", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Solid(_colColors[_addTarget]), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _targetBtn.Child = sp;
            ToolTipService.SetToolTip(_targetBtn, "选择新任务加入的列");
        }

        // ============ 导出 ============

        void ShowExportMenu()
        {
            var m = NewMenu();
            var hint = NewItem("导出已完成任务");
            hint.IsEnabled = false;
            m.Items.Add(hint);

            var miWeek = NewItem("本周（周一 ~ 周日）");
            miWeek.Click += delegate(object s, RoutedEventArgs e) { ExportRange(StartOfWeek(), StartOfWeek().AddDays(6)); };
            m.Items.Add(miWeek);

            var miLastWeek = NewItem("上周");
            miLastWeek.Click += delegate(object s, RoutedEventArgs e) { ExportRange(StartOfWeek().AddDays(-7), StartOfWeek().AddDays(-1)); };
            m.Items.Add(miLastWeek);

            var miMonth = NewItem("本月");
            miMonth.Click += delegate(object s, RoutedEventArgs e)
            {
                var d = DateTime.Today;
                var first = new DateTime(d.Year, d.Month, 1);
                ExportRange(first, first.AddMonths(1).AddDays(-1));
            };
            m.Items.Add(miMonth);

            var miLastMonth = NewItem("上月");
            miLastMonth.Click += delegate(object s, RoutedEventArgs e)
            {
                var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
                ExportRange(first, first.AddMonths(1).AddDays(-1));
            };
            m.Items.Add(miLastMonth);

            var miQuarter = NewItem("本季度");
            miQuarter.Click += delegate(object s, RoutedEventArgs e)
            {
                var d = DateTime.Today;
                var qs = new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1);
                ExportRange(qs, qs.AddMonths(3).AddDays(-1));
            };
            m.Items.Add(miQuarter);

            var miCustom = NewItem("自定义时间段…");
            miCustom.Click += delegate(object s, RoutedEventArgs e)
            {
                var dlg = new ExportDialog();
                dlg.Owner = this;
                if (dlg.ShowDialog() == true) ExportRange(dlg.FromDate, dlg.ToDate);
            };
            m.Items.Add(miCustom);

            m.PlacementTarget = _exportBtn;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        static DateTime StartOfWeek()
        {
            var d = DateTime.Today;
            return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        }

        void ExportRange(DateTime from, DateTime to)
        {
            DateTime end = to.Date.AddDays(1);
            var rows = new List<TaskItem>();
            foreach (TaskItem t in _tasks)
            {
                if (t.status != 2) continue;
                DateTime cd = ParseTime(t.completedAt);
                if (cd >= from.Date && cd < end) rows.Add(t);
            }
            if (rows.Count == 0)
            {
                MessageBox.Show(this, string.Format("{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd} 时间段内没有已完成的任务。", from, to), "导出", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            rows.Sort(delegate(TaskItem a, TaskItem b) { return ParseTime(a.completedAt).CompareTo(ParseTime(b.completedAt)); });

            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = string.Format("已完成任务_{0:yyyyMMdd}_至_{1:yyyyMMdd}.csv", from, to);
            dlg.Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
            dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (dlg.ShowDialog(this) != true) return;

            var sb = new StringBuilder();
            sb.AppendLine("完成时间,任务内容,录入时间,用时(天)");
            foreach (TaskItem t in rows)
            {
                DateTime cd = ParseTime(t.completedAt);
                DateTime cr = ParseTime(t.createdAt);
                string days = "";
                if (cr != DateTime.MinValue && cd >= cr)
                {
                    double dd = (cd - cr).TotalDays;
                    days = dd < 1 ? "0" : Math.Round(dd, 1).ToString();
                }
                string title = (t.title ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
                string created = cr == DateTime.MinValue ? "" : string.Format("{0:yyyy-MM-dd HH:mm}", cr);
                sb.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm},\"{1}\",{2},{3}", cd, title, created, days));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show(this, string.Format("已导出 {0} 条任务到：\n{1}", rows.Count, dlg.FileName), "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ============ 背景颜色 ============

        void ShowColorMenu()
        {
            var m = NewMenu();
            var hint = NewItem("莫兰迪色系");
            hint.IsEnabled = false;
            m.Items.Add(hint);

            var palette = new[] {
                new { Name = "云白（默认）", C = Color.FromRgb(0xFF, 0xFF, 0xFF) },
                new { Name = "米灰",   C = Color.FromRgb(0xE7, 0xE3, 0xDC) },
                new { Name = "亚麻",   C = Color.FromRgb(0xEB, 0xE4, 0xD8) },
                new { Name = "燕麦",   C = Color.FromRgb(0xE9, 0xE1, 0xD2) },
                new { Name = "豆沙粉", C = Color.FromRgb(0xE0, 0xCF, 0xCB) },
                new { Name = "藕粉",   C = Color.FromRgb(0xD9, 0xC7, 0xC7) },
                new { Name = "陶土",   C = Color.FromRgb(0xCD, 0xB9, 0xAD) },
                new { Name = "藕紫",   C = Color.FromRgb(0xD8, 0xD0, 0xE0) },
                new { Name = "雾霾蓝", C = Color.FromRgb(0xC9, 0xD6, 0xE3) },
                new { Name = "雾蓝",   C = Color.FromRgb(0xB0, 0xBF, 0xD1) },
                new { Name = "灰豆绿", C = Color.FromRgb(0xCB, 0xD6, 0xC6) },
                new { Name = "灰绿",   C = Color.FromRgb(0xB7, 0xC4, 0xB1) },
                new { Name = "青瓷",   C = Color.FromRgb(0xC9, 0xD8, 0xD4) }
            };
            foreach (var p in palette)
            {
                var mi = NewItem(p.Name);
                mi.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Solid(p.C), BorderBrush = Solid(0x44, 0x99, 0x99, 0x99), BorderThickness = new Thickness(1) };
                var col = p.C;
                mi.Click += delegate(object s, RoutedEventArgs e2) { ApplyBackgroundColor(col); };
                m.Items.Add(mi);
            }

            var miCustom = NewItem("自定义颜色…");
            miCustom.Click += delegate(object s, RoutedEventArgs e2)
            {
                var dlg = new ColorDialog(_bgColor);
                dlg.Owner = this;
                if (dlg.ShowDialog() == true) ApplyBackgroundColor(dlg.Selected);
            };
            m.Items.Add(miCustom);

            m.PlacementTarget = _colorBtn;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        void ApplyBackgroundColor(Color c)
        {
            _bgColor = c;
            _rootBg.Color = Color.FromArgb(0xE2, c.R, c.G, c.B);
            _pill.Background = Solid(0xFF, c.R, c.G, c.B);
            Refresh();   // 重建卡片，让已完成卡片的浅色底纹跟随背景色
            SaveState();
        }

        // ============ 存取 ============

        void SaveState()
        {
            try
            {
                SaveData d = new SaveData();
                d.tasks = _tasks;
                d.x = Left; d.y = Top;
                d.w = _expanded ? Width : _savedW;
                d.h = _expanded ? Height : _savedH;
                d.opacity = _opacityIndex;
                d.topmost = _topmost;
                d.expanded = _expanded;
                d.ex = _expanded ? Left : (_savedX == double.MinValue ? 0 : _savedX);
                d.ey = _expanded ? Top : (_savedY == double.MinValue ? 0 : _savedY);
                d.bg = string.Format("#{0:X2}{1:X2}{2:X2}", _bgColor.R, _bgColor.G, _bgColor.B);
                d.px = _lastPillX == double.MinValue ? 0 : _lastPillX;
                d.py = _lastPillY == double.MinValue ? 0 : _lastPillY;
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_file, new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
            }
            catch { }
        }

        void LoadState()
        {
            try
            {
                if (!File.Exists(_file)) { Seed(); return; }
                SaveData d = new JavaScriptSerializer().Deserialize<SaveData>(File.ReadAllText(_file));
                if (d == null) { Seed(); return; }
                if (d.tasks != null)
                {
                    _tasks = d.tasks;
                    // 旧版本数据没有时间字段：用数据文件的最后修改时间近似补上
                    DateTime legacy = File.Exists(_file) ? File.GetLastWriteTime(_file) : DateTime.Now;
                    string ts = legacy.ToString("yyyy-MM-dd HH:mm:ss");
                    foreach (TaskItem t in _tasks)
                    {
                        if (string.IsNullOrEmpty(t.createdAt)) t.createdAt = ts;
                        if (t.status == 2 && string.IsNullOrEmpty(t.completedAt)) t.completedAt = ts;
                    }
                }
                _opacityIndex = d.opacity;
                if (_opacityIndex < 0 || _opacityIndex >= _opLevels.Length) _opacityIndex = 1;
                _topmost = d.topmost;
                _expanded = d.expanded;
                if (d.w > 150) _savedW = d.w;
                if (d.h > 150) _savedH = d.h;
                if (d.x != 0 || d.y != 0) { Left = d.x; Top = d.y; }
                if (d.ex != 0 || d.ey != 0) { _savedX = d.ex; _savedY = d.ey; }
                if (d.px != 0 || d.py != 0) { _lastPillX = d.px; _lastPillY = d.py; }
                if (!string.IsNullOrEmpty(d.bg))
                {
                    try { _bgColor = (Color)ColorConverter.ConvertFromString(d.bg); }
                    catch { _bgColor = Colors.White; }
                }
            }
            catch { Seed(); }
        }

        void Seed()
        {
            _tasks.Add(new TaskItem { title = "双击卡片可以修改任务内容", status = 0, createdAt = NowIso() });
            _tasks.Add(new TaskItem { title = "拖动卡片可以排序或跨列移动", status = 0, createdAt = NowIso() });
            _tasks.Add(new TaskItem { title = "右键卡片可置顶、编辑、删除", status = 1, createdAt = NowIso() });
            _tasks.Add(new TaskItem { title = "我已完成，可在右上角 ⇩ 导出", status = 2, createdAt = NowIso(), completedAt = NowIso() });
        }

        bool OnScreen(double x, double y)
        {
            double l = SystemParameters.VirtualScreenLeft;
            double t = SystemParameters.VirtualScreenTop;
            double r = l + SystemParameters.VirtualScreenWidth;
            double b = t + SystemParameters.VirtualScreenHeight;
            return x >= l - 60 && x <= r - 100 && y >= t - 10 && y <= b - 60;
        }

        // ============ 小工具 ============

        static Style _menuStyle, _menuItemStyle;

        ContextMenu NewMenu()
        {
            var m = new ContextMenu();
            if (_menuStyle == null)
            {
                var st = new Style(typeof(ContextMenu));
                st.Setters.Add(new Setter(ContextMenu.BackgroundProperty, Brushes.White));
                st.Setters.Add(new Setter(ContextMenu.ForegroundProperty, Solid(0xFF, 0x2F, 0x35, 0x3F)));
                st.Setters.Add(new Setter(ContextMenu.FontSizeProperty, 12.5));
                st.Setters.Add(new Setter(ContextMenu.TemplateProperty, FlatMenuTemplate()));
                _menuStyle = st;
            }
            m.Style = _menuStyle;
            return m;
        }

        MenuItem NewItem(string header)
        {
            var mi = new MenuItem { Header = header };
            if (_menuItemStyle == null)
            {
                var st = new Style(typeof(MenuItem));
                st.Setters.Add(new Setter(MenuItem.TemplateProperty, FlatMenuItemTemplate()));
                _menuItemStyle = st;
            }
            mi.Style = _menuItemStyle;
            return mi;
        }

        static ControlTemplate FlatMenuTemplate()
        {
            var tmpl = new ControlTemplate(typeof(ContextMenu));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.White);
            bd.SetValue(Border.BorderBrushProperty, Solid(0xFF, 0xE0, 0xE5, 0xEC));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            bd.SetValue(Border.PaddingProperty, new Thickness(5));
            var host = new FrameworkElementFactory(typeof(StackPanel));
            host.SetValue(StackPanel.IsItemsHostProperty, true);
            bd.AppendChild(host);
            tmpl.VisualTree = bd;
            return tmpl;
        }

        static ControlTemplate FlatMenuItemTemplate()
        {
            var tmpl = new ControlTemplate(typeof(MenuItem));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 6, 16, 6));
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var ic = new FrameworkElementFactory(typeof(ContentPresenter));
            ic.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            ic.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 7, 0));
            ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var hd = new FrameworkElementFactory(typeof(ContentPresenter));
            hd.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            hd.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(ic);
            sp.AppendChild(hd);
            bd.AppendChild(sp);
            tmpl.VisualTree = bd;

            var hl = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            var hlSet = new Setter(Border.BackgroundProperty, Solid(0xFF, 0xEC, 0xF2, 0xFD));
            hlSet.TargetName = "bd";
            hl.Setters.Add(hlSet);
            tmpl.Triggers.Add(hl);

            var press = new Trigger { Property = MenuItem.IsPressedProperty, Value = true };
            var pSet = new Setter(Border.BackgroundProperty, Solid(0xFF, 0xDC, 0xE8, 0xFB));
            pSet.TargetName = "bd";
            press.Setters.Add(pSet);
            tmpl.Triggers.Add(press);

            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            var dSet = new Setter(UIElement.OpacityProperty, 0.4);
            dSet.TargetName = "bd";
            dis.Setters.Add(dSet);
            tmpl.Triggers.Add(dis);
            return tmpl;
        }

        static Color Col(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); }

        static string NowIso() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

        static DateTime ParseTime(string s)
        {
            DateTime d;
            return DateTime.TryParse(s, out d) ? d : DateTime.MinValue;
        }

        static string Fmt(string s)
        {
            var d = ParseTime(s);
            return d == DateTime.MinValue ? "未知" : d.ToString("yyyy-MM-dd HH:mm");
        }

        static string FmtShort(string s)
        {
            var d = ParseTime(s);
            return d == DateTime.MinValue ? "" : d.ToString("MM-dd HH:mm");
        }

        // 颜色混合：t=0 返回 a，t=1 返回 b
        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        static SolidColorBrush Solid(byte a, byte r, byte g, byte b2)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b2));
            br.Freeze();
            return br;
        }

        static SolidColorBrush Solid(Color c)
        {
            var br = new SolidColorBrush(c);
            br.Freeze();
            return br;
        }

        static SolidColorBrush Tint(Color c, byte a)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            br.Freeze();
            return br;
        }

        static Style HoverBg(SolidColorBrush normal, SolidColorBrush hover)
        {
            var st = new Style(typeof(Border));
            st.Setters.Add(new Setter(Border.BackgroundProperty, normal));
            var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            tr.Setters.Add(new Setter(Border.BackgroundProperty, hover));
            st.Triggers.Add(tr);
            return st;
        }

        static TextDecorationCollection StrikePen()
        {
            var td = new TextDecoration();
            td.Pen = new Pen(Solid(0xFF, 0x9A, 0xA3, 0xAF), 1);
            td.PenThicknessUnit = TextDecorationUnit.FontRecommended;
            var list = new TextDecorationCollection();
            list.Add(td);
            return list;
        }
    }

    // 自定义导出时间段对话框
    public class ExportDialog : Window
    {
        public bool Confirmed;
        public DateTime FromDate, ToDate;
        DatePicker _from, _to;

        public ExportDialog()
        {
            Title = "导出时间段";
            Width = 330; Height = 200;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            var today = DateTime.Today;
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

            var g = new Grid { Margin = new Thickness(18) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddRow(g, 0, "完成时间从", weekStart);
            AddRow(g, 1, "到", today);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "导出", Width = 72, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_from.SelectedDate == null || _to.SelectedDate == null) return;
                FromDate = _from.SelectedDate.Value;
                ToDate = _to.SelectedDate.Value;
                if (ToDate < FromDate) { var tmp = FromDate; FromDate = ToDate; ToDate = tmp; }
                Confirmed = true;
                Close();
            };
            var cancel = new Button { Content = "取消", Width = 72, Height = 28 };
            cancel.Click += delegate(object s, RoutedEventArgs e) { Close(); };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            Grid.SetRow(btns, 3);
            g.Children.Add(btns);

            Content = g;
        }

        void AddRow(Grid g, int row, string label, DateTime val)
        {
            var lb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) };
            Grid.SetRow(lb, row);
            Grid.SetColumn(lb, 0);
            g.Children.Add(lb);
            var dp = new DatePicker { SelectedDate = val, Width = 170, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            if (row == 0) _from = dp; else _to = dp;
            Grid.SetRow(dp, row);
            Grid.SetColumn(dp, 1);
            g.Children.Add(dp);
        }
    }

    // 自定义背景颜色对话框：RGB 滑杆 + 十六进制输入 + 实时预览
    public class ColorDialog : Window
    {
        public bool Confirmed;
        public Color Selected;
        Slider _r, _g, _b;
        Border _preview;
        TextBox _hex;
        bool _syncing;

        public ColorDialog(Color initial)
        {
            Title = "自定义背景颜色";
            Width = 330;
            Height = 280;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12.5;
            Background = Brushes.White;

            var g = new Grid { Margin = new Thickness(16) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddSliderRow(g, 0, "红", initial.R, 0);
            AddSliderRow(g, 1, "绿", initial.G, 1);
            AddSliderRow(g, 2, "蓝", initial.B, 2);

            var hexLabel = new TextBlock { Text = "十六进制", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 10, 0) };
            Grid.SetRow(hexLabel, 3);
            g.Children.Add(hexLabel);
            _hex = new TextBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Text = string.Format("#{0:X2}{1:X2}{2:X2}", initial.R, initial.G, initial.B) };
            _hex.TextChanged += delegate(object s, TextChangedEventArgs e) { TryHex(); };
            Grid.SetRow(_hex, 3);
            Grid.SetColumn(_hex, 1);
            g.Children.Add(_hex);

            _preview = new Border { Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 0), Background = new SolidColorBrush(initial) };
            Grid.SetRow(_preview, 4);
            Grid.SetColumnSpan(_preview, 2);
            g.Children.Add(_preview);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "确定", Width = 72, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += delegate(object s, RoutedEventArgs e)
            {
                Confirmed = true;
                Close();
            };
            var cancel = new Button { Content = "取消", Width = 72, Height = 28 };
            cancel.Click += delegate(object s, RoutedEventArgs e) { Confirmed = false; Close(); };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            Grid.SetRow(btns, 5);
            Grid.SetColumnSpan(btns, 2);
            g.Children.Add(btns);

            Content = g;
        }

        void AddSliderRow(Grid g, int row, string label, byte val, int channel)
        {
            var lb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 0) };
            Grid.SetRow(lb, row);
            g.Children.Add(lb);
            var s = new Slider { Minimum = 0, Maximum = 255, Value = val, Margin = new Thickness(0, 6, 0, 0) };
            s.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e) { OnSlidersChanged(); };
            if (channel == 0) _r = s;
            else if (channel == 1) _g = s;
            else _b = s;
            Grid.SetRow(s, row);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
        }

        Color CurrentColor()
        {
            return Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value);
        }

        void OnSlidersChanged()
        {
            if (_syncing) return;
            _syncing = true;
            Selected = CurrentColor();
            ((SolidColorBrush)_preview.Background).Color = Selected;
            _hex.Text = string.Format("#{0:X2}{1:X2}{2:X2}", Selected.R, Selected.G, Selected.B);
            _syncing = false;
        }

        void TryHex()
        {
            if (_syncing) return;
            string t = _hex.Text.Trim().TrimStart('#');
            if (t.Length != 6) return;
            try
            {
                Color c = (Color)ColorConverter.ConvertFromString("#" + t);
                _syncing = true;
                Selected = c;
                ((SolidColorBrush)_preview.Background).Color = c;
                _r.Value = c.R;
                _g.Value = c.G;
                _b.Value = c.B;
                _syncing = false;
            }
            catch { }
        }
    }
}
