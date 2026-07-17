using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
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
        private const string CursorDashboardUrl = "https://cursor.com/dashboard/usage";
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
        private const int ColumnDividerX = 380;
        private const int SingleColumnWidth = ColumnDividerX;
        private const int DualColumnWidth = ColumnDividerX * 2;
        private const int ColumnInnerPadding = 14;
        private const int ExitButtonSize = 18;
        private const int ExitButtonMargin = 8;
        private const int ResetCreditsPanelHeaderHeight = 24;
        private const int ResetCreditRowHeight = 26;
        private const int ResetCreditsPanelBottomPadding = 8;
        private const string ToggleClickableHotkeyText = "Ctrl+Alt+T: make clickable, then right-click for Cursor, reset dates & opacity | Ctrl+Alt+Q: exit";
        private const string TogglePassthroughHotkeyText = "Right-click: Cursor, reset dates & opacity | wheel: opacity | Ctrl+Alt+T: click-through | Ctrl+Alt+Q: exit";
        private const string ExitButtonTooltipText = "Exit widget";
        private static readonly TimeSpan NormalRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ResetCreditsRefreshInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FirstErrorBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SecondErrorBackoff = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MaximumErrorBackoff = TimeSpan.FromMinutes(5);
        private static readonly string[] ThaiMonthAbbreviations =
        {
            string.Empty,
            "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.",
            "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค."
        };

        private readonly AuthStore _authStore = new AuthStore();
        private readonly CursorAuthStore _cursorAuthStore = new CursorAuthStore();
        private readonly UsageApiClient _api = new UsageApiClient();
        private readonly CursorUsageApiClient _cursorApi = new CursorUsageApiClient();
        private readonly object _dataLock = new object();
        private readonly System.Windows.Forms.Timer _hoverTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip _hotkeyToolTip = new ToolTip();
        private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();

        private CombinedUsageSnapshot _snapshot = new CombinedUsageSnapshot();
        private RateLimitResetCreditsSnapshot _resetCreditsSnapshot = new RateLimitResetCreditsSnapshot();
        private bool _clickThrough = true;
        private bool _cursorEnabled;
        private bool _showResetExpiryDates;
        private bool _closing;
        private bool _hotkeysRegistered;
        private bool _hotkeyToolTipVisible;
        private bool _dragging;
        private bool _loadedSavedLocation;
        private bool _refreshTimerWasEnabledBeforeDrag;
        private bool _pendingRefreshAfterDrag;
        private bool _pendingManualRefreshAfterDrag;
        private bool _codexRefreshInFlight;
        private bool _cursorRefreshInFlight;
        private bool _resetCreditsRefreshInFlight;
        private bool _codexRefreshPending;
        private bool _cursorRefreshPending;
        private bool _resetCreditsRefreshPending;
        private int _codexFailureCount;
        private int _cursorFailureCount;
        private int _resetCreditsFailureCount;
        private int _cursorGeneration;
        private int _resetCreditsGeneration;
        private int _codexAuthChanged;
        private int _cursorAuthChanged;
        private DateTime _nextCodexRefreshUtc = DateTime.MinValue;
        private DateTime _nextCursorRefreshUtc = DateTime.MinValue;
        private DateTime _nextResetCreditsRefreshUtc = DateTime.MinValue;
        private Point _dragStartCursor;
        private Point _dragStartLocation;
        private string _hotkeyToolTipText;
        private Rectangle _exitButtonRect = Rectangle.Empty;
        private bool _exitButtonHovered;
        private EventWaitHandle _exitEvent;
        private RegisteredWaitHandle _exitWaitHandle;
        private CancellationTokenSource _cursorRequestCancellation;
        private CancellationTokenSource _resetCreditsRequestCancellation;
        private FileSystemWatcher _codexAuthWatcher;
        private FileSystemWatcher _cursorAuthWatcher;

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
            UpdateCursorMenu();
            UpdateResetCreditsMenu();
            UpdateAutoStartMenu();
            EnsureAuthWatchers();
            _refreshTimer.Start();
            _clockTimer.Start();
            _hoverTimer.Start();
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            SetupExitListener();
            ScheduleRefreshes(force: true);
        }

        private void SetupExitListener()
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\CodexUsageMonitor.Exit");
            _exitWaitHandle = ThreadPool.RegisterWaitForSingleObject(_exitEvent, ExitSignalCallback, null, Timeout.Infinite, false);
        }

        private void ExitSignalCallback(object state, bool timedOut)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((MethodInvoker)ExitWidget);
            }
            catch
            {
                // form closing
            }
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
                    case NativeMethods.HotkeyExit:
                        ExitWidget();
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
                NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyOpacityDown, mod, 0xBD) &&
                NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyExit, mod, 0x51);
        }

        private void UnregisterHotKeys()
        {
            if (!IsHandleCreated || !_hotkeysRegistered)
                return;

            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyTogglePassthrough);
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyOpacityUp);
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyOpacityDown);
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyExit);
            _hotkeysRegistered = false;
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // This timer only drives the scheduler; provider due times gate network requests.
            EnsureAuthWatchers(refreshOnCreate: true);
            ProcessAuthFileChanges();
            ScheduleRefreshes(force: false);
        }

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            if (_dragging)
                return;

            if (_snapshot?.Codex?.HasData == true ||
                (_cursorEnabled && _snapshot?.Cursor?.HasData == true) ||
                _clickThrough)
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

            var local = PointToClient(cursor);
            string text;
            if (!_clickThrough && _exitButtonRect.Contains(local))
                text = ExitButtonTooltipText;
            else
                text = _clickThrough ? ToggleClickableHotkeyText : TogglePassthroughHotkeyText;
            if (_hotkeyToolTipVisible && _hotkeyToolTipText == text)
                return;

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

        private void ScheduleRefreshes(bool force)
        {
            if (_closing)
                return;

            if (_dragging)
            {
                _pendingRefreshAfterDrag = true;
                _pendingManualRefreshAfterDrag |= force;
                return;
            }

            var now = DateTime.UtcNow;
            ScheduleCodexRefresh(now, force);
            if (_cursorEnabled)
                ScheduleCursorRefresh(now, force);
            if (_showResetExpiryDates)
                ScheduleResetCreditsRefresh(now, force);
        }

        private void ScheduleCodexRefresh(DateTime now, bool force)
        {
            if (_closing)
                return;

            if (_codexRefreshInFlight)
            {
                _codexRefreshPending |= force;
                return;
            }

            if (!force && now < _nextCodexRefreshUtc)
                return;

            _codexRefreshInFlight = true;
            _ = RefreshCodexAsync();
        }

        private void ScheduleCursorRefresh(DateTime now, bool force)
        {
            if (_closing || !_cursorEnabled)
                return;

            if (_cursorRefreshInFlight)
            {
                _cursorRefreshPending |= force;
                return;
            }

            if (!force && now < _nextCursorRefreshUtc)
                return;

            var generation = _cursorGeneration;
            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
            _cursorRequestCancellation = requestCancellation;
            _cursorRefreshInFlight = true;
            _ = RefreshCursorAsync(generation, requestCancellation);
        }

        private void ScheduleResetCreditsRefresh(DateTime now, bool force)
        {
            if (_closing || !_showResetExpiryDates)
                return;

            if (_resetCreditsRefreshInFlight)
            {
                _resetCreditsRefreshPending |= force;
                return;
            }

            if (!force && now < _nextResetCreditsRefreshUtc)
                return;

            var generation = _resetCreditsGeneration;
            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
            _resetCreditsRequestCancellation = requestCancellation;
            _resetCreditsRefreshInFlight = true;
            _ = RefreshResetCreditsAsync(generation, requestCancellation);
        }

        private async Task RefreshCodexAsync()
        {
            try
            {
                var snapshot = await FetchCodexSnapshotAsync(_shutdownCancellation.Token);
                if (_closing)
                    return;

                RecordCodexResult(snapshot);
                ApplyCodexSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
                // application closing
            }
            catch (Exception ex)
            {
                if (!_closing)
                {
                    var snapshot = new UsageSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow };
                    RecordCodexResult(snapshot);
                    ApplyCodexSnapshot(snapshot);
                }
            }
            finally
            {
                _codexRefreshInFlight = false;
                if (!_closing && _codexRefreshPending)
                {
                    _codexRefreshPending = false;
                    ScheduleCodexRefresh(DateTime.UtcNow, force: true);
                }
            }
        }

        private async Task RefreshCursorAsync(int generation, CancellationTokenSource requestCancellation)
        {
            try
            {
                var snapshot = await FetchCursorSnapshotAsync(requestCancellation.Token);
                if (_closing || !_cursorEnabled || generation != _cursorGeneration)
                    return;

                RecordCursorResult(snapshot);
                ApplyCursorSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                // Cursor disabled or application closing
            }
            catch (Exception ex)
            {
                if (!_closing && _cursorEnabled && generation == _cursorGeneration)
                {
                    var snapshot = new CursorUsageSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow };
                    RecordCursorResult(snapshot);
                    ApplyCursorSnapshot(snapshot);
                }
            }
            finally
            {
                if (ReferenceEquals(_cursorRequestCancellation, requestCancellation))
                    _cursorRequestCancellation = null;

                requestCancellation.Dispose();
                _cursorRefreshInFlight = false;
                if (!_closing && _cursorEnabled && _cursorRefreshPending)
                {
                    _cursorRefreshPending = false;
                    ScheduleCursorRefresh(DateTime.UtcNow, force: true);
                }
            }
        }

        private async Task RefreshResetCreditsAsync(int generation, CancellationTokenSource requestCancellation)
        {
            try
            {
                var snapshot = await FetchResetCreditsSnapshotAsync(requestCancellation.Token);
                if (_closing || !_showResetExpiryDates || generation != _resetCreditsGeneration)
                    return;

                RecordResetCreditsResult(snapshot);
                ApplyResetCreditsSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                // Reset expiry details hidden or application closing
            }
            catch (Exception ex)
            {
                if (!_closing && _showResetExpiryDates && generation == _resetCreditsGeneration)
                {
                    var snapshot = new RateLimitResetCreditsSnapshot
                    {
                        Error = ex.Message,
                        FetchedAtUtc = DateTime.UtcNow
                    };
                    RecordResetCreditsResult(snapshot);
                    ApplyResetCreditsSnapshot(snapshot);
                }
            }
            finally
            {
                if (ReferenceEquals(_resetCreditsRequestCancellation, requestCancellation))
                    _resetCreditsRequestCancellation = null;

                requestCancellation.Dispose();
                _resetCreditsRefreshInFlight = false;
                if (!_closing && _showResetExpiryDates && _resetCreditsRefreshPending)
                {
                    _resetCreditsRefreshPending = false;
                    ScheduleResetCreditsRefresh(DateTime.UtcNow, force: true);
                }
            }
        }

        private async Task<UsageSnapshot> FetchCodexSnapshotAsync(CancellationToken ct)
        {
            try
            {
                if (!_authStore.TryLoad(out var token, out var accountId, out var authError))
                    return new UsageSnapshot { Error = authError, FetchedAtUtc = DateTime.UtcNow };

                return await _api.FetchAsync(token, accountId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new UsageSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow };
            }
        }

        private async Task<CursorUsageSnapshot> FetchCursorSnapshotAsync(CancellationToken ct)
        {
            try
            {
                if (!_cursorAuthStore.TryLoad(out var token, out var authError))
                    return new CursorUsageSnapshot { Error = authError, FetchedAtUtc = DateTime.UtcNow };

                return await _cursorApi.FetchAsync(token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CursorUsageSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow };
            }
        }

        private async Task<RateLimitResetCreditsSnapshot> FetchResetCreditsSnapshotAsync(CancellationToken ct)
        {
            try
            {
                if (!_authStore.TryLoad(out var token, out var accountId, out var authError))
                {
                    return new RateLimitResetCreditsSnapshot
                    {
                        Error = authError,
                        FetchedAtUtc = DateTime.UtcNow
                    };
                }

                return await _api.FetchResetCreditsAsync(token, accountId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RateLimitResetCreditsSnapshot { Error = ex.Message, FetchedAtUtc = DateTime.UtcNow };
            }
        }

        private void RecordCodexResult(UsageSnapshot snapshot)
        {
            _nextCodexRefreshUtc = GetNextRefreshUtc(
                DateTime.UtcNow,
                string.IsNullOrWhiteSpace(snapshot?.Error),
                snapshot?.Unauthorized == true,
                NormalRefreshInterval,
                ref _codexFailureCount);
        }

        private void RecordCursorResult(CursorUsageSnapshot snapshot)
        {
            _nextCursorRefreshUtc = GetNextRefreshUtc(
                DateTime.UtcNow,
                string.IsNullOrWhiteSpace(snapshot?.Error),
                snapshot?.Unauthorized == true,
                NormalRefreshInterval,
                ref _cursorFailureCount);
        }

        private void RecordResetCreditsResult(RateLimitResetCreditsSnapshot snapshot)
        {
            _nextResetCreditsRefreshUtc = GetNextRefreshUtc(
                DateTime.UtcNow,
                string.IsNullOrWhiteSpace(snapshot?.Error),
                snapshot?.Unauthorized == true,
                ResetCreditsRefreshInterval,
                ref _resetCreditsFailureCount);
        }

        private static DateTime GetNextRefreshUtc(
            DateTime completedUtc,
            bool success,
            bool unauthorized,
            TimeSpan successInterval,
            ref int failureCount)
        {
            if (success)
            {
                failureCount = 0;
                return completedUtc + successInterval;
            }

            failureCount = Math.Min(failureCount + 1, 3);
            if (unauthorized || failureCount >= 3)
                return completedUtc + MaximumErrorBackoff;
            if (failureCount == 2)
                return completedUtc + SecondErrorBackoff;

            return completedUtc + FirstErrorBackoff;
        }

        private void ApplyCodexSnapshot(UsageSnapshot snapshot)
        {
            CombinedUsageSnapshot combined;
            var previousAvailableCount = 0;
            lock (_dataLock)
            {
                previousAvailableCount = _snapshot?.Codex?.ResetCreditsAvailableCount ?? 0;
                combined = new CombinedUsageSnapshot
                {
                    Codex = snapshot ?? new UsageSnapshot { FetchedAtUtc = DateTime.UtcNow },
                    Cursor = _snapshot?.Cursor ?? new CursorUsageSnapshot(),
                    FetchedAtUtc = DateTime.UtcNow
                };
                _snapshot = combined;
            }

            UpdateResetCreditsMenu();
            ApplySnapshotLayout(combined);
            Invalidate();

            var availableCount = combined.Codex?.ResetCreditsAvailableCount ?? 0;
            var resetCreditsDetailCount = Math.Max(
                _resetCreditsSnapshot?.ExpiresAtUtc?.Count ?? 0,
                _resetCreditsSnapshot?.AvailableCount ?? 0);
            if (_showResetExpiryDates &&
                availableCount != previousAvailableCount &&
                availableCount != resetCreditsDetailCount)
            {
                _resetCreditsFailureCount = 0;
                _nextResetCreditsRefreshUtc = DateTime.MinValue;
                ScheduleResetCreditsRefresh(DateTime.UtcNow, force: true);
            }
        }

        private void ApplyCursorSnapshot(CursorUsageSnapshot snapshot)
        {
            CombinedUsageSnapshot combined;
            lock (_dataLock)
            {
                combined = new CombinedUsageSnapshot
                {
                    Codex = _snapshot?.Codex ?? new UsageSnapshot(),
                    Cursor = snapshot ?? new CursorUsageSnapshot { FetchedAtUtc = DateTime.UtcNow },
                    FetchedAtUtc = DateTime.UtcNow
                };
                _snapshot = combined;
            }

            ApplySnapshotLayout(combined);
            Invalidate();
        }

        private void ApplyResetCreditsSnapshot(RateLimitResetCreditsSnapshot snapshot)
        {
            _resetCreditsSnapshot = snapshot ?? new RateLimitResetCreditsSnapshot
            {
                FetchedAtUtc = DateTime.UtcNow
            };
            UpdateResetCreditsMenu();
            ApplySnapshotLayout(_snapshot);
            Invalidate();
        }

        private void EnsureAuthWatchers(bool refreshOnCreate = false)
        {
            if (_closing)
                return;

            if (_codexAuthWatcher == null)
            {
                _codexAuthWatcher = TryCreateAuthWatcher(_authStore.AuthPath, cursor: false);
                if (refreshOnCreate && _codexAuthWatcher != null && File.Exists(_authStore.AuthPath))
                    Interlocked.Exchange(ref _codexAuthChanged, 1);
            }

            if (_cursorEnabled && _cursorAuthWatcher == null)
            {
                _cursorAuthWatcher = TryCreateAuthWatcher(_cursorAuthStore.AuthPath, cursor: true);
                if (refreshOnCreate && _cursorAuthWatcher != null && File.Exists(_cursorAuthStore.AuthPath))
                    Interlocked.Exchange(ref _cursorAuthChanged, 1);
            }
        }

        private FileSystemWatcher TryCreateAuthWatcher(string authPath, bool cursor)
        {
            try
            {
                var directory = Path.GetDirectoryName(authPath);
                var fileName = Path.GetFileName(authPath);
                if (string.IsNullOrWhiteSpace(directory) ||
                    string.IsNullOrWhiteSpace(fileName) ||
                    !Directory.Exists(directory))
                    return null;

                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.CreationTime |
                                   NotifyFilters.Size
                };

                if (cursor)
                {
                    watcher.Changed += CursorAuthFileChanged;
                    watcher.Created += CursorAuthFileChanged;
                    watcher.Deleted += CursorAuthFileChanged;
                    watcher.Renamed += CursorAuthFileRenamed;
                }
                else
                {
                    watcher.Changed += CodexAuthFileChanged;
                    watcher.Created += CodexAuthFileChanged;
                    watcher.Deleted += CodexAuthFileChanged;
                    watcher.Renamed += CodexAuthFileRenamed;
                }

                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private void CodexAuthFileChanged(object sender, FileSystemEventArgs e) => QueueAuthFileRefresh(cursor: false);

        private void CodexAuthFileRenamed(object sender, RenamedEventArgs e) => QueueAuthFileRefresh(cursor: false);

        private void CursorAuthFileChanged(object sender, FileSystemEventArgs e) => QueueAuthFileRefresh(cursor: true);

        private void CursorAuthFileRenamed(object sender, RenamedEventArgs e) => QueueAuthFileRefresh(cursor: true);

        private void QueueAuthFileRefresh(bool cursor)
        {
            if (_closing)
                return;

            var shouldPost = cursor
                ? Interlocked.Exchange(ref _cursorAuthChanged, 1) == 0
                : Interlocked.Exchange(ref _codexAuthChanged, 1) == 0;
            if (!shouldPost || IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((MethodInvoker)ProcessAuthFileChanges);
            }
            catch
            {
                // form closing; the scheduler timer will consume the flag if needed
            }
        }

        private void ProcessAuthFileChanges()
        {
            if (_closing)
                return;

            var now = DateTime.UtcNow;
            if (Interlocked.Exchange(ref _codexAuthChanged, 0) != 0)
            {
                _codexFailureCount = 0;
                _nextCodexRefreshUtc = DateTime.MinValue;
                if (_showResetExpiryDates)
                {
                    _resetCreditsGeneration++;
                    _resetCreditsFailureCount = 0;
                    _nextResetCreditsRefreshUtc = DateTime.MinValue;
                    _resetCreditsRequestCancellation?.Cancel();
                    _resetCreditsSnapshot = new RateLimitResetCreditsSnapshot();
                    ApplySnapshotLayout(_snapshot);
                    UpdateResetCreditsMenu();
                    Invalidate();
                }

                if (_dragging)
                    _pendingRefreshAfterDrag = true;
                else
                {
                    ScheduleCodexRefresh(now, force: true);
                    if (_showResetExpiryDates)
                        ScheduleResetCreditsRefresh(now, force: true);
                }
            }

            if (Interlocked.Exchange(ref _cursorAuthChanged, 0) != 0 && _cursorEnabled)
            {
                _cursorFailureCount = 0;
                _nextCursorRefreshUtc = DateTime.MinValue;
                if (_dragging)
                    _pendingRefreshAfterDrag = true;
                else
                    ScheduleCursorRefresh(now, force: true);
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

            CombinedUsageSnapshot snap;
            lock (_dataLock)
                snap = _snapshot;

            var titleFont = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            var valueFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            var percentFont = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
            var smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);

            try
            {
                if (_cursorEnabled)
                {
                    using (var divider = new Pen(Color.FromArgb(70, 120, 132, 148), 1f))
                        g.DrawLine(divider, ColumnDividerX, 8, ColumnDividerX, bounds.Height - 28);
                }

                DrawCodexColumn(g, snap?.Codex, titleFont, labelFont, valueFont, percentFont, smallFont, bounds);
                if (_cursorEnabled)
                    DrawCursorColumn(g, snap?.Cursor, titleFont, labelFont, valueFont, percentFont, smallFont, bounds);
                if (_showResetExpiryDates)
                    DrawResetCreditsPanel(g, snap, labelFont, valueFont, bounds);
                DrawExitButton(g, bounds, smallFont);
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

        private void ApplySnapshotLayout(CombinedUsageSnapshot snap, bool preserveNearestEdge = true)
        {
            var oldBounds = Bounds;
            var targetWidth = _cursorEnabled ? DualColumnWidth : SingleColumnWidth;
            var targetHeight = GetBaseClientHeight(snap) + GetResetCreditsExtraHeight();
            if (ClientSize.Width != targetWidth || ClientSize.Height != targetHeight)
            {
                ClientSize = new Size(targetWidth, targetHeight);
                if (preserveNearestEdge && IsHandleCreated)
                    KeepResizedWindowVisible(oldBounds);
            }
        }

        private int GetBaseClientHeight(CombinedUsageSnapshot snap)
        {
            var codexMeters = Math.Max(1, GetUsageWindows(snap?.Codex).Count);
            var windowCount = codexMeters;
            if (_cursorEnabled)
            {
                var cursorMeters = Math.Max(1, GetCursorMeterCount(snap?.Cursor));
                windowCount = Math.Max(codexMeters, cursorMeters);
            }

            return Math.Max(MinClientHeight, LayoutBaseHeight + (windowCount * MeterSpacing));
        }

        private int GetResetCreditsExtraHeight()
        {
            if (!_showResetExpiryDates)
                return 0;

            return ResetCreditsPanelHeaderHeight +
                   (GetResetCreditsRowCount() * ResetCreditRowHeight) +
                   ResetCreditsPanelBottomPadding;
        }

        private int GetResetCreditsRowCount()
        {
            if (!_showResetExpiryDates)
                return 0;

            if (!string.IsNullOrWhiteSpace(_resetCreditsSnapshot?.Error))
                return 1;

            var detailCount = _resetCreditsSnapshot?.ExpiresAtUtc?.Count ?? 0;
            var availableCount = Math.Max(
                detailCount,
                _resetCreditsSnapshot?.AvailableCount ?? 0);
            if ((_resetCreditsSnapshot?.FetchedAtUtc ?? DateTime.MinValue) == DateTime.MinValue)
                return 1;

            return availableCount;
        }

        private void DrawCodexColumn(
            Graphics g,
            UsageSnapshot snap,
            Font titleFont,
            Font labelFont,
            Font valueFont,
            Font percentFont,
            Font smallFont,
            Rectangle bounds)
        {
            var columnWidth = _cursorEnabled ? ColumnDividerX : bounds.Width;
            var column = new Rectangle(0, 0, columnWidth, bounds.Height);
            DrawColumnHeader(g, "Codex", snap?.PlanType, titleFont, smallFont, column, 0);

            if (!string.IsNullOrWhiteSpace(snap?.Error))
            {
                DrawColumnMessage(g, labelFont, snap.Error, column, 62);
                return;
            }

            if (snap == null || !snap.HasData)
            {
                DrawColumnMessage(g, labelFont, "Loading Codex...", column, 62);
                return;
            }

            DrawCodexStatusBadge(g, smallFont, column, snap);
            var windows = GetUsageWindows(snap);
            for (var i = 0; i < windows.Count; i++)
            {
                var item = windows[i];
                DrawHudMeter(
                    g,
                    labelFont,
                    valueFont,
                    percentFont,
                    smallFont,
                    ColumnInnerPadding,
                    MeterTop + (i * MeterSpacing),
                    column.Width - (ColumnInnerPadding * 2),
                    FormatUsageTitle(item.Window),
                    FormatPeriodBadge(item.Window),
                    FormatWindowLabel(item.Window),
                    item.Window);
            }
        }

        private void DrawCursorColumn(
            Graphics g,
            CursorUsageSnapshot snap,
            Font titleFont,
            Font labelFont,
            Font valueFont,
            Font percentFont,
            Font smallFont,
            Rectangle bounds)
        {
            var column = new Rectangle(ColumnDividerX + 1, 0, bounds.Width - ColumnDividerX - 1, bounds.Height);
            DrawColumnHeader(g, "Cursor", snap?.MembershipType, titleFont, smallFont, column, ColumnDividerX + 1);

            if (!string.IsNullOrWhiteSpace(snap?.Error))
            {
                DrawColumnMessage(g, labelFont, snap.Error, column, 62);
                return;
            }

            if (snap == null || !snap.HasData)
            {
                DrawColumnMessage(g, labelFont, "Loading Cursor...", column, 62);
                return;
            }

            DrawCursorStatusBadge(g, smallFont, column, snap);
            var meters = GetCursorMeters(snap);
            for (var i = 0; i < meters.Count; i++)
            {
                var meter = meters[i];
                DrawPercentMeter(
                    g,
                    labelFont,
                    valueFont,
                    percentFont,
                    smallFont,
                    column.X + ColumnInnerPadding,
                    MeterTop + (i * MeterSpacing),
                    column.Width - (ColumnInnerPadding * 2),
                    meter.Title,
                    meter.PeriodLabel,
                    meter.Subtitle,
                    meter.PercentUsed);
            }
        }

        private void DrawResetCreditsPanel(
            Graphics g,
            CombinedUsageSnapshot snap,
            Font labelFont,
            Font valueFont,
            Rectangle bounds)
        {
            var columnWidth = _cursorEnabled ? ColumnDividerX : bounds.Width;
            var panelTop = GetBaseClientHeight(snap) - 20;
            var detailCount = _resetCreditsSnapshot?.ExpiresAtUtc?.Count ?? 0;
            var availableCount = Math.Max(
                detailCount,
                _resetCreditsSnapshot?.AvailableCount ?? 0);
            var detailsLoaded = (_resetCreditsSnapshot?.FetchedAtUtc ?? DateTime.MinValue) != DateTime.MinValue &&
                                string.IsNullOrWhiteSpace(_resetCreditsSnapshot?.Error);
            if (!detailsLoaded)
                availableCount = Math.Max(availableCount, snap?.Codex?.ResetCreditsAvailableCount ?? 0);

            using (var headerBrush = new SolidBrush(Color.FromArgb(225, 214, 224, 238)))
            using (var rule = new Pen(Color.FromArgb(70, 120, 132, 148), 1f))
            {
                DrawTrimmedText(
                    g,
                    "Usage limit resets (" + availableCount + ")",
                    valueFont,
                    headerBrush,
                    new Rectangle(ColumnInnerPadding, panelTop, columnWidth - (ColumnInnerPadding * 2), 18),
                    StringAlignment.Near);
                g.DrawLine(rule, ColumnInnerPadding, panelTop + 20, columnWidth - ColumnInnerPadding, panelTop + 20);
            }

            var rowTop = panelTop + ResetCreditsPanelHeaderHeight;
            if (!string.IsNullOrWhiteSpace(_resetCreditsSnapshot?.Error))
            {
                using (var errorBrush = new SolidBrush(Color.FromArgb(235, 255, 178, 190)))
                    DrawTrimmedText(
                        g,
                        _resetCreditsSnapshot.Error,
                        labelFont,
                        errorBrush,
                        new Rectangle(ColumnInnerPadding, rowTop, columnWidth - (ColumnInnerPadding * 2), ResetCreditRowHeight),
                        StringAlignment.Near);
                return;
            }

            if ((_resetCreditsSnapshot?.FetchedAtUtc ?? DateTime.MinValue) == DateTime.MinValue)
            {
                DrawResetCreditMessage(g, labelFont, "Loading reset expiry dates...", rowTop, columnWidth);
                return;
            }

            if (availableCount <= 0)
                return;

            for (var i = 0; i < availableCount; i++)
            {
                var rowRect = new Rectangle(
                    ColumnInnerPadding,
                    rowTop + (i * ResetCreditRowHeight),
                    columnWidth - (ColumnInnerPadding * 2),
                    ResetCreditRowHeight);

                if ((i & 1) != 0)
                {
                    using (var rowFill = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
                        g.FillRectangle(rowFill, rowRect);
                }

                using (var indexBrush = new SolidBrush(Color.FromArgb(220, 104, 190, 255)))
                    DrawTrimmedText(
                        g,
                        (i + 1).ToString(),
                        valueFont,
                        indexBrush,
                        new Rectangle(rowRect.X, rowRect.Y, 42, rowRect.Height),
                        StringAlignment.Center);

                var expiryText = "Expiry unavailable";
                var expiryColor = Color.FromArgb(180, 184, 194, 206);
                if (i < detailCount)
                {
                    var expiresAtUtc = _resetCreditsSnapshot.ExpiresAtUtc[i];
                    expiryText = FormatThaiExpiryDate(expiresAtUtc);
                    expiryColor = GetExpiryColor(expiresAtUtc);
                }

                using (var expiryBrush = new SolidBrush(expiryColor))
                    DrawTrimmedText(
                        g,
                        expiryText,
                        labelFont,
                        expiryBrush,
                        new Rectangle(rowRect.X + 56, rowRect.Y, rowRect.Width - 64, rowRect.Height),
                        StringAlignment.Near);

                if (i < availableCount - 1)
                {
                    using (var separator = new Pen(Color.FromArgb(45, 160, 172, 188), 1f))
                        g.DrawLine(separator, rowRect.X + 48, rowRect.Bottom - 1, rowRect.Right, rowRect.Bottom - 1);
                }
            }
        }

        private static void DrawResetCreditMessage(Graphics g, Font font, string text, int y, int columnWidth)
        {
            using (var brush = new SolidBrush(Color.FromArgb(190, 184, 194, 206)))
                DrawTrimmedText(
                    g,
                    text,
                    font,
                    brush,
                    new Rectangle(ColumnInnerPadding, y, columnWidth - (ColumnInnerPadding * 2), ResetCreditRowHeight),
                    StringAlignment.Near);
        }

        private static string FormatThaiExpiryDate(DateTime expiresAtUtc)
        {
            if (expiresAtUtc.Kind == DateTimeKind.Unspecified)
                expiresAtUtc = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc);
            else if (expiresAtUtc.Kind != DateTimeKind.Utc)
                expiresAtUtc = expiresAtUtc.ToUniversalTime();

            DateTime bangkok;
            try
            {
                bangkok = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(expiresAtUtc, "SE Asia Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                bangkok = expiresAtUtc.ToLocalTime();
            }
            catch (InvalidTimeZoneException)
            {
                bangkok = expiresAtUtc.ToLocalTime();
            }

            return string.Format(
                "{0} {1} {2} เวลา {3:00}:{4:00}",
                bangkok.Day,
                ThaiMonthAbbreviations[bangkok.Month],
                bangkok.Year,
                bangkok.Hour,
                bangkok.Minute);
        }

        private static Color GetExpiryColor(DateTime expiresAtUtc)
        {
            var remaining = expiresAtUtc.ToUniversalTime() - DateTime.UtcNow;
            if (remaining <= TimeSpan.FromDays(3))
                return Color.FromArgb(255, 244, 92, 92);
            if (remaining <= TimeSpan.FromDays(7))
                return Color.FromArgb(255, 245, 172, 74);

            return Color.FromArgb(235, 238, 242, 250);
        }

        private static int GetCursorMeterCount(CursorUsageSnapshot snap)
        {
            return snap == null || string.IsNullOrWhiteSpace(snap.Error) ? Math.Max(1, GetCursorMeters(snap).Count) : 1;
        }

        private static System.Collections.Generic.List<CursorMeter> GetCursorMeters(CursorUsageSnapshot snap)
        {
            var meters = new System.Collections.Generic.List<CursorMeter>();
            if (snap == null)
                return meters;

            meters.Add(new CursorMeter("Total", "CYCLE", "included usage", snap.TotalPercentUsed));
            meters.Add(new CursorMeter("API", "NAMED", "model usage", snap.ApiPercentUsed));

            if (snap.AutoPercentUsed > 0 && Math.Abs(snap.AutoPercentUsed - snap.TotalPercentUsed) > 0.1)
                meters.Add(new CursorMeter("Auto", "AUTO", "model usage", snap.AutoPercentUsed));

            if (snap.OnDemandEnabled && snap.OnDemandUsedCents.GetValueOrDefault() > 0)
            {
                var usedDollars = snap.OnDemandUsedCents.GetValueOrDefault() / 100.0;
                meters.Add(new CursorMeter("On-demand", "PAY", usedDollars.ToString("0.00") + " USD", -1));
            }

            return meters;
        }

        private sealed class CursorMeter
        {
            public CursorMeter(string title, string periodLabel, string subtitle, double percentUsed)
            {
                Title = title;
                PeriodLabel = periodLabel;
                Subtitle = subtitle;
                PercentUsed = percentUsed;
            }

            public string Title { get; }
            public string PeriodLabel { get; }
            public string Subtitle { get; }
            public double PercentUsed { get; }
        }

        private void DrawColumnHeader(
            Graphics g,
            string title,
            string plan,
            Font titleFont,
            Font smallFont,
            Rectangle column,
            int originX)
        {
            using (var titleBrush = new SolidBrush(Color.White))
            using (var subBrush = new SolidBrush(Color.FromArgb(185, 176, 190, 204)))
            {
                g.DrawString(title, titleFont, titleBrush, originX + ColumnInnerPadding, 10);

                var planText = string.IsNullOrWhiteSpace(plan) ? "-" : plan.ToUpperInvariant();
                var rightPadding = column.Right >= ClientSize.Width
                    ? ExitButtonSize + ExitButtonMargin + 6
                    : 0;
                DrawTrimmedText(
                    g,
                    planText,
                    smallFont,
                    subBrush,
                    new Rectangle(originX + column.Width - 104 - rightPadding, 12, 90, 14),
                    StringAlignment.Far);
            }

            var mode = _clickThrough ? "VIEW" : "EDIT";
            var modeColor = _clickThrough
                ? Color.FromArgb(255, 100, 210, 186)
                : Color.FromArgb(255, 104, 176, 255);
            using (var modeBrush = new SolidBrush(modeColor))
                g.DrawString(mode, smallFont, modeBrush, originX + ColumnInnerPadding, 27);
        }

        private void DrawColumnMessage(Graphics g, Font font, string text, Rectangle column, int y)
        {
            using (var brush = new SolidBrush(Color.FromArgb(235, 255, 178, 190)))
                DrawTrimmedText(
                    g,
                    text,
                    font,
                    brush,
                    new Rectangle(column.X + 16, y, column.Width - 32, 36),
                    StringAlignment.Near);
        }

        private static void DrawCodexStatusBadge(Graphics g, Font smallFont, Rectangle column, UsageSnapshot snap)
        {
            var text = snap.LimitReached ? "LIMIT" : (snap.Allowed ? "READY" : "BLOCKED");
            var color = snap.LimitReached
                ? Color.FromArgb(255, 255, 91, 91)
                : (snap.Allowed ? Color.FromArgb(255, 101, 231, 145) : Color.FromArgb(255, 255, 174, 76));

            DrawStatusBadgeAt(g, smallFont, column.X + column.Width - 84, 25, 70, text, color);
        }

        private static void DrawCursorStatusBadge(Graphics g, Font smallFont, Rectangle column, CursorUsageSnapshot snap)
        {
            string text;
            Color color;
            if (snap.IsUnlimited)
            {
                text = "∞";
                color = Color.FromArgb(255, 104, 176, 255);
            }
            else if (snap.TotalPercentUsed >= 100 || snap.ApiPercentUsed >= 100)
            {
                text = "LIMIT";
                color = Color.FromArgb(255, 255, 91, 91);
            }
            else
            {
                text = "READY";
                color = Color.FromArgb(255, 101, 231, 145);
            }

            DrawStatusBadgeAt(g, smallFont, column.X + column.Width - 84, 25, 70, text, color);
        }

        private static void DrawStatusBadgeAt(Graphics g, Font smallFont, int x, int y, int width, string text, Color color)
        {
            var badge = new Rectangle(x, y, width, 16);
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

        private void DrawExitButton(Graphics g, Rectangle bounds, Font smallFont)
        {
            _exitButtonRect = new Rectangle(
                bounds.Width - ExitButtonMargin - ExitButtonSize,
                ExitButtonMargin,
                ExitButtonSize,
                ExitButtonSize);

            var fillColor = _exitButtonHovered
                ? Color.FromArgb(210, 255, 96, 96)
                : Color.FromArgb(_clickThrough ? 90 : 150, 255, 96, 96);
            var borderColor = Color.FromArgb(_clickThrough ? 110 : 190, 255, 120, 120);

            using (var path = RoundedRect(_exitButtonRect, 6))
            using (var fill = new SolidBrush(fillColor))
            using (var border = new Pen(borderColor, 1f))
            using (var brush = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                DrawTrimmedText(g, "X", smallFont, brush, _exitButtonRect, StringAlignment.Center);
            }
        }

        private static void DrawPercentMeter(
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
            string subtitle,
            double percentUsed)
        {
            var hasPercent = percentUsed >= 0;
            var remaining = hasPercent ? GetRemainingPercent(percentUsed) : 0;
            var accent = GetRemainingColor(remaining, hasPercent);
            var accentEnd = ShiftColor(accent, 28);
            var barRect = new Rectangle(x, y + 29, width, 12);
            var percentText = hasPercent ? remaining.ToString("0.#") + "%" : subtitle;

            using (var labelBrush = new SolidBrush(Color.FromArgb(230, 238, 242, 250)))
            using (var smallBrush = new SolidBrush(Color.FromArgb(166, 178, 190, 204)))
            using (var valueBrush = new SolidBrush(Color.White))
            {
                DrawTrimmedText(g, title, valueFont, labelBrush, new Rectangle(x, y, 84, 18), StringAlignment.Near);
                var pillWidth = Math.Max(34, Math.Min(70, (periodLabel.Length * 7) + 12));
                var pillX = x + 88;
                DrawMiniPill(g, smallFont, periodLabel, pillX, y + 1, pillWidth,
                    Color.FromArgb(hasPercent ? 55 : 34, accent), Color.FromArgb(hasPercent ? 150 : 90, accent));
                g.DrawString(subtitle, smallFont, smallBrush, pillX + pillWidth + 8, y + 2);
                DrawTrimmedText(g, percentText, percentFont, valueBrush, new Rectangle(x + width - 74, y - 2, 74, 24), StringAlignment.Far);
            }

            if (!hasPercent)
                return;

            using (var remainingBrush = new SolidBrush(Color.FromArgb(150, 184, 194, 206)))
                DrawTrimmedText(g, "left", smallFont, remainingBrush, new Rectangle(x + width - 44, y + 22, 44, 12), StringAlignment.Far);

            using (var trackPath = RoundedRect(barRect, 6))
            using (var track = new SolidBrush(Color.FromArgb(170, 38, 43, 50)))
            using (var frame = new Pen(Color.FromArgb(80, 220, 230, 240), 1f))
            {
                g.FillPath(track, trackPath);

                var fillWidth = (int)Math.Round(barRect.Width * (remaining / 100.0));
                if (fillWidth > 0)
                {
                    var fillRect = new Rectangle(barRect.X, barRect.Y, Math.Max(3, fillWidth), barRect.Height);
                    using (var fillPath = RoundedRect(fillRect, 6))
                    using (var fill = new LinearGradientBrush(fillRect, accent, accentEnd, LinearGradientMode.Horizontal))
                        g.FillPath(fill, fillPath);
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
            var remaining = hasData ? GetRemainingPercent(window.UsedPercent) : 0;
            var accent = GetRemainingColor(remaining, hasData);
            var accentEnd = ShiftColor(accent, 28);
            var barRect = new Rectangle(x, y + 29, width, 12);
            var percentText = hasData ? remaining.ToString("0.#") + "%" : "--";
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

            using (var remainingBrush = new SolidBrush(Color.FromArgb(150, 184, 194, 206)))
                DrawTrimmedText(g, "left", smallFont, remainingBrush, new Rectangle(x + width - 44, y + 22, 44, 12), StringAlignment.Far);

            using (var trackPath = RoundedRect(barRect, 6))
            using (var track = new SolidBrush(Color.FromArgb(170, 38, 43, 50)))
            using (var frame = new Pen(Color.FromArgb(80, 220, 230, 240), 1f))
            {
                g.FillPath(track, trackPath);

                var fillWidth = (int)Math.Round(barRect.Width * (remaining / 100.0));
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

        private void DrawFooter(Graphics g, CombinedUsageSnapshot snap, Font smallFont)
        {
            var fetchedAt = snap?.FetchedAtUtc ?? DateTime.UtcNow;
            var local = fetchedAt.ToLocalTime();
            var text = string.Empty;

            var codex = snap?.Codex;
            if (codex?.HasData == true)
            {
                var windows = GetUsageWindows(codex);
                for (var i = 0; i < windows.Count; i++)
                {
                    var window = windows[i].Window;
                    var remaining = GetRemainingResetSeconds(window, codex.FetchedAtUtc, DateTime.UtcNow);
                    text += (text.Length == 0 ? string.Empty : " | ") +
                            "Codex " + FormatWindowLabel(window) + " " + FormatCountdown(remaining);
                }
            }

            var cursor = snap?.Cursor;
            if (_cursorEnabled && cursor?.BillingCycleEndUtc != null)
            {
                var resetSeconds = (long)Math.Max(0, (cursor.BillingCycleEndUtc.Value - DateTime.UtcNow).TotalSeconds);
                text += (text.Length == 0 ? string.Empty : " | ") +
                        "Cursor cycle " + FormatCountdown(resetSeconds);
            }

            text += (text.Length == 0 ? string.Empty : " | ") +
                    string.Format("Opacity {0}% | Ctrl+Alt+T settings | Ctrl+Alt+Q exit | {1}",
                        (int)Math.Round(Opacity * 100),
                        local.ToString("HH:mm:ss"));

            using (var brush = new SolidBrush(Color.FromArgb(170, 184, 194, 214)))
                DrawTrimmedText(g, text, smallFont, brush, new Rectangle(14, ClientSize.Height - 22, ClientSize.Width - 28, 14), StringAlignment.Near);
        }

        private static double ClampPercent(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static double GetRemainingPercent(double usedPercent)
        {
            return 100 - ClampPercent(usedPercent);
        }

        private static Color GetRemainingColor(double remainingPercent, bool hasData)
        {
            if (!hasData)
                return Color.FromArgb(255, 96, 105, 118);
            if (remainingPercent <= 10)
                return Color.FromArgb(255, 244, 92, 92);
            if (remainingPercent <= 30)
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

        private static long GetRemainingResetSeconds(UsageWindow window, DateTime fetchedAtUtc, DateTime nowUtc)
        {
            if (window == null)
                return 0;

            DateTime resetUtc;
            if (window.ResetAt > 0 && window.ResetAt <= 253402300799L)
            {
                try
                {
                    resetUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(window.ResetAt);
                    return (long)Math.Max(0, Math.Ceiling((resetUtc - nowUtc).TotalSeconds));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // fall back to the relative reset value below
                }
            }

            if (window.ResetAfterSeconds <= 0)
                return 0;

            if (fetchedAtUtc == DateTime.MinValue)
                fetchedAtUtc = nowUtc;
            else if (fetchedAtUtc.Kind != DateTimeKind.Utc)
                fetchedAtUtc = fetchedAtUtc.ToUniversalTime();

            try
            {
                resetUtc = fetchedAtUtc.AddSeconds(window.ResetAfterSeconds);
                return (long)Math.Max(0, Math.Ceiling((resetUtc - nowUtc).TotalSeconds));
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
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
                if (_exitButtonRect.Contains(e.Location))
                {
                    ExitWidget();
                    return;
                }

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
            if (!_clickThrough)
            {
                var hovered = _exitButtonRect.Contains(e.Location);
                if (hovered != _exitButtonHovered)
                {
                    _exitButtonHovered = hovered;
                    Invalidate();
                }
            }

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

            var local = PointToClient(Cursor.Position);
            if (!_cursorEnabled || local.X < ColumnDividerX)
                OpenAnalyticsPage();
            else
                OpenCursorDashboard();
        }

        private void MiRefresh_Click(object sender, EventArgs e) => ScheduleRefreshes(force: true);

        private void MiClickThrough_Click(object sender, EventArgs e) => ToggleClickThrough();

        private void MiAutoStart_Click(object sender, EventArgs e) => ToggleAutoStart();

        private void MiEnableCursor_Click(object sender, EventArgs e) =>
            SetCursorEnabled(!_cursorEnabled, persist: true, refresh: true);

        private void MiShowResetExpiries_Click(object sender, EventArgs e) =>
            SetShowResetExpiryDates(!_showResetExpiryDates, persist: true, refresh: true);

        private void MiOpenWeb_Click(object sender, EventArgs e) => OpenAnalyticsPage();

        private void MiOpenCursorWeb_Click(object sender, EventArgs e) => OpenCursorDashboard();

        private void MiExit_Click(object sender, EventArgs e) => ExitWidget();

        private void ExitWidget()
        {
            if (IsDisposed)
                return;

            SaveWindowSettings();
            Close();
        }

        private void OpacityTrack_Scroll(object sender, EventArgs e)
        {
            SetOpacity(_opacityTrack.Value / 100.0, persist: true);
        }

        private void ContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var compact = _clickThrough;
            _miClickThrough.Visible = !compact;
            _miAutoStart.Visible = !compact;
            _miEnableCursor.Visible = !compact;
            _miShowResetExpiries.Visible = !compact;
            _miOpacityLabel.Visible = !compact;
            _miOpacityHost.Visible = !compact;
            _miOpenWeb.Visible = !compact;
            _miOpenCursorWeb.Visible = !compact && _cursorEnabled;

            if (!compact)
            {
                SyncOpacityTrack();
                UpdateCursorMenu();
                UpdateResetCreditsMenu();
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

        private void SetCursorEnabled(bool enabled, bool persist, bool refresh)
        {
            var changed = _cursorEnabled != enabled;
            _cursorEnabled = enabled;
            if (changed)
            {
                _cursorGeneration++;
                _cursorFailureCount = 0;
                _cursorRefreshPending = false;
                _nextCursorRefreshUtc = enabled ? DateTime.MinValue : DateTime.MaxValue;

                if (!enabled)
                {
                    _cursorRequestCancellation?.Cancel();
                    _cursorAuthWatcher?.Dispose();
                    _cursorAuthWatcher = null;
                    Interlocked.Exchange(ref _cursorAuthChanged, 0);
                }

                CombinedUsageSnapshot combined;
                lock (_dataLock)
                {
                    combined = new CombinedUsageSnapshot
                    {
                        Codex = _snapshot?.Codex ?? new UsageSnapshot(),
                        Cursor = new CursorUsageSnapshot(),
                        FetchedAtUtc = _snapshot?.FetchedAtUtc ?? DateTime.UtcNow
                    };
                    _snapshot = combined;
                }

                ApplySnapshotLayout(combined, preserveNearestEdge: true);
                Invalidate();
            }

            UpdateCursorMenu();

            if (persist)
                SaveWindowSettings();

            if (enabled && refresh)
            {
                EnsureAuthWatchers();
                ScheduleCursorRefresh(DateTime.UtcNow, force: true);
            }
        }

        private void UpdateCursorMenu()
        {
            _miEnableCursor.Checked = _cursorEnabled;
            _miEnableCursor.Text = "Enable Cursor";
        }

        private void SetShowResetExpiryDates(bool enabled, bool persist, bool refresh)
        {
            var changed = _showResetExpiryDates != enabled;
            _showResetExpiryDates = enabled;
            if (changed)
            {
                _resetCreditsGeneration++;
                _resetCreditsFailureCount = 0;
                _resetCreditsRefreshPending = false;
                _nextResetCreditsRefreshUtc = enabled ? DateTime.MinValue : DateTime.MaxValue;
                _resetCreditsRequestCancellation?.Cancel();
                _resetCreditsSnapshot = new RateLimitResetCreditsSnapshot();

                ApplySnapshotLayout(_snapshot, preserveNearestEdge: true);
                Invalidate();
            }

            UpdateResetCreditsMenu();

            if (persist)
                SaveWindowSettings();

            if (enabled && refresh)
                ScheduleResetCreditsRefresh(DateTime.UtcNow, force: true);
        }

        private void UpdateResetCreditsMenu()
        {
            var detailCount = _resetCreditsSnapshot?.ExpiresAtUtc?.Count ?? 0;
            var detailsLoaded = (_resetCreditsSnapshot?.FetchedAtUtc ?? DateTime.MinValue) != DateTime.MinValue &&
                                string.IsNullOrWhiteSpace(_resetCreditsSnapshot?.Error);
            var availableCount = detailsLoaded
                ? Math.Max(detailCount, _resetCreditsSnapshot?.AvailableCount ?? 0)
                : _snapshot?.Codex?.ResetCreditsAvailableCount ?? 0;

            _miShowResetExpiries.Checked = _showResetExpiryDates;
            _miShowResetExpiries.Text = availableCount > 0
                ? "Show reset expiry dates (" + availableCount + ")"
                : "Show reset expiry dates";
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
            _miOpacityLabel.Text = "Opacity: " + trackValue + "% (drag slider)";
        }

        private static void OpenCursorDashboard()
        {
            try
            {
                Process.Start(new ProcessStartInfo(CursorDashboardUrl) { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
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
            var opacity = 0.88;
            var clickThrough = true;
            var cursorEnabled = false;
            var showResetExpiryDates = false;
            object xValue = null;
            object yValue = null;

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key != null)
                    {
                        opacity = Convert.ToDouble(key.GetValue("Opacity") ?? 0.88);
                        clickThrough = Convert.ToInt32(key.GetValue("ClickThrough") ?? 1) != 0;
                        cursorEnabled = Convert.ToInt32(key.GetValue("CursorEnabled") ?? 0) != 0;
                        showResetExpiryDates = Convert.ToInt32(key.GetValue("ShowResetExpiryDates") ?? 0) != 0;
                        xValue = key.GetValue("X");
                        yValue = key.GetValue("Y");
                    }
                }
            }
            catch
            {
                // keep defaults
            }

            if (double.IsNaN(opacity) || double.IsInfinity(opacity))
                opacity = 0.88;

            SetOpacity(opacity, persist: false);
            SetClickThrough(clickThrough, persist: false);
            SetCursorEnabled(cursorEnabled, persist: false, refresh: false);
            SetShowResetExpiryDates(showResetExpiryDates, persist: false, refresh: false);

            try
            {
                if (xValue != null && yValue != null)
                {
                    Location = new Point(Convert.ToInt32(xValue), Convert.ToInt32(yValue));
                    _loadedSavedLocation = true;
                }
            }
            catch
            {
                // keep the default location
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

        private void KeepResizedWindowVisible(Rectangle oldBounds)
        {
            var area = Screen.FromRectangle(oldBounds).WorkingArea;
            var distanceToLeft = Math.Abs(oldBounds.Left - area.Left);
            var distanceToRight = Math.Abs(area.Right - oldBounds.Right);
            var x = distanceToRight < distanceToLeft
                ? oldBounds.Right - Width
                : oldBounds.Left;
            var distanceToTop = Math.Abs(oldBounds.Top - area.Top);
            var distanceToBottom = Math.Abs(area.Bottom - oldBounds.Bottom);
            var y = distanceToBottom < distanceToTop
                ? oldBounds.Bottom - Height
                : oldBounds.Top;

            var maxX = Math.Max(area.Left, area.Right - Width);
            var maxY = Math.Max(area.Top, area.Bottom - Height);
            x = Math.Max(area.Left, Math.Min(maxX, x));
            y = Math.Max(area.Top, Math.Min(maxY, y));
            Location = new Point(x, y);
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
            _pendingManualRefreshAfterDrag = false;
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
                var force = _pendingManualRefreshAfterDrag;
                _pendingRefreshAfterDrag = false;
                _pendingManualRefreshAfterDrag = false;
                ScheduleRefreshes(force);
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
                    key.SetValue("CursorEnabled", _cursorEnabled ? 1 : 0);
                    key.SetValue("ShowResetExpiryDates", _showResetExpiryDates ? 1 : 0);
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
            _closing = true;
            _refreshTimer.Stop();
            _clockTimer.Stop();
            _hoverTimer.Stop();
            _codexRefreshPending = false;
            _cursorRefreshPending = false;
            _resetCreditsRefreshPending = false;
            _cursorGeneration++;
            _resetCreditsGeneration++;
            _cursorRequestCancellation?.Cancel();
            _resetCreditsRequestCancellation?.Cancel();
            _shutdownCancellation.Cancel();
            _codexAuthWatcher?.Dispose();
            _codexAuthWatcher = null;
            _cursorAuthWatcher?.Dispose();
            _cursorAuthWatcher = null;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            HideHotkeyToolTip();
            UnregisterHotKeys();
            _exitWaitHandle?.Unregister(null);
            _exitWaitHandle = null;
            _exitEvent?.Dispose();
            _exitEvent = null;
            SaveWindowSettings();
            base.OnFormClosing(e);
        }
    }
}
