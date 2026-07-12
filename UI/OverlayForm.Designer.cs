namespace CodexUsageMonitor.UI
{
    partial class OverlayForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
                _clockTimer?.Stop();
                _clockTimer?.Dispose();
                _hoverTimer?.Stop();
                _hoverTimer?.Dispose();
                _hotkeyToolTip?.Dispose();
                _opacityTrack?.Dispose();
                _api?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this._refreshTimer = new System.Windows.Forms.Timer(this.components);
            this._clockTimer = new System.Windows.Forms.Timer(this.components);
            this._contextMenu = new System.Windows.Forms.ContextMenuStrip(this.components);
            this._miRefresh = new System.Windows.Forms.ToolStripMenuItem();
            this._miClickThrough = new System.Windows.Forms.ToolStripMenuItem();
            this._miOpacityHost = new System.Windows.Forms.ToolStripControlHost(this._opacityTrack = new System.Windows.Forms.TrackBar());
            this._miOpenWeb = new System.Windows.Forms.ToolStripMenuItem();
            this._miExit = new System.Windows.Forms.ToolStripMenuItem();
            ((System.ComponentModel.ISupportInitialize)(this._opacityTrack)).BeginInit();
            this._contextMenu.SuspendLayout();
            this.SuspendLayout();
            //
            // _refreshTimer
            //
            this._refreshTimer.Interval = 5000;
            this._refreshTimer.Tick += new System.EventHandler(this.RefreshTimer_Tick);
            //
            // _clockTimer
            //
            this._clockTimer.Interval = 1000;
            this._clockTimer.Tick += new System.EventHandler(this.ClockTimer_Tick);
            //
            // _opacityTrack
            //
            this._opacityTrack.AutoSize = false;
            this._opacityTrack.LargeChange = 10;
            this._opacityTrack.Maximum = 100;
            this._opacityTrack.Minimum = 35;
            this._opacityTrack.Size = new System.Drawing.Size(170, 30);
            this._opacityTrack.SmallChange = 5;
            this._opacityTrack.TickFrequency = 5;
            this._opacityTrack.Value = 88;
            this._opacityTrack.Scroll += new System.EventHandler(this.OpacityTrack_Scroll);
            //
            // _contextMenu
            //
            this._contextMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this._miRefresh,
                this._miClickThrough,
                this._miOpacityHost,
                this._miOpenWeb,
                this._miExit});
            this._contextMenu.Name = "_contextMenu";
            this._contextMenu.Size = new System.Drawing.Size(220, 138);
            this._contextMenu.Opening += new System.ComponentModel.CancelEventHandler(this.ContextMenu_Opening);
            //
            // _miRefresh
            //
            this._miRefresh.Name = "_miRefresh";
            this._miRefresh.Size = new System.Drawing.Size(219, 22);
            this._miRefresh.Text = "Refresh now";
            this._miRefresh.Click += new System.EventHandler(this.MiRefresh_Click);
            //
            // _miClickThrough
            //
            this._miClickThrough.Name = "_miClickThrough";
            this._miClickThrough.Size = new System.Drawing.Size(219, 22);
            this._miClickThrough.Text = "Click-through (Ctrl+Alt+T)";
            this._miClickThrough.Click += new System.EventHandler(this.MiClickThrough_Click);
            //
            // _miOpacityHost
            //
            this._miOpacityHost.Name = "_miOpacityHost";
            this._miOpacityHost.Size = new System.Drawing.Size(180, 30);
            this._miOpacityHost.Text = "Opacity";
            //
            // _miOpenWeb
            //
            this._miOpenWeb.Name = "_miOpenWeb";
            this._miOpenWeb.Size = new System.Drawing.Size(219, 22);
            this._miOpenWeb.Text = "Open analytics page";
            this._miOpenWeb.Click += new System.EventHandler(this.MiOpenWeb_Click);
            //
            // _miExit
            //
            this._miExit.Name = "_miExit";
            this._miExit.Size = new System.Drawing.Size(219, 22);
            this._miExit.Text = "Exit";
            this._miExit.Click += new System.EventHandler(this.MiExit_Click);
            //
            // OverlayForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.BackColor = System.Drawing.Color.Magenta;
            this.ClientSize = new System.Drawing.Size(340, 150);
            this.ContextMenuStrip = this._contextMenu;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.KeyPreview = true;
            this.Name = "OverlayForm";
            this.Opacity = 0.88D;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            this.Text = "Codex Usage";
            this.TopMost = true;
            this.TransparencyKey = System.Drawing.Color.Magenta;
            this.Load += new System.EventHandler(this.OverlayForm_Load);
            this.Paint += new System.Windows.Forms.PaintEventHandler(this.OverlayForm_Paint);
            this.MouseDown += new System.Windows.Forms.MouseEventHandler(this.OverlayForm_MouseDown);
            this.MouseMove += new System.Windows.Forms.MouseEventHandler(this.OverlayForm_MouseMove);
            this.MouseUp += new System.Windows.Forms.MouseEventHandler(this.OverlayForm_MouseUp);
            this.MouseWheel += new System.Windows.Forms.MouseEventHandler(this.OverlayForm_MouseWheel);
            this.DoubleClick += new System.EventHandler(this.OverlayForm_DoubleClick);
            ((System.ComponentModel.ISupportInitialize)(this._opacityTrack)).EndInit();
            this._contextMenu.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.Timer _refreshTimer;
        private System.Windows.Forms.Timer _clockTimer;
        private System.Windows.Forms.ContextMenuStrip _contextMenu;
        private System.Windows.Forms.ToolStripMenuItem _miRefresh;
        private System.Windows.Forms.ToolStripMenuItem _miClickThrough;
        private System.Windows.Forms.ToolStripControlHost _miOpacityHost;
        private System.Windows.Forms.TrackBar _opacityTrack;
        private System.Windows.Forms.ToolStripMenuItem _miOpenWeb;
        private System.Windows.Forms.ToolStripMenuItem _miExit;
    }
}
