using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FormsTimer = System.Windows.Forms.Timer;

namespace LinkTV
{
    internal sealed class CastWindow : Form
    {
        private const int Port = 17863;
        private const int CardPadding = 17;
        private const int SidebarWidth = 320;
        private const int StatusWidth = 208;
        private static readonly Color Background = Color.FromArgb(255, 248, 243);
        private static readonly Color Blush = Color.FromArgb(252, 224, 236);
        private static readonly Color Lavender = Color.FromArgb(237, 226, 250);
        private static readonly Color Ink = Color.FromArgb(67, 44, 77);
        private static readonly Color Muted = Color.FromArgb(111, 81, 119);
        private static readonly Color Accent = Color.FromArgb(162, 59, 112);
        private static readonly Color PreviewBackground = Color.FromArgb(48, 34, 57);
        private static readonly Color DisabledFill = Color.FromArgb(240, 229, 240);
        private static readonly Color AudioFill = Color.FromArgb(252, 239, 247);
        private static readonly Color PairingFill = Color.FromArgb(252, 240, 249);

        private readonly ListBox deviceList = new ListBox();
        private readonly Label emptyDevices = MakeWrappedLabel("正在查找附近设备…", 10, FontStyle.Regular, Muted, Background);
        private readonly Button connectButton = MakeButton("连接设备", false);
        private readonly Button confirmButton = MakeButton("确认", true);
        private readonly Button cancelButton = MakeButton("取消", false);
        private readonly Label pairingTitle = MakeWrappedLabel("", 11, FontStyle.Bold, Ink, PairingFill);
        private readonly Label pairingNumber = MakeLabel("", 24, FontStyle.Bold, Accent);
        private readonly Label pairingHelp = MakeWrappedLabel("", 9, FontStyle.Regular, Muted, PairingFill);
        private readonly Button sendButton = MakeButton("投屏到设备", true);
        private readonly CheckBox syncAudioOption = MakeAudioOption("同步本机声音");
        private readonly CheckBox muteComputerOption = MakeAudioOption("本机静音");
        private readonly Label syncAudioLabel = MakeLabel("同步本机声音", 9, FontStyle.Regular, Ink);
        private readonly Label muteComputerLabel = MakeLabel("本机静音", 9, FontStyle.Regular, Ink);
        private readonly Label helpText = MakeWrappedLabel("", 9, FontStyle.Regular, Muted, Color.White);
        private readonly Label connectionStatus = MakeWrappedLabel("正在检查网络", 10, FontStyle.Regular, Muted, Lavender);
        private readonly Button fullscreenButton = MakeButton("全屏观看", false);
        private readonly Button receiverMuteButton = MakeButton("静音本机投屏声音", false);
        private readonly ToolTip statusTooltip = new ToolTip();
        private readonly PictureBox picture = new PictureBox();
        private readonly SoftSurface previewIdle = new SoftSurface(Blush, Lavender, 20);
        private readonly TableLayoutPanel previewMessage = new TableLayoutPanel();
        private readonly Label previewTitle = MakeLabel("", 18, FontStyle.Bold, Ink);
        private readonly Label previewDetail = MakeLabel("", 10, FontStyle.Regular, Muted);
        private readonly FormsTimer invitationTimer = new FormsTimer();
        private readonly bool audioSupported = Environment.OSVersion.Version.Build >= 20348;

        private readonly object frameLock = new object();
        private readonly PcmPlayback remoteAudio = new PcmPlayback();
        private TableLayoutPanel discoveryPanel;
        private TableLayoutPanel pairingPanel;
        private TableLayoutPanel sidebarLayout;
        private TableLayoutPanel shellLayout;
        private SoftSurface statusBadge;
        private StatusViewport deviceStatusSurface;
        private StatusViewport helpStatusSurface;
        private Label discoveryTip;
        private Panel audioOptions;
        private Panel previewCanvas;
        private Panel receiverAudioBar;
        private TableLayoutPanel previewLayout;
        private Form fullscreenWindow;
        private int headerMinimum;
        private int sendHeight;
        private int audioOptionsHeight;
        private int receiverAudioHeight;
        private CastHub hub;
        private NearbyTvDiscovery discovery;
        private BonjourRegistration bonjour;
        private string invitedName;
        private string pairingPeer;
        private string connectedName;
        private bool localConfirmed;
        private bool localResponded;
        private Image mascot;
        private Icon appIcon;
        private CancellationTokenSource captureCancellation;
        private WindowsAudio.SpeakerMute speakerMute;
        private string ffmpeg;
        private string outgoingVideoMode;
        private H264Preview h264Preview;
        private bool audioReady;
        private string audioWarning;
        private string muteWarning;
        private bool startingListener;
        private Bitmap pendingFrame;
        private bool previewRenderQueued;
        private int receiveVersion;
        private int displayedVersion = -1;
        private bool remoteRequested;
        private bool localCastingReserved;
        private volatile bool remoteCasting;
        private volatile bool closing;
        private string operationMessage;

        public CastWindow()
        {
            Text = "双向投屏";
            using (Stream iconStream = typeof(CastWindow).Assembly.GetManifestResourceStream("LinkTV.AppIcon.ico"))
            {
                if (iconStream != null)
                {
                    appIcon = new Icon(iconStream, SystemInformation.IconSize);
                    Icon = appIcon;
                }
            }
            Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1024, 620);
            MinimumSize = new Size(640, 580);
            BackColor = Background;
            ForeColor = Ink;
            SetStyle(ControlStyles.ResizeRedraw, true);

            SuspendLayout();
            TableLayoutPanel shell = NewTable(3, 2);
            shellLayout = shell;
            shell.Padding = new Padding(26, 16, 26, 26);
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarWidth));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int headerHeight;
            TableLayoutPanel header = BuildHeader(out headerHeight);
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, headerHeight));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            shell.Controls.Add(header, 0, 0);
            shell.SetColumnSpan(header, 3);
            shell.Controls.Add(BuildPairingCard(SidebarWidth - 2 * CardPadding), 0, 1);
            shell.Controls.Add(BuildPreview(), 2, 1);
            shell.SizeChanged += (sender, args) => ArrangeColumns();
            Controls.Add(shell);
            ResumeLayout();

            connectButton.Click += ToggleConnection;
            deviceList.SelectedIndexChanged += (sender, e) => UpdatePresentation();
            confirmButton.Click += (sender, e) => ConfirmPairing(true);
            cancelButton.Click += (sender, e) => ConfirmPairing(false);
            sendButton.Click += ToggleSending;
            fullscreenButton.Click += (sender, e) => ToggleFullscreen();
            receiverMuteButton.Click += (sender, e) => ToggleReceivingMute();
            picture.DoubleClick += (sender, e) => ToggleFullscreen();
            syncAudioOption.Checked = audioSupported;
            syncAudioOption.CheckedChanged += (sender, e) => UpdatePresentation();
            muteComputerOption.CheckedChanged += (sender, e) =>
            {
                UpdateSpeakerMute();
                UpdatePresentation();
            };
            invitationTimer.Interval = 12000;
            invitationTimer.Tick += InvitationExpired;
            UpdatePresentation();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartListening();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintGradient(e.Graphics, ClientRectangle, Background, Color.FromArgb(248, 234, 248));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancelCapture();
            closing = true;
            CloseFullscreen();
            invitationTimer.Dispose();
            remoteAudio.Dispose();
            if (discovery != null)
            {
                discovery.Dispose();
            }
            if (bonjour != null)
            {
                bonjour.Dispose();
            }
            if (hub != null)
            {
                hub.Dispose();
            }

            lock (frameLock)
            {
                StopH264Preview();
                DiscardPendingFrame();
            }

            DisplayFrame(null);
            if (mascot != null)
            {
                mascot.Dispose();
            }
            base.OnFormClosed(e);
        }

        private void ArrangeColumns()
        {
            int available = shellLayout.ClientSize.Width - shellLayout.Padding.Horizontal - (int)shellLayout.ColumnStyles[1].Width;
            int width = Math.Max(240, Math.Min(SidebarWidth, (int)(available * 0.44)));
            if ((int)shellLayout.ColumnStyles[0].Width != width)
            {
                shellLayout.ColumnStyles[0].Width = width;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                statusTooltip.Dispose();
            }
            if (disposing && appIcon != null)
            {
                appIcon.Dispose();
                appIcon = null;
            }
        }

        private TableLayoutPanel BuildHeader(out int height)
        {
            TableLayoutPanel header = NewTable(2, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StatusWidth));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = MakeLabel("双向投屏", 26, FontStyle.Bold, Ink);
            title.TextAlign = ContentAlignment.MiddleLeft;
            connectionStatus.TextAlign = ContentAlignment.MiddleCenter;
            connectionStatus.Padding = new Padding(12, 7, 12, 7);
            statusBadge = new SoftSurface(Lavender, Lavender, 16);
            headerMinimum = Math.Max(68, PreferredHeight(title, 0) + 12);
            height = headerMinimum;
            statusBadge.Controls.Add(connectionStatus);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(statusBadge, 1, 0);
            return header;
        }

        private void ArrangeHeader()
        {
            int statusHeight = PreferredHeight(connectionStatus, StatusWidth);
            int height = Math.Max(headerMinimum, statusHeight + 10);
            int top = (height - statusHeight) / 2;
            shellLayout.RowStyles[0].Height = height;
            statusBadge.Margin = new Padding(0, top, 0, height - statusHeight - top);
        }

        private Panel BuildPairingCard(int width)
        {
            Panel surface = WhiteCard();
            TableLayoutPanel form = NewTable(1, 8);
            sidebarLayout = form;
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label title = MakeLabel("附近设备", 15, FontStyle.Bold, Ink);
            discoveryTip = MakeLabel("在对方设备打开「双向投屏」。", 9, FontStyle.Regular, Muted);
            sendHeight = Math.Max(42, PreferredHeight(sendButton, width) + 6);
            audioOptions = BuildAudioOptions();
            AddFixedRows(form, PreferredHeight(title, width) + 4, PreferredHeight(discoveryTip, width) + 8);
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(form, 8, sendHeight, 8, audioOptionsHeight, 0);

            AddWide(form, title, 0);
            AddWide(form, discoveryTip, 1);
            Panel modePanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.Transparent };
            discoveryPanel = BuildDiscoveryPanel();
            pairingPanel = BuildConfirmationPanel(width);
            modePanel.Controls.Add(discoveryPanel);
            modePanel.Controls.Add(pairingPanel);
            pairingPanel.BringToFront();
            pairingPanel.Hide();
            AddWide(form, modePanel, 2);
            AddWide(form, sendButton, 4);
            AddWide(form, audioOptions, 6);
            helpText.Padding = new Padding(0, 9, 0, 0);
            helpStatusSurface = new StatusViewport(helpText, Color.White);
            AddWide(form, helpStatusSurface, 7);
            form.SizeChanged += (sender, args) =>
                ArrangeSidebar(pairingPeer != null || invitedName != null, hub != null && hub.IsConnected);

            surface.Controls.Add(form);
            return surface;
        }

        private Panel BuildAudioOptions()
        {
            SoftSurface surface = new SoftSurface(AudioFill, AudioFill, 14);
            surface.Padding = new Padding(12, 7, 12, 7);
            TableLayoutPanel options = NewTable(2, 3);
            options.BackColor = AudioFill;
            int checkWidth = Math.Max(syncAudioOption.GetPreferredSize(Size.Empty).Width,
                muteComputerOption.GetPreferredSize(Size.Empty).Width) + 8;
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, checkWidth));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int rowHeight = 4 + Math.Max(26, Math.Max(PreferredHeight(syncAudioLabel, 0), PreferredHeight(muteComputerLabel, 0)));
            AddFixedRows(options, rowHeight, 3, rowHeight);
            audioOptionsHeight = surface.Padding.Vertical + rowHeight * 2 + 3;
            AddAudioOption(options, syncAudioOption, syncAudioLabel, 0);
            AddAudioOption(options, muteComputerOption, muteComputerLabel, 2);
            surface.Controls.Add(options);
            return surface;
        }

        private static void AddAudioOption(TableLayoutPanel options, CheckBox check, Label caption, int row)
        {
            caption.BackColor = AudioFill;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.UseMnemonic = false;
            caption.Click += (sender, args) =>
            {
                if (check.Enabled)
                {
                    check.Focus();
                    check.Checked = !check.Checked;
                }
            };
            options.Controls.Add(check, 0, row);
            options.Controls.Add(caption, 1, row);
        }

        private TableLayoutPanel BuildDiscoveryPanel()
        {
            TableLayoutPanel table = NewTable(1, 3);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(table, 9, Math.Max(39, PreferredHeight(connectButton, 0) + 6));

            SoftSurface listArea = new SoftSurface(Background, Background, 17);
            deviceList.Dock = DockStyle.Fill;
            deviceList.Margin = Padding.Empty;
            deviceList.BorderStyle = BorderStyle.None;
            deviceList.IntegralHeight = false;
            deviceList.BackColor = Background;
            deviceList.ForeColor = Ink;
            deviceList.DrawMode = DrawMode.OwnerDrawFixed;
            deviceList.ItemHeight = Math.Max(44, TextRenderer.MeasureText("设备", deviceList.Font).Height + 12);
            deviceList.DrawItem += DrawDevice;
            deviceList.AccessibleName = "附近设备";
            emptyDevices.TextAlign = ContentAlignment.MiddleCenter;
            emptyDevices.Padding = new Padding(12, 6, 12, 6);
            deviceStatusSurface = new StatusViewport(emptyDevices, Background);
            listArea.Controls.Add(deviceList);
            listArea.Controls.Add(deviceStatusSurface);

            table.Controls.Add(listArea, 0, 0);
            table.Controls.Add(connectButton, 0, 2);
            return table;
        }

        private void DrawDevice(object sender, DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush rowColor = new SolidBrush(selected ? Blush : Background))
            {
                e.Graphics.FillRectangle(rowColor, e.Bounds);
            }

            if (e.Index < 0)
            {
                return;
            }

            int badgeSize = Math.Min(e.Bounds.Height - 10, 30);
            Rectangle badge = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + (e.Bounds.Height - badgeSize) / 2, badgeSize, badgeSize);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush badgeBrush = new SolidBrush(selected ? Color.White : Lavender))
            using (Pen devicePen = new Pen(Accent, 1.5f))
            {
                e.Graphics.FillEllipse(badgeBrush, badge);
                int centerX = badge.Left + badge.Width / 2;
                int centerY = badge.Top + badge.Height / 2;
                e.Graphics.DrawRectangle(devicePen, centerX - 7, centerY - 6, 14, 10);
                e.Graphics.DrawLine(devicePen, centerX, centerY + 4, centerX, centerY + 7);
                e.Graphics.DrawLine(devicePen, centerX - 4, centerY + 7, centerX + 4, centerY + 7);
            }

            Rectangle text = new Rectangle(badge.Right + 10, e.Bounds.Top, e.Bounds.Right - badge.Right - 16, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, deviceList.Items[e.Index].ToString(), deviceList.Font, text, Ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        private TableLayoutPanel BuildConfirmationPanel(int width)
        {
            TableLayoutPanel table = NewTable(3, 6);
            table.BackColor = PairingFill;
            table.Padding = new Padding(11, 9, 11, 9);
            table.SizeChanged += (sender, args) => SetRoundedRegion(table, 17);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pairingTitle.Text = "连接设备";
            pairingNumber.BackColor = PairingFill;
            pairingNumber.AccessibleName = "核对号码";
            pairingNumber.TextAlign = ContentAlignment.MiddleCenter;
            pairingHelp.Text = "两边号码相同，请在两台设备上各点一次确认。";
            pairingHelp.TextAlign = ContentAlignment.MiddleCenter;
            AddFixedRows(table, PreferredHeight(pairingTitle, 0) + 4,
                TextRenderer.MeasureText("000000", pairingNumber.Font).Height + 5,
                PreferredHeight(pairingHelp, width - table.Padding.Horizontal) + 4, 8);
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(table, Math.Max(38, Math.Max(PreferredHeight(cancelButton, 0), PreferredHeight(confirmButton, 0)) + 6));
            AddWide(table, pairingTitle, 0);
            AddWide(table, pairingNumber, 1);
            AddWide(table, pairingHelp, 2);
            table.Controls.Add(cancelButton, 0, 5);
            table.Controls.Add(confirmButton, 2, 5);
            table.SizeChanged += ArrangePairingText;
            pairingTitle.TextChanged += ArrangePairingText;
            pairingHelp.TextChanged += ArrangePairingText;
            return table;
        }

        private void ArrangePairingText(object sender, EventArgs e)
        {
            if (pairingPanel == null || pairingPanel.Width <= pairingPanel.Padding.Horizontal)
            {
                return;
            }
            int width = pairingPanel.Width - pairingPanel.Padding.Horizontal;
            int height = pairingPanel.Height - pairingPanel.Padding.Vertical;
            int hintHeight = PreferredHeight(pairingHelp, width) + 4;
            int remaining = height - hintHeight - (int)(pairingPanel.RowStyles[1].Height
                + pairingPanel.RowStyles[3].Height + pairingPanel.RowStyles[5].Height);
            pairingPanel.SuspendLayout();
            pairingPanel.RowStyles[0].Height = Math.Max(0, Math.Min(remaining, PreferredHeight(pairingTitle, width) + 4));
            pairingPanel.RowStyles[2].Height = hintHeight;
            pairingPanel.ResumeLayout();
            statusTooltip.SetToolTip(pairingTitle, pairingTitle.Text);
        }

        private Panel BuildPreview()
        {
            Panel surface = WhiteCard();
            TableLayoutPanel card = NewTable(1, 3);
            previewLayout = card;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label title = MakeLabel("设备画面", 15, FontStyle.Bold, Ink);
            TableLayoutPanel heading = NewTable(2, 1);
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,
                Math.Max(104, fullscreenButton.GetPreferredSize(Size.Empty).Width + 12)));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            fullscreenButton.Margin = new Padding(0, 0, 0, 7);
            fullscreenButton.Visible = false;
            heading.Controls.Add(title, 0, 0);
            heading.Controls.Add(fullscreenButton, 1, 0);
            AddFixedRows(card, Math.Max(PreferredHeight(title, 0) + 8, PreferredHeight(fullscreenButton, 0) + 8));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(card, 0);
            AddWide(card, heading, 0);

            SoftSurface canvas = new SoftSurface(PreviewBackground, PreviewBackground, 20);
            previewCanvas = canvas;
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.BackColor = PreviewBackground;
            picture.AccessibleName = "设备实时画面";
            canvas.Controls.Add(picture);

            previewMessage.Dock = DockStyle.Fill;
            previewMessage.Margin = Padding.Empty;
            previewMessage.Padding = new Padding(16, 0, 16, 0);
            previewMessage.BackColor = Color.Transparent;
            previewMessage.ColumnCount = 1;
            previewMessage.RowCount = 5;
            previewMessage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            previewMessage.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mascot = LoadMascot();
            AddFixedRows(previewMessage, mascot == null ? 0 : 150, 42, 50);
            previewMessage.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            if (mascot != null)
            {
                PictureBox mascotView = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty,
                    BackColor = Color.Transparent,
                    Image = mascot,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    TabStop = false
                };
                previewMessage.Controls.Add(mascotView, 0, 1);
            }
            previewTitle.TextAlign = ContentAlignment.MiddleCenter;
            previewDetail.TextAlign = ContentAlignment.TopCenter;
            previewMessage.Controls.Add(previewTitle, 0, 2);
            previewMessage.Controls.Add(previewDetail, 0, 3);
            previewMessage.SizeChanged += ArrangePreview;
            previewTitle.TextChanged += ArrangePreview;
            previewDetail.TextChanged += ArrangePreview;
            previewIdle.Controls.Add(previewMessage);
            canvas.Controls.Add(previewIdle);
            previewIdle.BringToFront();

            card.Controls.Add(canvas, 0, 1);
            receiverAudioBar = new Panel
            {
                BackColor = Color.White,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(0, 8, 0, 0),
                Visible = false
            };
            receiverMuteButton.AccessibleDescription = "只影响这台电脑接收的投屏声音。";
            receiverAudioHeight = Math.Max(41, PreferredHeight(receiverMuteButton, 0) + 6) + receiverAudioBar.Padding.Vertical;
            receiverAudioBar.Controls.Add(receiverMuteButton);
            card.Controls.Add(receiverAudioBar, 0, 2);
            surface.Controls.Add(card);
            return surface;
        }

        private void ToggleReceivingMute()
        {
            if (remoteCasting && !closing)
            {
                remoteAudio.SetMuted(!remoteAudio.IsMuted);
                RefreshReceiverAudio();
            }
        }

        private void RefreshReceiverAudio()
        {
            bool receiving = remoteCasting && !closing;
            bool inWindow = receiving && fullscreenWindow == null;
            receiverMuteButton.Text = remoteAudio.IsMuted ? "恢复本机投屏声音" : "静音本机投屏声音";
            receiverMuteButton.Visible = receiving;
            receiverAudioBar.Visible = inWindow;
            previewLayout.RowStyles[2].Height = inWindow ? receiverAudioHeight : 0;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.F11 && (remoteCasting || fullscreenWindow != null))
            {
                ToggleFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref message, keys);
        }

        private void ToggleFullscreen()
        {
            if (fullscreenWindow != null)
            {
                CloseFullscreen();
            }
            else if (remoteCasting && picture.Image != null && !closing)
            {
                CreateFullscreenWindow(Screen.FromControl(this).Bounds).Show(this);
            }
        }

        private Form CreateFullscreenWindow(Rectangle bounds)
        {
            Form window = new Form
            {
                Text = "全屏观看",
                Bounds = bounds,
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                KeyPreview = true,
                BackColor = PreviewBackground,
                Icon = Icon
            };
            fullscreenWindow = window;
            Button exit = MakeButton("退出全屏", false);
            exit.Dock = DockStyle.None;
            exit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exit.Size = new Size(Math.Max(132, exit.GetPreferredSize(Size.Empty).Width + 16),
                Math.Max(42, PreferredHeight(exit, 0) + 8));
            exit.Location = new Point(window.ClientSize.Width - exit.Width - 18, 18);
            exit.Click += (sender, args) => window.Close();
            window.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.F11 || args.KeyCode == Keys.Escape)
                {
                    args.SuppressKeyPress = true;
                    window.Close();
                }
            };
            window.FormClosing += (sender, args) => RestorePreview(window);
            window.Controls.Add(picture);
            window.Controls.Add(exit);
            receiverMuteButton.Dock = DockStyle.None;
            receiverMuteButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            receiverMuteButton.Size = new Size(Math.Max(180, receiverMuteButton.GetPreferredSize(Size.Empty).Width + 18), exit.Height);
            window.Controls.Add(receiverMuteButton);
            receiverMuteButton.Location = new Point(exit.Left - receiverMuteButton.Width - 8, exit.Top);
            exit.BringToFront();
            receiverMuteButton.BringToFront();
            RefreshReceiverAudio();
            return window;
        }

        private void CloseFullscreen()
        {
            Form window = fullscreenWindow;
            if (window != null)
            {
                RestorePreview(window);
                window.Close();
            }
        }

        private void RestorePreview(Form window)
        {
            if (fullscreenWindow == window)
            {
                fullscreenWindow = null;
                if (!previewCanvas.IsDisposed)
                {
                    previewCanvas.Controls.Add(picture);
                    previewIdle.BringToFront();
                    receiverAudioBar.Controls.Add(receiverMuteButton);
                    receiverMuteButton.Dock = DockStyle.Fill;
                    RefreshReceiverAudio();
                }
            }
        }

        private void ArrangePreview(object sender, EventArgs e)
        {
            int width = Math.Max(1, previewMessage.ClientSize.Width - previewMessage.Padding.Horizontal);
            int titleHeight = PreferredHeight(previewTitle, width) + 6;
            int detailHeight = PreferredHeight(previewDetail, width) + 8;
            int imageHeight = mascot == null
                ? 0
                : Math.Min(150, Math.Max(0, previewMessage.ClientSize.Height - titleHeight - detailHeight - 20));
            previewMessage.SuspendLayout();
            previewMessage.RowStyles[1].Height = imageHeight;
            previewMessage.RowStyles[2].Height = titleHeight;
            previewMessage.RowStyles[3].Height = detailHeight;
            previewMessage.ResumeLayout();
        }

        private static Image LoadMascot()
        {
            using (Stream stream = typeof(CastWindow).Assembly.GetManifestResourceStream("LinkTV.Mascot.png"))
            {
                if (stream == null)
                {
                    return null;
                }

                using (Image image = Image.FromStream(stream))
                {
                    return new Bitmap(image);
                }
            }
        }

        private async void StartListening()
        {
            if (startingListener || hub != null || closing)
            {
                return;
            }
            startingListener = true;
            operationMessage = null;
            UpdatePresentation();
            CastHub listener = null;
            NearbyTvDiscovery nearby = null;
            try
            {
                FfmpegVideo.Installation video = await Task.Run(() => FfmpegVideo.FindInstallation());
                if (closing)
                {
                    return;
                }

                IPAddress mask;
                uint interfaceIndex;
                IPAddress address = FindLocalAddress(out mask, out interfaceIndex);
                if (address == null)
                {
                    operationMessage = "请先把电脑连到与设备相同的网络，再点「重新检查网络」。";
                    return;
                }

                string localName = "电脑 " + Environment.MachineName;
                ffmpeg = video == null ? null : video.Path;
                byte capabilities = 0;
                if (video != null && video.HasNvenc)
                {
                    capabilities |= CastHub.H264Send;
                }
                if (video != null && video.CanDecode)
                {
                    capabilities |= CastHub.H264Receive;
                }
                listener = new CastHub(address, Port, localName, capabilities);
                nearby = new NearbyTvDiscovery(address, mask, localName);
                listener.ConnectionChanged += OnConnectionChanged;
                listener.PairingRequested += OnPairingRequested;
                listener.PairingFinished += OnPairingFinished;
                listener.RemoteCastingChanged += OnRemoteCastingChanged;
                listener.RemoteStopRequested += OnRemoteStopRequested;
                listener.JpegReceived += OnJpegReceived;
                listener.H264Started += OnH264Started;
                listener.H264FrameReceived += OnH264Frame;
                listener.AudioReceived += remoteAudio.Enqueue;
                nearby.DevicesChanged += devices => PostToUi(() => UpdateNearbyDevices(devices));
                listener.Start();
                nearby.Start();
                hub = listener;
                discovery = nearby;
                bonjour = BonjourRegistration.TryStart(address, interfaceIndex, localName, Port);
                operationMessage = null;
            }
            catch (SocketException error)
            {
                operationMessage = error.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "已有程序占用了投屏端口。关闭另一个投屏窗口后重试。"
                    : "无法开启投屏。请检查电脑的网络连接后重试。";
            }
            catch (NetworkInformationException)
            {
                operationMessage = "无法读取电脑网络地址。请检查网络连接后重试。";
            }
            finally
            {
                startingListener = false;
                if (hub != listener && listener != null)
                {
                    listener.Dispose();
                }
                if (discovery != nearby && nearby != null)
                {
                    nearby.Dispose();
                }
                if (!closing)
                {
                    UpdatePresentation();
                }
            }
        }

        private static IPAddress FindLocalAddress(out IPAddress mask, out uint interfaceIndex)
        {
            IPAddress bestAddress = null;
            int bestPriority = -1;
            mask = null;
            interfaceIndex = 0;

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool wireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                bool ethernet = adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
                if (adapter.OperationalStatus != OperationalStatus.Up || (!wireless && !ethernet))
                {
                    continue;
                }

                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
                int priority = (wireless ? 2 : 0) + (hasGateway ? 1 : 0);

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    IPAddress address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address)
                        || unicast.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred)
                    {
                        continue;
                    }

                    if (priority > bestPriority)
                    {
                        bestAddress = address;
                        mask = unicast.IPv4Mask;
                        interfaceIndex = (uint)properties.GetIPv4Properties().Index;
                        bestPriority = priority;
                    }
                }
            }

            return bestAddress;
        }

        private void UpdateNearbyDevices(NearbyTv[] devices)
        {
            NearbyTv selection = deviceList.SelectedItem as NearbyTv;
            deviceList.BeginUpdate();
            deviceList.Items.Clear();
            deviceList.Items.AddRange(devices);
            int selectedIndex = selection == null
                ? -1
                : Array.FindIndex(devices, device => device.Address.Equals(selection.Address));
            deviceList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : devices.Length == 1 ? 0 : -1;
            deviceList.EndUpdate();
            UpdatePresentation();
        }

        private void ToggleConnection(object sender, EventArgs e)
        {
            if (hub == null)
            {
                StartListening();
                return;
            }

            if (hub.IsConnected)
            {
                CancelCapture();
                hub.Disconnect();
                return;
            }

            NearbyTv device = deviceList.SelectedItem as NearbyTv;
            if (device == null || pairingPeer != null || invitedName != null)
            {
                return;
            }

            operationMessage = null;
            if (discovery.Invite(device))
            {
                invitedName = device.Name;
                invitationTimer.Start();
            }
            else
            {
                operationMessage = "邀请发送失败。确认设备和电脑在同一网络后重试。";
            }

            UpdatePresentation();
        }

        private void InvitationExpired(object sender, EventArgs e)
        {
            invitationTimer.Stop();
            invitedName = null;
            operationMessage = "设备没有响应。保持对方的投屏应用打开，然后重试。";
            UpdatePresentation();
        }

        private void ConfirmPairing(bool accepted)
        {
            if (pairingPeer == null)
            {
                invitationTimer.Stop();
                invitedName = null;
                UpdatePresentation();
                return;
            }

            if (!accepted && localConfirmed)
            {
                localConfirmed = false;
                hub.Disconnect();
                UpdatePresentation();
                return;
            }

            if (hub.RespondToPairing(accepted))
            {
                localConfirmed = accepted;
                localResponded = true;
            }

            UpdatePresentation();
        }

        private void OnPairingRequested(string peerName, string matchNumber)
        {
            PostToUi(() =>
            {
                invitationTimer.Stop();
                invitedName = null;
                pairingPeer = peerName;
                pairingNumber.Text = matchNumber;
                localConfirmed = false;
                localResponded = false;
                operationMessage = null;
                UpdatePresentation();
            });
        }

        private void OnPairingFinished(bool paired)
        {
            PostToUi(() =>
            {
                connectedName = paired ? pairingPeer : null;
                invitedName = null;
                pairingPeer = null;
                if (!paired)
                {
                    operationMessage = "配对已取消或超时。可以重新选择设备。";
                }
                UpdatePresentation();
            });
        }

        private void ToggleSending(object sender, EventArgs e)
        {
            if (hub == null)
            {
                return;
            }

            if (captureCancellation != null)
            {
                CancelCapture();
                UpdatePresentation();
                return;
            }

            if (!hub.IsConnected)
            {
                return;
            }

            lock (frameLock)
            {
                if (remoteCasting)
                {
                    return;
                }

                // A simultaneous remote START yields to the Windows sender.
                localCastingReserved = true;
            }

            CastHub activeHub = hub;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            captureCancellation = cancellation;
            audioWarning = null;
            muteWarning = null;
            outgoingVideoMode = null;
            operationMessage = null;
            bool includeAudio = audioSupported && syncAudioOption.Checked;
            UpdatePresentation();
            Task.Run(() => SendDesktop(activeHub, cancellation, includeAudio));
        }

        private void SendDesktop(CastHub activeHub, CancellationTokenSource cancellation, bool includeAudio)
        {
            bool started = false;
            string errorMessage = null;
            CancellationTokenSource audioStop = null;
            Task audioWorker = null;
            object modeSwitch = new object();
            try
            {
                if (!cancellation.IsCancellationRequested && !remoteCasting)
                {
                    started = activeHub.SendStart();
                    if (!started)
                    {
                        throw new IOException("Could not start sending desktop frames.");
                    }

                    if (includeAudio)
                    {
                        audioStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        CancellationToken audioToken = audioStop.Token;
                        audioWorker = Task.Factory.StartNew(
                            () => SendComputerAudio(activeHub, cancellation, audioToken, modeSwitch),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }

                    if (ffmpeg != null && activeHub.CanSendH264)
                    {
                        bool beganH264 = false;
                        FfmpegVideo.CaptureDesktop(ffmpeg, cancellation.Token, (width, height, fps) =>
                        {
                            if (!activeHub.SendH264Begin(width, height, fps))
                            {
                                throw new IOException("Could not start H.264 on the paired connection.");
                            }
                            beganH264 = true;
                            SetOutgoingVideoMode(cancellation, "H.264 · " + width + " × " + height + " · 最高 60 FPS");
                        }, (ptsUs, keyFrame, accessUnit) =>
                        {
                            if (!activeHub.SendH264Frame(ptsUs, keyFrame, accessUnit))
                            {
                                throw new IOException("The remote endpoint stopped accepting H.264 frames.");
                            }
                        });
                        if (cancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        if (beganH264)
                        {
                            lock (modeSwitch)
                            {
                                if (!activeHub.SendStop() || !activeHub.SendStart())
                                {
                                    throw new IOException("Could not switch to JPEG on the paired connection.");
                                }
                            }
                        }
                    }

                    SetOutgoingVideoMode(cancellation, "JPEG 兼容模式 · 最高 20 FPS");
                    DesktopCapture.Run(cancellation.Token, () => !remoteCasting, jpeg =>
                    {
                        if (!remoteCasting && !activeHub.SendJpeg(jpeg))
                        {
                            throw new IOException("The remote endpoint stopped accepting frames.");
                        }
                    });
                }
            }
            catch (IOException)
            {
                errorMessage = "投屏已中断。请检查设备是否仍然连接。";
            }
            catch (Exception error)
            {
                errorMessage = "无法读取电脑画面。请在解锁的桌面上重新投屏。";
                System.Diagnostics.Trace.TraceWarning(error.ToString());
            }
            finally
            {
                if (audioStop != null)
                {
                    audioStop.Cancel();
                    SetAudioState(cancellation, audioStop.Token, false, null);
                }

                if (started)
                {
                    activeHub.SendStop();
                }

                if (audioWorker != null)
                {
                    audioWorker.GetAwaiter().GetResult();
                }
                if (audioStop != null)
                {
                    audioStop.Dispose();
                }

                if (!PostToUi(() => FinishCapture(cancellation, errorMessage)))
                {
                    cancellation.Dispose();
                }
            }
        }

        private void SetOutgoingVideoMode(CancellationTokenSource session, string detail)
        {
            PostToUi(() =>
            {
                if (captureCancellation == session && !session.IsCancellationRequested)
                {
                    outgoingVideoMode = detail;
                    UpdatePresentation();
                }
            });
        }

        private void SendComputerAudio(CastHub activeHub, CancellationTokenSource session, CancellationToken stop, object modeSwitch)
        {
            const int MaxAudioPacketBytes = 16384;
            bool firstPacketSent = false;
            string warning = null;
            try
            {
                WindowsAudio.Capture(stop, pcm =>
                {
                    if (pcm.Length % WindowsAudio.BytesPerFrame != 0)
                    {
                        throw new InvalidDataException("Unaligned PCM buffer.");
                    }

                    for (int offset = 0; offset < pcm.Length && !stop.IsCancellationRequested; offset += MaxAudioPacketBytes)
                    {
                        int size = Math.Min(MaxAudioPacketBytes, pcm.Length - offset);
                        byte[] packet = pcm;
                        if (size != pcm.Length)
                        {
                            packet = new byte[size];
                            Buffer.BlockCopy(pcm, offset, packet, 0, size);
                        }

                        bool sent;
                        lock (modeSwitch)
                        {
                            sent = !stop.IsCancellationRequested && activeHub.SendAudio(packet);
                        }
                        if (!sent)
                        {
                            if (stop.IsCancellationRequested)
                            {
                                return;
                            }
                            throw new IOException("Could not send computer audio.");
                        }

                        if (!firstPacketSent)
                        {
                            firstPacketSent = true;
                            SetAudioState(session, stop, true, null);
                        }
                    }
                });
            }
            catch (Exception error)
            {
                if (!stop.IsCancellationRequested)
                {
                    warning = "电脑声音采集失败，画面仍在投屏。";
                    System.Diagnostics.Trace.TraceWarning(error.ToString());
                }
            }
            finally
            {
                SetAudioState(session, stop, false, warning);
            }
        }

        private void SetAudioState(CancellationTokenSource session, CancellationToken stop, bool ready, string warning)
        {
            PostToUi(() =>
            {
                if (captureCancellation != session)
                {
                    return;
                }

                audioReady = ready && !stop.IsCancellationRequested;
                audioWarning = warning;
                UpdateSpeakerMute();
                UpdatePresentation();
            });
        }

        private void UpdateSpeakerMute()
        {
            if (!audioReady || !muteComputerOption.Checked)
            {
                muteWarning = null;
                RestoreSpeaker();
                return;
            }

            if (speakerMute == null)
            {
                WindowsAudio.SpeakerMute mute = new WindowsAudio.SpeakerMute();
                try
                {
                    mute.Start();
                    speakerMute = mute;
                    muteWarning = null;
                }
                catch (Exception error)
                {
                    mute.Dispose();
                    muteWarning = "无法将本机静音，对方设备仍在接收声音。";
                    System.Diagnostics.Trace.TraceWarning(error.ToString());
                }
            }
        }

        private void RestoreSpeaker()
        {
            if (speakerMute != null)
            {
                try
                {
                    speakerMute.Dispose();
                }
                catch (Exception error)
                {
                    muteWarning = "无法恢复本机音量。请检查 Windows 音量设置。";
                    System.Diagnostics.Trace.TraceWarning(error.ToString());
                }
                finally
                {
                    speakerMute = null;
                }
            }
        }

        private void FinishCapture(CancellationTokenSource cancellation, string errorMessage)
        {
            bool wasCancelled = cancellation.IsCancellationRequested;
            captureCancellation = null;
            cancellation.Dispose();
            audioReady = false;
            audioWarning = null;
            RestoreSpeaker();
            lock (frameLock)
            {
                localCastingReserved = false;
                UpdateRemoteCasting();
            }

            operationMessage = wasCancelled ? null : errorMessage;
            UpdatePresentation();
        }

        private void CancelCapture()
        {
            if (captureCancellation != null)
            {
                captureCancellation.Cancel();
            }
            audioReady = false;
            RestoreSpeaker();
        }

        private void OnRemoteStopRequested()
        {
            PostToUi(() =>
            {
                if (captureCancellation != null)
                {
                    CancelCapture();
                    UpdatePresentation();
                }
            });
        }

        private void OnConnectionChanged(bool isConnected)
        {
            if (!isConnected)
            {
                SetRemoteCasting(false);
            }

            PostToUi(() =>
            {
                if (!isConnected)
                {
                    CancelCapture();
                    remoteAudio.SetMuted(false);
                    connectedName = null;
                    operationMessage = null;
                }

                UpdatePresentation();
            });
        }

        private void OnRemoteCastingChanged(bool isCasting)
        {
            SetRemoteCasting(isCasting);
            PostToUi(() =>
            {
                if (remoteCasting)
                {
                    operationMessage = null;
                }

                UpdatePresentation();
            });
        }

        private void SetRemoteCasting(bool isCasting)
        {
            lock (frameLock)
            {
                remoteRequested = isCasting;
                UpdateRemoteCasting();
            }
        }

        private void UpdateRemoteCasting()
        {
            bool shouldReceive = remoteRequested && !localCastingReserved;
            if (remoteCasting != shouldReceive)
            {
                remoteCasting = shouldReceive;
                receiveVersion++;
                if (!shouldReceive)
                {
                    StopH264Preview();
                }
                DiscardPendingFrame();
                remoteAudio.SetReceiving(shouldReceive);
                RequestPreviewRender();
            }
        }

        private void OnH264Started(int width, int height, int fps)
        {
            lock (frameLock)
            {
                if (!remoteCasting || closing || ffmpeg == null)
                {
                    return;
                }

                int version = receiveVersion;
                StopH264Preview();
                h264Preview = new H264Preview(ffmpeg, width, height, frame => PresentFrame(version, frame));
            }
        }

        private void OnH264Frame(long ptsUs, bool keyFrame, byte[] accessUnit)
        {
            H264Preview decoder;
            lock (frameLock)
            {
                decoder = h264Preview;
            }
            if (decoder != null)
            {
                decoder.Enqueue(keyFrame, accessUnit);
            }
        }

        private void StopH264Preview()
        {
            if (h264Preview != null)
            {
                h264Preview.Dispose();
                h264Preview = null;
            }
        }

        private void OnJpegReceived(byte[] jpeg)
        {
            int version;
            lock (frameLock)
            {
                if (!remoteCasting || closing)
                {
                    return;
                }

                version = receiveVersion;
            }

            Bitmap nextFrame;
            try
            {
                using (MemoryStream stream = new MemoryStream(jpeg, false))
                using (Image decoded = Image.FromStream(stream, false, true))
                {
                    const int MaxPixels = 8294400;
                    if (decoded.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid || (long)decoded.Width * decoded.Height > MaxPixels)
                    {
                        return;
                    }

                    nextFrame = new Bitmap(decoded);
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return;
            }
            catch (OutOfMemoryException)
            {
                return;
            }

            PresentFrame(version, nextFrame);
        }

        private void PresentFrame(int version, Bitmap nextFrame)
        {
            lock (frameLock)
            {
                if (version != receiveVersion || closing)
                {
                    nextFrame.Dispose();
                    return;
                }

                DiscardPendingFrame();
                pendingFrame = nextFrame;
                RequestPreviewRender();
            }
        }

        // The network/decoder only replaces the latest frame and posts one paint at a time.
        private void RequestPreviewRender()
        {
            if (!previewRenderQueued)
            {
                previewRenderQueued = PostToUi(() => RenderLatestFrame(this, EventArgs.Empty));
            }
        }

        private void RenderLatestFrame(object sender, EventArgs e)
        {
            Bitmap frame;
            int version;
            lock (frameLock)
            {
                previewRenderQueued = false;
                frame = pendingFrame;
                pendingFrame = null;
                version = receiveVersion;
            }

            if (frame != null || displayedVersion != version)
            {
                displayedVersion = version;
                DisplayFrame(frame);
                previewIdle.Visible = frame == null || !remoteCasting;
                fullscreenButton.Visible = frame != null && remoteCasting;
                if (!remoteCasting)
                {
                    CloseFullscreen();
                }
            }
        }

        private void DiscardPendingFrame()
        {
            if (pendingFrame != null)
            {
                pendingFrame.Dispose();
                pendingFrame = null;
            }
        }

        private void DisplayFrame(Bitmap frame)
        {
            Image previous = picture.Image;
            picture.Image = frame;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void UpdatePresentation()
        {
            bool connected = hub != null && hub.IsConnected;
            bool sending = captureCancellation != null;
            bool stopping = sending && captureCancellation.IsCancellationRequested;
            bool receiving = connected && remoteCasting;
            bool comparing = pairingPeer != null;
            bool inviting = invitedName != null;
            bool pairing = comparing || inviting;

            discoveryPanel.Visible = !pairing;
            pairingPanel.Visible = pairing;
            if (pairing)
            {
                pairingTitle.Text = comparing ? pairingPeer : invitedName;
                if (!comparing)
                {
                    pairingNumber.Text = "…";
                    pairingHelp.Text = "正在等待对方响应。";
                }
                else if (!localResponded)
                {
                    pairingHelp.Text = "两边号码相同，请在两台设备上各点一次确认。";
                }
                else
                {
                    pairingHelp.Text = localConfirmed ? "已确认，正在等待对方。" : "正在取消…";
                }
                confirmButton.Enabled = comparing && !localResponded;
                cancelButton.Enabled = !comparing || !localResponded || localConfirmed;
            }

            string screenState;
            if (receiving)
            {
                screenState = "正在接收画面";
                sendButton.Text = screenState;
                sendButton.Enabled = false;
            }
            else if (sending)
            {
                sendButton.Text = stopping ? "正在停止…" : "停止投屏";
                screenState = stopping ? "正在停止投屏" : "电脑正在投屏";
                sendButton.Enabled = !stopping;
            }
            else
            {
                screenState = connected ? "设备已连接" : pairing ? "正在配对" : "查找附近设备";
                sendButton.Text = "投屏到设备";
                sendButton.Enabled = connected;
            }

            connectionStatus.Text = startingListener ? "正在准备投屏" : hub == null ? "网络未就绪" : screenState;
            connectionStatus.ForeColor = connected || pairing ? Accent : Ink;
            ArrangeHeader();
            connectButton.Text = hub == null ? "重新检查网络" : connected ? "断开连接" : "连接设备";
            connectButton.Enabled = !startingListener && (hub == null || connected || deviceList.SelectedItem != null);
            emptyDevices.Text = connected
                ? connectedName ?? "已连接"
                : startingListener ? "正在检查视频支持…" : hub == null ? "还未连接到局域网" : "正在查找附近设备…";
            bool showDeviceStatus = connected || hub == null || deviceList.Items.Count == 0;
            deviceList.Visible = !showDeviceStatus;
            deviceStatusSurface.Visible = showDeviceStatus;
            if (showDeviceStatus)
            {
                statusTooltip.SetToolTip(emptyDevices, emptyDevices.Text);
                deviceStatusSurface.FitContent();
            }
            deviceList.Enabled = !connected;
            syncAudioOption.Enabled = audioSupported && !sending && !receiving;
            muteComputerOption.Enabled = audioSupported && syncAudioOption.Checked && !receiving && !stopping;
            syncAudioLabel.ForeColor = syncAudioOption.Enabled ? Ink : Muted;
            syncAudioLabel.Cursor = syncAudioOption.Enabled ? Cursors.Hand : Cursors.Default;
            muteComputerLabel.ForeColor = muteComputerOption.Enabled ? Ink : Muted;
            muteComputerLabel.Cursor = muteComputerOption.Enabled ? Cursors.Hand : Cursors.Default;
            string audioHint = null;
            if (connected && !audioSupported)
            {
                audioHint = "当前 Windows 版本支持画面投屏。";
            }
            else if (sending && !stopping && muteComputerOption.Checked && !audioReady)
            {
                audioHint = "同步声音后将静音本机。";
            }
            helpText.Text = operationMessage ?? audioWarning ?? muteWarning ?? audioHint ?? "";
            statusTooltip.SetToolTip(helpText, helpText.Text);
            ArrangeSidebar(pairing, connected);

            if (receiving)
            {
                previewTitle.Text = "正在接收设备画面";
                previewDetail.Text = "第一帧到达后会在这里显示。";
            }
            else if (sending && !stopping)
            {
                previewTitle.Text = "电脑正在投屏";
                previewDetail.Text = outgoingVideoMode ?? "对方设备上会显示本机主屏幕。";
            }
            else
            {
                previewTitle.Text = connected ? "等待设备投屏" : "连接后即可投屏";
                previewDetail.Text = connected ? "选「投屏到设备」，也可以接收对方共享的画面。" : "选择设备，两边确认连接。";
            }

            RefreshReceiverAudio();
            previewIdle.Visible = !receiving || picture.Image == null;
        }

        private void ArrangeSidebar(bool pairing, bool connected)
        {
            sidebarLayout.SuspendLayout();
            bool showHint = !pairing && !connected;
            discoveryTip.Visible = showHint;
            int width = Math.Max(1, sidebarLayout.ClientSize.Width);
            sidebarLayout.RowStyles[1].Height = showHint ? PreferredHeight(discoveryTip, width) + 8 : 0;
            sendButton.Visible = !pairing;
            audioOptions.Visible = !pairing;
            bool showHelp = !pairing && helpText.Text.Length != 0;
            helpStatusSurface.Visible = showHelp;
            sidebarLayout.RowStyles[3].Height = pairing ? 0 : 8;
            sidebarLayout.RowStyles[4].Height = pairing ? 0 : sendHeight;
            sidebarLayout.RowStyles[5].Height = pairing ? 0 : 8;
            sidebarLayout.RowStyles[6].Height = pairing ? 0 : audioOptionsHeight;
            int helpHeight = 0;
            if (showHelp)
            {
                int required = PreferredHeight(helpText, width) + 4;
                int deviceFloor = connected
                    ? Math.Max(deviceList.ItemHeight, Math.Min(88, PreferredHeight(emptyDevices, width)))
                    : deviceList.ItemHeight;
                int occupied = (int)(sidebarLayout.RowStyles[0].Height + sidebarLayout.RowStyles[1].Height
                    + sidebarLayout.RowStyles[3].Height + sidebarLayout.RowStyles[4].Height
                    + sidebarLayout.RowStyles[5].Height + sidebarLayout.RowStyles[6].Height
                    + discoveryPanel.RowStyles[1].Height + discoveryPanel.RowStyles[2].Height) + deviceFloor;
                int oneLine = TextRenderer.MeasureText("状态", helpText.Font).Height + helpText.Padding.Vertical + 4;
                helpHeight = Math.Min(required, Math.Max(oneLine, sidebarLayout.ClientSize.Height - occupied));
            }
            sidebarLayout.RowStyles[7].Height = helpHeight;
            sidebarLayout.ResumeLayout();
            if (showHelp)
            {
                helpStatusSurface.FitContent();
            }
        }

        private bool PostToUi(Action callback)
        {
            if (closing || !IsHandleCreated)
            {
                return false;
            }

            try
            {
                BeginInvoke(callback);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static TableLayoutPanel NewTable(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Margin = Padding.Empty,
                BackColor = Color.Transparent,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
        }

        private static Panel WhiteCard()
        {
            return new SoftSurface(Color.White, Color.White, 24) { Padding = new Padding(CardPadding) };
        }

        private static int PreferredHeight(Control control, int width)
        {
            return control.GetPreferredSize(new Size(width, 0)).Height;
        }

        private static void AddFixedRows(TableLayoutPanel table, params int[] heights)
        {
            foreach (int height in heights)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            }
        }

        private static void AddWide(TableLayoutPanel table, Control control, int row)
        {
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, table.ColumnCount);
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Microsoft YaHei UI", size, style),
                ForeColor = color,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.TopLeft
            };
        }

        private static Label MakeWrappedLabel(string text, float size, FontStyle style, Color color, Color background)
        {
            return new WrappedLabel
            {
                Text = text,
                Font = new Font("Microsoft YaHei UI", size, style),
                ForeColor = color,
                BackColor = background,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false
            };
        }

        private sealed class WrappedLabel : Label
        {
            private const TextFormatFlags LineFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
            private const string ClosingPunctuation = "。！？，；：、）》」』】〕〉％…";

            public WrappedLabel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                int width = proposedSize.Width <= 0 ? int.MaxValue : Math.Max(1, proposedSize.Width - Padding.Horizontal);
                List<string> lines = Wrap(width);
                int widest = 0;
                foreach (string line in lines)
                {
                    widest = Math.Max(widest, Measure(line).Width);
                }
                return new Size(widest + Padding.Horizontal, lines.Count * LineHeight() + Padding.Vertical);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Rectangle area = new Rectangle(Padding.Left, Padding.Top,
                    Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
                int lineHeight = LineHeight();
                if (area.Width == 0 || area.Height < lineHeight)
                {
                    return;
                }
                List<string> lines = Wrap(area.Width);
                bool centered = TextAlign == ContentAlignment.MiddleCenter;
                int y = area.Top + (centered ? Math.Max(0, (area.Height - lines.Count * lineHeight) / 2) : 0);
                TextFormatFlags flags = LineFlags | (centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
                foreach (string line in lines)
                {
                    if (y + lineHeight > area.Bottom)
                    {
                        break;
                    }
                    TextRenderer.DrawText(e.Graphics, line, Font, new Rectangle(area.Left, y, area.Width, lineHeight),
                        ForeColor, flags);
                    y += lineHeight;
                }
            }

            private int LineHeight()
            {
                return Measure("Hg国").Height;
            }

            private Size Measure(string text)
            {
                return TextRenderer.MeasureText(text, Font, Size.Empty, LineFlags);
            }

            private List<string> Wrap(int width)
            {
                List<string> result = new List<string>();
                if (Text.Length == 0)
                {
                    return result;
                }
                foreach (string paragraph in Text.Replace("\r", "").Split('\n'))
                {
                    string current = "";
                    TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(paragraph);
                    while (elements.MoveNext())
                    {
                        string element = elements.GetTextElement();
                        if (current.Length != 0 && Measure(current + element).Width > width)
                        {
                            if (ClosingPunctuation.IndexOf(element, StringComparison.Ordinal) >= 0)
                            {
                                string previous = current.TrimEnd();
                                int[] positions = StringInfo.ParseCombiningCharacters(previous);
                                if (positions.Length > 1)
                                {
                                    int last = positions[positions.Length - 1];
                                    string tail = previous.Substring(last) + element;
                                    if (Measure(tail).Width <= width)
                                    {
                                        result.Add(previous.Substring(0, last).TrimEnd());
                                        current = tail;
                                        continue;
                                    }
                                }
                            }
                            int breakAt = current.LastIndexOf(' ');
                            if (breakAt >= current.Length / 2 && breakAt > 0)
                            {
                                result.Add(current.Substring(0, breakAt));
                                current = current.Substring(breakAt + 1);
                            }
                            else
                            {
                                result.Add(current.TrimEnd());
                                current = "";
                            }
                        }
                        if (current.Length != 0 || !string.IsNullOrWhiteSpace(element))
                        {
                            current += element;
                        }
                    }
                    result.Add(current.TrimEnd());
                }
                return result;
            }
        }

        private sealed class StatusViewport : Panel
        {
            private readonly Label content;
            private bool arranging;

            public StatusViewport(Label content, Color fill)
            {
                this.content = content;
                BackColor = fill;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                AutoScroll = true;
                content.Dock = DockStyle.Top;
                Controls.Add(content);
                content.TextChanged += (sender, args) => FitContent();
                SizeChanged += (sender, args) => FitContent();
            }

            public void FitContent()
            {
                if (arranging || Width < 1 || Height < 1)
                {
                    return;
                }
                arranging = true;
                try
                {
                    int required = PreferredHeight(content, Width);
                    if (required > Height)
                    {
                        required = PreferredHeight(content, Math.Max(1, Width - SystemInformation.VerticalScrollBarWidth));
                    }
                    content.Height = Math.Max(Height, required);
                    AutoScrollMinSize = required > Height ? new Size(0, required) : Size.Empty;
                }
                finally
                {
                    arranging = false;
                }
            }
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button button = new SoftButton
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
                BackColor = primary ? Accent : Blush,
                ForeColor = primary ? Color.White : Accent,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(145, 45, 98) : Color.FromArgb(248, 209, 226);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(129, 36, 87) : Color.FromArgb(242, 195, 217);
            button.SizeChanged += (sender, e) => SetRoundedRegion(button, 14);
            return button;
        }

        private static CheckBox MakeAudioOption(string text)
        {
            return new CheckBox
            {
                AccessibleName = text,
                Text = "",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = AudioFill,
                ForeColor = Ink,
                Font = new Font("Microsoft YaHei UI", 9, FontStyle.Regular),
                CheckAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft,
                UseVisualStyleBackColor = false
            };
        }

        private sealed class SoftButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled)
                {
                    base.OnPaint(e);
                    return;
                }

                e.Graphics.Clear(DisabledFill);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private static void PaintGradient(Graphics graphics, Rectangle area, Color start, Color finish)
        {
            if (area.Width < 1 || area.Height < 1)
            {
                return;
            }

            using (LinearGradientBrush brush = new LinearGradientBrush(area, start, finish, 28f))
            {
                graphics.FillRectangle(brush, area);
            }
        }

        private static void SetRoundedRegion(Control control, int radius)
        {
            int width = control.Width;
            int height = control.Height;
            int diameter = Math.Min(radius * 2, Math.Min(width, height));
            if (diameter < 2)
            {
                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(width - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(width - diameter, height - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, height - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                Region previous = control.Region;
                control.Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        private sealed class SoftSurface : Panel
        {
            private readonly Color start;
            private readonly Color finish;
            private readonly int radius;

            public SoftSurface(Color start, Color finish, int radius)
            {
                this.start = start;
                this.finish = finish;
                this.radius = radius;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = start;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                SetRoundedRegion(this, radius);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                PaintGradient(e.Graphics, ClientRectangle, start, finish);
            }
        }
    }
}
