using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexUsageMonitor.Models;
using CodexUsageMonitor.Services;
using Microsoft.Win32;

namespace CodexUsageMonitor.UI
{
    public partial class OverlayForm : Form
    {
        private const string AnalyticsUrl = "https://chatgpt.com/codex/cloud/settings/analytics";
        private const string RegistryKey = @"Software\CodexUsageMonitor";
        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRunValueName = "CodexUsageMonitor";
        private const double MinOpacity = 0.35;
        private const double MaxOpacity = 1.0;
        private const double OpacityStep = 0.05;
        private const int FixedMargin = 24;
        private const int MinVisibleWidth = 80;
        private const int MinVisibleHeight = 40;
        private const int MeterTop = 48;
        private const int MeterSpacing = 52;
        private const int LayoutBaseHeight = 80;
        private const int MinClientHeight = 136;
        private const string ToggleClickableHotkeyText = "Ctrl+Alt+T: make clickable";
        private const string TogglePassthroughHotkeyText = "Ctrl+Alt+T: make click-through";

        private readonly AuthStore _authStore = new AuthStore();
        private readonly UsageApiClient _api = new UsageApiClient();
        private readonly object _dataLock = new object();
        private readonly System.Windows.Forms.Timer _hoverTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip _hotkeyToolTip = new ToolTip();

        private UsageSnapshot _snapshot = new UsageSnapshot();
        private bool _refreshing;
        private bool _clickThrough = true;
        private bool _hotkeysRegistered;
        private bool _hotkeyToolTipVisible;
        private bool _dragging;
        private bool _loadedSavedLocation;
        private bool _refreshTimerWasEnabledBeforeDrag;
        private bool _pendingRefreshAfterDrag;
        private Point _dragStartCursor;
        private Point _dragStartLocation;
        private string _hotkeyToolTipText;

        public OverlayForm()
        {
            InitializeComponent();
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            _hoverTimer.Interval = 250;
            _hoverTimer.Tick += HoverTimer_Tick;
            _hotkeyToolTip.AutomaticDelay = 100;
            _hotkeyToolTip.AutoPopDelay = 4000;
            _hotkeyToolTip.InitialDelay = 100;
            _hotkeyToolTip.ReshowDelay = 100;
            _hotkeyToolTip.ShowAlways = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WsExLayered;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyClickThrough();
            RegisterHotKeys();
        }

        private void OverlayForm_Load(object sender, EventArgs e)
        {
            LoadWindowSettings();
            EnsureVisibleOrPlaceDefault();
            SyncOpacityTrack();
            UpdateClickThroughMenu();
            UpdateAutoStartMenu();
            _refreshTimer.Start();
            _clockTimer.Start();
            _hoverTimer.Start();
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            BeginRefresh();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WmHotkey)
            {
                switch (m.WParam.ToInt32())
                {
                    case NativeMethods.HotkeyTogglePassthrough:
                        ToggleClickThrough();
                        return;
                    case NativeMethods.HotkeyOpacityUp:
                        AdjustOpacity(OpacityStep);
                        return;
                    case NativeMethods.HotkeyOpacityDown:
                        AdjustOpacity(-OpacityStep);
                        return;
                }
            }

            base.WndProc(ref m);
        }

        private void RegisterHotKeys()
        {
            if (_hotkeysRegistered || !IsHandleCreated)
                return;

            var mod = NativeMethods.ModControl | NativeMethods.ModAlt;
            _hotkeysRegistered =
                NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyTogglePassthrough, mod, 0x54) &&
                NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyOpacityUp, mod, 0xBB) &&
                NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyOpacityDown, mod, 0xBD);
        }

        private void UnregisterHotKeys()
        {
            if (!IsHandleCreated || !_hotkeysRegistered)
                return;

            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyTogglePassthrough);
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyOpacityUp);
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyOpacityDown);
            _hotkeysRegistered = false;
        }

        private void RefreshTimer_Tick(object sender, EventArgs e) => BeginRefresh();

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            if (_dragging)
                return;

            if (_snapshot?.HasData == true || _clickThrough)
                Invalidate();
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized || _dragging)
                return;

            var cursor = Cursor.Position;
            if (!Bounds.Contains(cursor))
            {
                HideHotkeyToolTip();
                return;
            }

            var text = _clickThrough ? ToggleClickableHotkeyText : TogglePassthroughHotkeyText;
            if (_hotkeyToolTipVisible && _hotkeyToolTipText == text)
                return;

            var local = PointToClient(cursor);
            local.Offset(12, 18);
            _hotkeyToolTip.Show(text, this, local, 2500);
            _hotkeyToolTipVisible = true;
            _hotkeyToolTipText = text;
        }

        private void HideHotkeyToolTip()
        {
            if (!_hotkeyToolTipVisible)
                return;

            _hotkeyToolTip.Hide(this);
            _hotkeyToolTipVisible = false;
            _hotkeyToolTipText = null;
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e) => EnsureVisibleOrPlaceDefault();

        private void BeginRefresh()
        {
            if (_dragging)
            {
                _pendingRefreshAfterDrag = true;
                return;
            }

            if (_refreshing)
                return;

            _refreshing = true;
            Task.Run(() =>
            {
                try
                {
                    if (!_authStore.TryLoad(out var token, out var accountId, out var authError))
                    {
                        UpdateSnapshot(new UsageSnapshot { Error = authError, FetchedAtUtc = DateTime.UtcNow });
                        return;
                    }

                    var snapshot = _api.FetchAsync(token, accountId, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    UpdateSnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    UpdateSnapshot(new UsageSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow });
                }
                finally
                {
                    _refreshing = false;
                }
            });
        }

        private void UpdateSnapshot(UsageSnapshot snapshot)
        {
            lock (_dataLock)
            {
                _snapshot = snapshot ?? new UsageSnapshot();
            }

            if (IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (!_dragging && !IsDisposed)
                    {
                        ApplySnapshotLayout(snapshot);
                        Invalidate();
                    }
                }));
            }
            catch
            {
                // form closing
            }
        }

        private void OverlayForm_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            using (var path = RoundedRect(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), 12))
            using (var fill = new LinearGradientBrush(bounds,
                Color.FromArgb(238, 16, 19, 24),
                Color.FromArgb(238, 28, 32, 38),
                LinearGradientMode.ForwardDiagonal))
            using (var border = new Pen(Color.FromArgb(145, 92, 112, 132), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            UsageSnapshot snap;
            lock (_dataLock)
                snap = _snapshot;

            var titleFont = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            var valueFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            var percentFont = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
            var smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);

            try
            {
                DrawHeader(g, snap, titleFont, smallFont, bounds);

                if (!string.IsNullOrWhiteSpace(snap.Error))
                {
                    DrawMessage(g, labelFont, snap.Error);
                    DrawFooter(g, snap, smallFont);
                    return;
                }

                if (!snap.HasData)
                {
                    DrawMessage(g, labelFont, "Loading usage...");
                    DrawFooter(g, snap, smallFont);
                    return;
                }

                DrawStatusBadge(g, valueFont, smallFont, bounds, snap);
                var windows = GetUsageWindows(snap);
                for (var i = 0; i < windows.Count; i++)
                {
                    var item = windows[i];

                    DrawHudMeter(g, labelFont, valueFont, percentFont, smallFont, 14, MeterTop + (i * MeterSpacing), bounds.Width - 28,
                        FormatUsageTitle(item.Window),
                        FormatPeriodBadge(item.Window),
                        FormatWindowLabel(item.Window),
                        item.Window);
                }

                DrawFooter(g, snap, smallFont);
            }
            finally
            {
                titleFont.Dispose();
                labelFont.Dispose();
                valueFont.Dispose();
                percentFont.Dispose();
                smallFont.Dispose();
            }
        }

        private void ApplySnapshotLayout(UsageSnapshot snap)
        {
            var windowCount = Math.Max(1, GetUsageWindows(snap).Count);
            var targetHeight = Math.Max(MinClientHeight, LayoutBaseHeight + (windowCount * MeterSpacing));
            if (ClientSize.Height != targetHeight)
                ClientSize = new Size(ClientSize.Width, targetHeight);
        }

        private static System.Collections.Generic.List<UsageLimitWindow> GetUsageWindows(UsageSnapshot snap)
        {
            var windows = new System.Collections.Generic.List<UsageLimitWindow>();
            if (snap == null)
                return windows;

            if (snap.Windows != null && snap.Windows.Count > 0)
            {
                windows.AddRange(snap.Windows);
                SortUsageWindows(windows);
                return windows;
            }

            AddFallbackUsageWindow(windows, "primary_window", snap.Primary);
            AddFallbackUsageWindow(windows, "secondary_window", snap.Secondary);
            SortUsageWindows(windows);
            return windows;
        }

        private static void SortUsageWindows(System.Collections.Generic.List<UsageLimitWindow> windows)
        {
            windows.Sort((left, right) =>
            {
                var usedCompare = right.Window.UsedPercent.CompareTo(left.Window.UsedPercent);
                if (usedCompare != 0)
                    return usedCompare;

                return left.Window.LimitWindowSeconds.CompareTo(right.Window.LimitWindowSeconds);
            });
        }

        private static void AddFallbackUsageWindow(
            System.Collections.Generic.List<UsageLimitWindow> windows,
            string key,
            UsageWindow window)
        {
            if (window == null || window.LimitWindowSeconds <= 0)
                return;

            windows.Add(new UsageLimitWindow
            {
                Key = key,
                Window = window
            });
        }

        private static string FormatWindowLabel(UsageWindow window)
        {
            if (window == null || window.LimitWindowSeconds <= 0)
                return "no data";

            var seconds = window.LimitWindowSeconds;
            if (seconds % (24 * 60 * 60) == 0)
                return (seconds / (24 * 60 * 60)) + "d";
            if (seconds % (60 * 60) == 0)
                return (seconds / (60 * 60)) + "h";
            if (seconds % 60 == 0)
                return (seconds / 60) + "m";

            return seconds + "s";
        }

        private static string FormatUsageTitle(UsageWindow window)
        {
            if (window == null || window.LimitWindowSeconds <= 0)
                return "Usage";

            var seconds = window.LimitWindowSeconds;
            if (seconds == 24 * 60 * 60)
                return "Daily";
            if (seconds >= 6 * 24 * 60 * 60 && seconds <= 8 * 24 * 60 * 60)
                return "Weekly";

            return FormatWindowLabel(window);
        }

        private static string FormatPeriodBadge(UsageWindow window)
        {
            if (window == null || window.LimitWindowSeconds <= 0)
                return "LIVE";

            var seconds = window.LimitWindowSeconds;
            if (seconds == 24 * 60 * 60)
                return "DAY";
            if (seconds >= 6 * 24 * 60 * 60 && seconds <= 8 * 24 * 60 * 60)
                return "WEEKLY";

            return FormatWindowLabel(window).ToUpperInvariant();
        }

        private void DrawHeader(Graphics g, UsageSnapshot snap, Font titleFont, Font smallFont, Rectangle bounds)
        {
            using (var titleBrush = new SolidBrush(Color.White))
            using (var subBrush = new SolidBrush(Color.FromArgb(185, 176, 190, 204)))
            {
                g.DrawString("Codex Usage", titleFont, titleBrush, 14, 10);

                var plan = string.IsNullOrWhiteSpace(snap.PlanType) ? "-" : snap.PlanType.ToUpperInvariant();
                DrawTrimmedText(g, plan, smallFont, subBrush, new Rectangle(bounds.Width - 104, 12, 90, 14), StringAlignment.Far);
            }

            var mode = _clickThrough ? "VIEW" : "EDIT";
            var modeColor = _clickThrough
                ? Color.FromArgb(255, 100, 210, 186)
                : Color.FromArgb(255, 104, 176, 255);
            using (var modeBrush = new SolidBrush(modeColor))
                g.DrawString(mode, smallFont, modeBrush, 14, 27);
        }

        private void DrawMessage(Graphics g, Font font, string text)
        {
            using (var brush = new SolidBrush(Color.FromArgb(235, 255, 178, 190)))
                DrawTrimmedText(g, text, font, brush, new Rectangle(16, 62, ClientSize.Width - 32, 36), StringAlignment.Near);
        }

        private static void DrawStatusBadge(Graphics g, Font valueFont, Font smallFont, Rectangle bounds, UsageSnapshot snap)
        {
            var text = snap.LimitReached ? "LIMIT" : (snap.Allowed ? "READY" : "BLOCKED");
            var color = snap.LimitReached
                ? Color.FromArgb(255, 255, 91, 91)
                : (snap.Allowed ? Color.FromArgb(255, 101, 231, 145) : Color.FromArgb(255, 255, 174, 76));

            var badge = new Rectangle(bounds.Width - 84, 25, 70, 16);
            using (var path = RoundedRect(badge, 8))
            using (var fill = new SolidBrush(Color.FromArgb(54, color)))
            using (var outline = new Pen(Color.FromArgb(160, color), 1f))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(fill, path);
                g.DrawPath(outline, path);
                DrawTrimmedText(g, text, smallFont, brush, badge, StringAlignment.Center);
            }
        }

        private static void DrawHudMeter(
            Graphics g,
            Font labelFont,
            Font valueFont,
            Font percentFont,
            Font smallFont,
            int x,
            int y,
            int width,
            string title,
            string periodLabel,
            string windowLabel,
            UsageWindow window)
        {
            var hasData = window != null;
            var used = hasData ? GetUsedPercent(window) : 0;
            var accent = GetUsageColor(used, hasData);
            var accentEnd = ShiftColor(accent, 28);
            var barRect = new Rectangle(x, y + 29, width, 12);
            var percentText = hasData ? used.ToString("0.#") + "%" : "--";
            var percentRect = new Rectangle(x + width - 74, y - 2, 74, 24);

            using (var labelBrush = new SolidBrush(Color.FromArgb(230, 238, 242, 250)))
            using (var smallBrush = new SolidBrush(Color.FromArgb(166, 178, 190, 204)))
            using (var valueBrush = new SolidBrush(Color.White))
            {
                DrawTrimmedText(g, title, valueFont, labelBrush, new Rectangle(x, y, 84, 18), StringAlignment.Near);
                var pillWidth = Math.Max(34, Math.Min(70, (periodLabel.Length * 7) + 12));
                var pillX = x + 88;
                DrawMiniPill(g, smallFont, periodLabel, pillX, y + 1, pillWidth,
                    Color.FromArgb(hasData ? 55 : 34, accent), Color.FromArgb(hasData ? 150 : 90, accent));
                g.DrawString(windowLabel + " window", smallFont, smallBrush, pillX + pillWidth + 8, y + 2);
                DrawTrimmedText(g, percentText, percentFont, valueBrush, percentRect, StringAlignment.Far);
            }

            using (var usedBrush = new SolidBrush(Color.FromArgb(150, 184, 194, 206)))
                DrawTrimmedText(g, "used", smallFont, usedBrush, new Rectangle(x + width - 44, y + 22, 44, 12), StringAlignment.Far);

            using (var trackPath = RoundedRect(barRect, 6))
            using (var track = new SolidBrush(Color.FromArgb(170, 38, 43, 50)))
            using (var frame = new Pen(Color.FromArgb(80, 220, 230, 240), 1f))
            {
                g.FillPath(track, trackPath);

                var fillWidth = (int)Math.Round(barRect.Width * (used / 100.0));
                if (fillWidth > 0)
                {
                    var fillRect = new Rectangle(barRect.X, barRect.Y, Math.Max(3, fillWidth), barRect.Height);
                    using (var fillPath = RoundedRect(fillRect, 6))
                    using (var fill = new LinearGradientBrush(fillRect, accent, accentEnd, LinearGradientMode.Horizontal))
                    {
                        g.FillPath(fill, fillPath);
                    }
                }

                for (var i = 1; i < 5; i++)
                {
                    var tickX = barRect.X + (barRect.Width * i / 5);
                    using (var tick = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                        g.DrawLine(tick, tickX, barRect.Y + 3, tickX, barRect.Bottom - 3);
                }

                g.DrawPath(frame, trackPath);
            }
        }

        private static void DrawMiniPill(Graphics g, Font font, string text, int x, int y, int width, Color fillColor, Color borderColor)
        {
            var rect = new Rectangle(x, y, width, 14);
            using (var path = RoundedRect(rect, 7))
            using (var fill = new SolidBrush(fillColor))
            using (var border = new Pen(borderColor, 1f))
            using (var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                DrawTrimmedText(g, text, font, brush, rect, StringAlignment.Center);
            }
        }

        private void DrawFooter(Graphics g, UsageSnapshot snap, Font smallFont)
        {
            var local = snap.FetchedAtUtc.ToLocalTime();
            var text = string.Format("Opacity {0}% | Ctrl+Alt+T | {1}",
                (int)Math.Round(Opacity * 100),
                local.ToString("HH:mm:ss"));

            if (snap.HasData)
            {
                var windows = GetUsageWindows(snap);
                for (var i = 0; i < windows.Count; i++)
                {
                    var window = windows[i].Window;
                    text += " | " + FormatWindowLabel(window) + " " + FormatCountdown(window.ResetAfterSeconds);
                }
            }

            using (var brush = new SolidBrush(Color.FromArgb(170, 184, 194, 214)))
                DrawTrimmedText(g, text, smallFont, brush, new Rectangle(14, ClientSize.Height - 22, ClientSize.Width - 28, 14), StringAlignment.Near);
        }

        private static double ClampPercent(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static double GetUsedPercent(UsageWindow window)
        {
            if (window == null)
                return 0;

            return ClampPercent(window.UsedPercent);
        }

        private static Color GetUsageColor(double usedPercent, bool hasData)
        {
            if (!hasData)
                return Color.FromArgb(255, 96, 105, 118);
            if (usedPercent >= 90)
                return Color.FromArgb(255, 244, 92, 92);
            if (usedPercent >= 70)
                return Color.FromArgb(255, 245, 172, 74);

            return Color.FromArgb(255, 80, 204, 176);
        }

        private static Color ShiftColor(Color color, int delta)
        {
            return Color.FromArgb(color.A,
                Math.Max(0, Math.Min(255, color.R + delta)),
                Math.Max(0, Math.Min(255, color.G + delta)),
                Math.Max(0, Math.Min(255, color.B + delta)));
        }

        private static void DrawTrimmedText(Graphics g, string text, Font font, Brush brush, Rectangle bounds, StringAlignment alignment)
        {
            using (var format = new StringFormat())
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        private static string FormatCountdown(long seconds)
        {
            if (seconds <= 0)
                return "now";

            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1)
                return string.Format("{0}d | {1:00}h | {2:00}m", (int)ts.TotalDays, ts.Hours, ts.Minutes);
            if (ts.TotalHours >= 1)
                return string.Format("{0:00}h | {1:00}m", (int)ts.TotalHours, ts.Minutes);
            if (ts.TotalMinutes >= 1)
                return string.Format("{0:00}m | {1:00}s", ts.Minutes, ts.Seconds);
            return ts.Seconds + "s";
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return path;

            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            var d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void OverlayForm_MouseDown(object sender, MouseEventArgs e)
        {
            if (_clickThrough)
                return;

            if (e.Button == MouseButtons.Left)
            {
                BeginDrag();
                return;
            }

            if (e.Button == MouseButtons.Middle)
            {
                ToggleClickThrough();
            }
        }

        private void OverlayForm_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;

            var delta = new Size(Cursor.Position.X - _dragStartCursor.X, Cursor.Position.Y - _dragStartCursor.Y);
            Location = _dragStartLocation + delta;
        }

        private void OverlayForm_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                EndDrag();
        }

        private void OverlayForm_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_clickThrough)
                return;

            AdjustOpacity(e.Delta > 0 ? OpacityStep : -OpacityStep);
        }

        private void OverlayForm_DoubleClick(object sender, EventArgs e)
        {
            if (_clickThrough)
                return;

            OpenAnalyticsPage();
        }

        private void MiRefresh_Click(object sender, EventArgs e) => BeginRefresh();

        private void MiClickThrough_Click(object sender, EventArgs e) => ToggleClickThrough();

        private void MiAutoStart_Click(object sender, EventArgs e) => ToggleAutoStart();

        private void MiOpenWeb_Click(object sender, EventArgs e) => OpenAnalyticsPage();

        private void MiExit_Click(object sender, EventArgs e) => Close();

        private void OpacityTrack_Scroll(object sender, EventArgs e)
        {
            SetOpacity(_opacityTrack.Value / 100.0, persist: true);
        }

        private void ContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_clickThrough)
                e.Cancel = true;
            else
            {
                SyncOpacityTrack();
                UpdateAutoStartMenu();
            }
        }

        private void ToggleClickThrough()
        {
            SetClickThrough(!_clickThrough, persist: true);
        }

        private void SetClickThrough(bool enabled, bool persist)
        {
            _clickThrough = enabled;

            ApplyClickThrough();
            UpdateClickThroughMenu();
            Invalidate();
            HideHotkeyToolTip();

            if (persist)
                SaveWindowSettings();
        }

        private void ApplyClickThrough()
        {
            if (!IsHandleCreated)
                return;

            var style = NativeMethods.GetWindowLong(Handle, NativeMethods.GwlExStyle);
            if (_clickThrough)
                style |= NativeMethods.WsExTransparent;
            else
                style &= ~NativeMethods.WsExTransparent;

            NativeMethods.SetWindowLong(Handle, NativeMethods.GwlExStyle, style);
        }

        private void UpdateClickThroughMenu()
        {
            _miClickThrough.Checked = _clickThrough;
            _miClickThrough.Text = _clickThrough
                ? "Click-through ON (Ctrl+Alt+T)"
                : "Click-through OFF (Ctrl+Alt+T)";
        }

        private void ToggleAutoStart()
        {
            var enabled = IsAutoStartEnabled();

            try
            {
                SetAutoStart(!enabled);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Unable to update startup setting.\r\n\r\n" + ex.Message,
                    "Codex Usage",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            UpdateAutoStartMenu();
        }

        private void UpdateAutoStartMenu()
        {
            var enabled = IsAutoStartEnabled();
            _miAutoStart.Checked = enabled;
            _miAutoStart.Text = enabled
                ? "Auto run after startup ON"
                : "Auto run after startup OFF";
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: false))
                {
                    var command = key?.GetValue(StartupRunValueName) as string;
                    if (string.IsNullOrWhiteSpace(command))
                        return false;

                    var startupPath = ExtractExecutablePath(command);
                    return string.Equals(startupPath, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SetAutoStart(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(StartupRunKey))
            {
                if (key == null)
                    throw new InvalidOperationException("Could not open the Windows startup registry key.");

                if (enabled)
                    key.SetValue(StartupRunValueName, "\"" + Application.ExecutablePath + "\"");
                else
                    key.DeleteValue(StartupRunValueName, throwOnMissingValue: false);
            }
        }

        private static string ExtractExecutablePath(string command)
        {
            command = (command ?? string.Empty).Trim();
            if (command.Length == 0)
                return string.Empty;

            if (command[0] == '"')
            {
                var endQuote = command.IndexOf('"', 1);
                return endQuote > 1 ? command.Substring(1, endQuote - 1) : command.Trim('"');
            }

            var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
                return command.Substring(0, exeIndex + 4);

            var firstSpace = command.IndexOf(' ');
            return firstSpace >= 0 ? command.Substring(0, firstSpace) : command;
        }

        private void AdjustOpacity(double delta)
        {
            SetOpacity(Opacity + delta, persist: true);
        }

        private void SetOpacity(double value, bool persist)
        {
            var clamped = Math.Max(MinOpacity, Math.Min(MaxOpacity, value));
            Opacity = clamped;
            SyncOpacityTrack();
            Invalidate();

            if (persist)
                SaveWindowSettings();
        }

        private void SyncOpacityTrack()
        {
            var trackValue = (int)Math.Round(Opacity * 100);
            trackValue = Math.Max(_opacityTrack.Minimum, Math.Min(_opacityTrack.Maximum, trackValue));
            if (_opacityTrack.Value != trackValue)
                _opacityTrack.Value = trackValue;

            _miOpacityHost.Text = "Opacity " + trackValue + "%";
        }

        private static void OpenAnalyticsPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(AnalyticsUrl) { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        private void LoadWindowSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key == null)
                        return;

                    var opacity = Convert.ToDouble(key.GetValue("Opacity") ?? 0.88);
                    var clickThrough = Convert.ToInt32(key.GetValue("ClickThrough") ?? 1) != 0;
                    var xValue = key.GetValue("X");
                    var yValue = key.GetValue("Y");

                    SetOpacity(opacity, persist: false);
                    SetClickThrough(clickThrough, persist: false);

                    if (xValue != null && yValue != null)
                    {
                        Location = new Point(Convert.ToInt32(xValue), Convert.ToInt32(yValue));
                        _loadedSavedLocation = true;
                    }
                }
            }
            catch
            {
                // keep defaults
            }
        }

        private void EnsureVisibleOrPlaceDefault()
        {
            if (_loadedSavedLocation && HasUsableVisibleArea(Bounds))
                return;

            PlaceDefault();
            _loadedSavedLocation = true;
        }

        private void PlaceDefault()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var target = new Point(area.Right - Width - FixedMargin, area.Top + FixedMargin);
            if (Location != target)
                Location = target;
        }

        private static bool HasUsableVisibleArea(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            foreach (var screen in Screen.AllScreens)
            {
                var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
                if (visible.Width >= Math.Min(MinVisibleWidth, bounds.Width) &&
                    visible.Height >= Math.Min(MinVisibleHeight, bounds.Height))
                    return true;
            }

            return false;
        }

        private void BeginDrag()
        {
            _dragging = true;
            _pendingRefreshAfterDrag = false;
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
            _refreshTimerWasEnabledBeforeDrag = _refreshTimer.Enabled;
            _refreshTimer.Stop();
            HideHotkeyToolTip();
            Capture = true;
        }

        private void EndDrag()
        {
            if (!_dragging)
                return;

            _dragging = false;
            Capture = false;
            EnsureVisibleOrPlaceDefault();
            SaveWindowSettings();

            if (_refreshTimerWasEnabledBeforeDrag)
                _refreshTimer.Start();

            if (_pendingRefreshAfterDrag)
            {
                _pendingRefreshAfterDrag = false;
                BeginRefresh();
            }

            Invalidate();
        }

        private void SaveWindowSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
                {
                    key.SetValue("Opacity", Opacity);
                    key.SetValue("ClickThrough", _clickThrough ? 1 : 0);
                    if (HasUsableVisibleArea(Bounds))
                    {
                        key.SetValue("X", Location.X);
                        key.SetValue("Y", Location.Y);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            HideHotkeyToolTip();
            UnregisterHotKeys();
            SaveWindowSettings();
            base.OnFormClosing(e);
        }
    }
}
