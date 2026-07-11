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
        private const double MinOpacity = 0.35;
        private const double MaxOpacity = 1.0;
        private const double OpacityStep = 0.05;

        private readonly AuthStore _authStore = new AuthStore();
        private readonly UsageApiClient _api = new UsageApiClient();
        private readonly object _dataLock = new object();

        private UsageSnapshot _snapshot = new UsageSnapshot();
        private bool _refreshing;
        private bool _dragging;
        private bool _clickThrough;
        private bool _hotkeysRegistered;
        private Point _dragStart;
        private Point _formStart;

        public OverlayForm()
        {
            InitializeComponent();
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
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
            LoadWindowPosition();
            SyncOpacityTrack();
            UpdateClickThroughMenu();
            _refreshTimer.Start();
            _clockTimer.Start();
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
            if (_snapshot?.HasData == true || _clickThrough)
                Invalidate();
        }

        private void BeginRefresh()
        {
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
                BeginInvoke((MethodInvoker)Invalidate);
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
            using (var fill = new SolidBrush(Color.FromArgb(210, 18, 20, 26)))
            using (var border = new Pen(Color.FromArgb(120, 90, 110, 140), 1f))
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
            var smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);

            try
            {
                var title = "Codex Usage";
                var plan = string.IsNullOrWhiteSpace(snap.PlanType) ? "-" : snap.PlanType.ToUpperInvariant();
                g.DrawString(title, titleFont, Brushes.White, 14, 10);
                g.DrawString(plan, smallFont, new SolidBrush(Color.FromArgb(180, 170, 190, 220)), bounds.Width - 70, 12);

                var mode = _clickThrough ? "PASSTHR" : "MOVE";
                var modeColor = _clickThrough
                    ? Color.FromArgb(255, 255, 190, 90)
                    : Color.FromArgb(255, 120, 200, 255);
                g.DrawString(mode, smallFont, new SolidBrush(modeColor), 14, 26);

                if (!string.IsNullOrWhiteSpace(snap.Error))
                {
                    g.DrawString(snap.Error, labelFont, new SolidBrush(Color.FromArgb(255, 255, 120, 120)), 14, 44);
                    DrawFooter(g, snap, smallFont);
                    return;
                }

                if (!snap.HasData)
                {
                    g.DrawString("Loading...", labelFont, Brushes.Gainsboro, 14, 44);
                    DrawFooter(g, snap, smallFont);
                    return;
                }

                var status = snap.LimitReached ? "LIMIT REACHED" : (snap.Allowed ? "OK" : "BLOCKED");
                var statusColor = snap.LimitReached
                    ? Color.FromArgb(255, 255, 90, 90)
                    : Color.FromArgb(255, 90, 220, 140);
                g.DrawString(status, valueFont, new SolidBrush(statusColor), bounds.Width - 110, 10);

                DrawUsageBar(g, labelFont, valueFont, 14, 42, "5h window",
                    snap.Primary, Color.FromArgb(255, 64, 156, 255));
                DrawUsageBar(g, labelFont, valueFont, 14, 82, "7d window",
                    snap.Secondary, Color.FromArgb(255, 120, 90, 255));

                DrawFooter(g, snap, smallFont);
            }
            finally
            {
                titleFont.Dispose();
                labelFont.Dispose();
                valueFont.Dispose();
                smallFont.Dispose();
            }
        }

        private void DrawFooter(Graphics g, UsageSnapshot snap, Font smallFont)
        {
            var local = snap.FetchedAtUtc.ToLocalTime();
            var text = string.Format("{0}% | {1} | Updated {2}",
                (int)Math.Round(Opacity * 100),
                _clickThrough ? "wheel/hotkey only" : "drag to move",
                local.ToString("HH:mm:ss"));

            if (snap.HasData && snap.Primary != null)
                text += " | 5h reset " + FormatCountdown(snap.Primary.ResetAfterSeconds);

            g.DrawString(text, smallFont, new SolidBrush(Color.FromArgb(170, 170, 180, 200)), 14, ClientSize.Height - 22);
        }

        private static void DrawUsageBar(Graphics g, Font labelFont, Font valueFont, int x, int y, string label, UsageWindow window, Color barColor)
        {
            var percent = window?.UsedPercent ?? 0;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            g.DrawString(label, labelFont, Brushes.Gainsboro, x, y);
            var pctText = percent.ToString("0.#") + "%";
            var pctSize = g.MeasureString(pctText, valueFont);
            g.DrawString(pctText, valueFont, Brushes.White, 310 - pctSize.Width, y);

            var barRect = new Rectangle(x, y + 18, 296, 10);
            using (var track = new SolidBrush(Color.FromArgb(90, 40, 45, 58)))
                g.FillRectangle(track, barRect);

            var fillWidth = (int)Math.Round(barRect.Width * (percent / 100.0));
            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height);
                using (var brush = new LinearGradientBrush(fillRect, barColor, ShiftColor(barColor, 40), LinearGradientMode.Horizontal))
                    g.FillRectangle(brush, fillRect);
            }

            using (var frame = new Pen(Color.FromArgb(80, 255, 255, 255)))
                g.DrawRectangle(frame, barRect);
        }

        private static Color ShiftColor(Color c, int delta)
        {
            return Color.FromArgb(c.A,
                Math.Min(255, c.R + delta),
                Math.Min(255, c.G + delta),
                Math.Min(255, c.B + delta));
        }

        private static string FormatCountdown(long seconds)
        {
            if (seconds <= 0)
                return "now";

            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return string.Format("{0}h {1}m", (int)ts.TotalHours, ts.Minutes);
            if (ts.TotalMinutes >= 1)
                return string.Format("{0}m {1}s", ts.Minutes, ts.Seconds);
            return ts.Seconds + "s";
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
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

            if (e.Button == MouseButtons.Middle)
            {
                ToggleClickThrough();
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            _dragging = true;
            _dragStart = e.Location;
            _formStart = Location;
        }

        private void OverlayForm_MouseMove(object sender, MouseEventArgs e)
        {
            if (_clickThrough || !_dragging)
                return;

            var dx = e.X - _dragStart.X;
            var dy = e.Y - _dragStart.Y;
            Location = new Point(_formStart.X + dx, _formStart.Y + dy);
        }

        private void OverlayForm_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                SaveWindowSettings();
            }
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
                SyncOpacityTrack();
        }

        private void ToggleClickThrough()
        {
            SetClickThrough(!_clickThrough, persist: true);
        }

        private void SetClickThrough(bool enabled, bool persist)
        {
            _clickThrough = enabled;
            if (_dragging)
                _dragging = false;

            ApplyClickThrough();
            UpdateClickThroughMenu();
            Invalidate();

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

        private void LoadWindowPosition()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key == null)
                    {
                        PlaceDefault();
                        return;
                    }

                    var x = (int)(key.GetValue("X") ?? -1);
                    var y = (int)(key.GetValue("Y") ?? -1);
                    var opacity = Convert.ToDouble(key.GetValue("Opacity") ?? 0.88);
                    var clickThrough = Convert.ToInt32(key.GetValue("ClickThrough") ?? 0) == 1;

                    SetOpacity(opacity, persist: false);
                    _clickThrough = clickThrough;

                    if (x < 0 || y < 0)
                    {
                        PlaceDefault();
                        return;
                    }

                    var screen = Screen.FromPoint(new Point(x, y));
                    x = Math.Max(screen.WorkingArea.Left, Math.Min(x, screen.WorkingArea.Right - Width));
                    y = Math.Max(screen.WorkingArea.Top, Math.Min(y, screen.WorkingArea.Bottom - Height));
                    Location = new Point(x, y);
                }
            }
            catch
            {
                PlaceDefault();
            }
        }

        private void PlaceDefault()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 24, area.Top + 24);
        }

        private void SaveWindowSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
                {
                    key.SetValue("X", Location.X);
                    key.SetValue("Y", Location.Y);
                    key.SetValue("Opacity", Opacity);
                    key.SetValue("ClickThrough", _clickThrough ? 1 : 0);
                }
            }
            catch
            {
                // ignore
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKeys();
            SaveWindowSettings();
            base.OnFormClosing(e);
        }
    }
}
