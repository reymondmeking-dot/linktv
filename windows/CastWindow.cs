using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private const int CardPadding = 19;
        private const int AudioInset = 18;
        private const int AudioGap = 12;
        private static readonly Color Background = Color.FromArgb(255, 248, 243);
        private static readonly Color Blush = Color.FromArgb(252, 224, 236);
        private static readonly Color Lavender = Color.FromArgb(237, 226, 250);
        private static readonly Color Ink = Color.FromArgb(67, 44, 77);
        private static readonly Color Muted = Color.FromArgb(111, 81, 119);
        private static readonly Color Accent = Color.FromArgb(162, 59, 112);
        private static readonly Color PreviewBackground = Color.FromArgb(48, 34, 57);
        private static readonly Color DisabledFill = Color.FromArgb(240, 229, 240);

        private readonly ListBox deviceList = new ListBox();
        private readonly Label emptyDevices = MakeLabel("正在查找附近设备…", 10, FontStyle.Regular, Muted);
        private readonly Button connectButton = MakeButton("连接设备", false);
        private readonly Button confirmButton = MakeButton("确认", true);
        private readonly Button cancelButton = MakeButton("取消", false);
        private readonly Label pairingTitle = MakeLabel("", 11, FontStyle.Bold, Ink);
        private readonly Label pairingNumber = MakeLabel("", 24, FontStyle.Bold, Accent);
        private readonly Label pairingHelp = MakeLabel("", 9, FontStyle.Regular, Muted);
        private readonly Button sendButton = MakeButton("投屏到设备", true);
        private readonly CheckBox syncAudioOption = MakeAudioOption("同步本机声音");
        private readonly CheckBox muteComputerOption = MakeAudioOption("本机静音");
        private readonly Label helpText = MakeLabel("", 9, FontStyle.Regular, Muted);
        private readonly Label connectionStatus = MakeLabel("正在检查网络", 10, FontStyle.Regular, Muted);
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
        private Panel audioOptions;
        private int sendHeight;
        private int audioOptionsHeight;
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
            MinimumSize = new Size(780, 580);
            BackColor = Background;
            ForeColor = Ink;
            SetStyle(ControlStyles.ResizeRedraw, true);

            SuspendLayout();
            int inlineAudioWidth = syncAudioOption.GetPreferredSize(Size.Empty).Width
                + muteComputerOption.GetPreferredSize(Size.Empty).Width + AudioGap + AudioInset;
            int sidebarWidth = Math.Max(300, Math.Min(400, inlineAudioWidth + CardPadding * 2));
            bool stackAudio = sidebarWidth < inlineAudioWidth + CardPadding * 2;
            TableLayoutPanel shell = NewTable(3, 2);
            shell.Padding = new Padding(26, 16, 26, 26);
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, sidebarWidth));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int headerHeight;
            TableLayoutPanel header = BuildHeader(out headerHeight);
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, headerHeight));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            shell.Controls.Add(header, 0, 0);
            shell.SetColumnSpan(header, 3);
            shell.Controls.Add(BuildPairingCard(sidebarWidth - 2 * CardPadding, stackAudio), 0, 1);
            shell.Controls.Add(BuildPreview(), 2, 1);
            Controls.Add(shell);
            ResumeLayout();

            connectButton.Click += ToggleConnection;
            deviceList.SelectedIndexChanged += (sender, e) => UpdatePresentation();
            confirmButton.Click += (sender, e) => ConfirmPairing(true);
            cancelButton.Click += (sender, e) => ConfirmPairing(false);
            sendButton.Click += ToggleSending;
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

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
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
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = MakeLabel("双向投屏", 26, FontStyle.Bold, Ink);
            title.TextAlign = ContentAlignment.MiddleLeft;
            connectionStatus.TextAlign = ContentAlignment.MiddleCenter;
            connectionStatus.AutoEllipsis = true;
            SoftSurface status = new SoftSurface(Lavender, Blush, 16);
            int statusHeight = PreferredHeight(connectionStatus, 208) + 16;
            height = Math.Max(68, Math.Max(PreferredHeight(title, 0), statusHeight) + 18);
            int inset = (height - statusHeight) / 2;
            status.Margin = new Padding(0, inset, 0, height - statusHeight - inset);
            status.Controls.Add(connectionStatus);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(status, 1, 0);
            return header;
        }

        private Panel BuildPairingCard(int width, bool stackAudio)
        {
            Panel surface = WhiteCard();
            TableLayoutPanel form = NewTable(1, 8);
            sidebarLayout = form;
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label title = MakeLabel("附近设备", 15, FontStyle.Bold, Ink);
            Label tip = MakeLabel("在对方设备打开「双向投屏」。", 9, FontStyle.Regular, Muted);
            sendHeight = Math.Max(42, PreferredHeight(sendButton, width) + 6);
            audioOptions = BuildAudioOptions(stackAudio);
            AddFixedRows(form, PreferredHeight(title, width) + 6, PreferredHeight(tip, width) + 9);
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(form, 12, sendHeight, 10, audioOptionsHeight, 0);

            AddWide(form, title, 0);
            AddWide(form, tip, 1);
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
            AddWide(form, helpText, 7);

            surface.Controls.Add(form);
            return surface;
        }

        private Panel BuildAudioOptions(bool stacked)
        {
            SoftSurface surface = new SoftSurface(Background, Color.FromArgb(252, 239, 247), 14);
            surface.Padding = new Padding(10, 0, 8, 0);
            int lineHeight = Math.Max(PreferredHeight(syncAudioOption, 0), PreferredHeight(muteComputerOption, 0));
            TableLayoutPanel options;
            if (stacked)
            {
                options = NewTable(1, 2);
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                options.Controls.Add(syncAudioOption, 0, 0);
                options.Controls.Add(muteComputerOption, 0, 1);
                audioOptionsHeight = lineHeight * 2 + 12;
            }
            else
            {
                options = NewTable(3, 1);
                options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, syncAudioOption.GetPreferredSize(Size.Empty).Width));
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AudioGap));
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                options.Controls.Add(syncAudioOption, 0, 0);
                options.Controls.Add(muteComputerOption, 2, 0);
                audioOptionsHeight = Math.Max(42, lineHeight + 12);
            }
            surface.Controls.Add(options);
            return surface;
        }

        private TableLayoutPanel BuildDiscoveryPanel()
        {
            TableLayoutPanel table = NewTable(1, 3);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddFixedRows(table, 9, Math.Max(39, PreferredHeight(connectButton, 0) + 6));

            SoftSurface listArea = new SoftSurface(Background, Color.FromArgb(252, 243, 251), 17);
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
            emptyDevices.AutoEllipsis = true;
            listArea.Controls.Add(deviceList);
            listArea.Controls.Add(emptyDevices);
            emptyDevices.BringToFront();

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
            table.BackColor = Color.FromArgb(252, 240, 249);
            table.Padding = new Padding(11, 9, 11, 9);
            table.SizeChanged += (sender, args) => SetRoundedRegion(table, 17);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pairingTitle.Text = "连接设备";
            pairingTitle.AutoEllipsis = true;
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
            return table;
        }

        private Panel BuildPreview()
        {
            Panel surface = WhiteCard();
            TableLayoutPanel card = NewTable(1, 2);
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label title = MakeLabel("设备画面", 15, FontStyle.Bold, Ink);
            AddFixedRows(card, PreferredHeight(title, 0) + 8);
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddWide(card, title, 0);

            SoftSurface canvas = new SoftSurface(PreviewBackground, PreviewBackground, 20);
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
            surface.Controls.Add(card);
            return surface;
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

        private void StartListening()
        {
            CastHub listener = null;
            NearbyTvDiscovery nearby = null;
            try
            {
                IPAddress mask;
                uint interfaceIndex;
                IPAddress address = FindLocalAddress(out mask, out interfaceIndex);
                if (address == null)
                {
                    operationMessage = "请先把电脑连到与设备相同的网络，再点「重新检查网络」。";
                    UpdatePresentation();
                    return;
                }

                string localName = "电脑 " + Environment.MachineName;
                ffmpeg = FfmpegVideo.FindExecutable();
                byte capabilities = ffmpeg == null ? (byte)0 : (byte)(CastHub.H264Send | CastHub.H264Receive);
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
                if (hub != listener && listener != null)
                {
                    listener.Dispose();
                }
                if (discovery != nearby && nearby != null)
                {
                    nearby.Dispose();
                }
            }

            UpdatePresentation();
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
                }
                catch (Exception error)
                {
                    mute.Dispose();
                    audioWarning = "无法将本机静音，对方设备仍在接收声音。";
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

            connectionStatus.Text = hub == null ? "网络未就绪" : screenState;
            connectionStatus.ForeColor = connected || pairing ? Accent : Ink;
            connectButton.Text = hub == null ? "重新检查网络" : connected ? "断开连接" : "连接设备";
            connectButton.Enabled = hub == null || connected || deviceList.SelectedItem != null;
            emptyDevices.Text = connected
                ? connectedName ?? "已连接"
                : hub == null ? "还未连接到局域网" : "正在查找附近设备…";
            emptyDevices.Visible = connected || hub == null || deviceList.Items.Count == 0;
            deviceList.Enabled = !connected;
            syncAudioOption.Enabled = audioSupported && !sending && !receiving;
            muteComputerOption.Enabled = audioSupported && syncAudioOption.Checked && !receiving && !stopping;
            string audioHint = null;
            if (connected && !audioSupported)
            {
                audioHint = "当前 Windows 版本支持画面投屏。";
            }
            else if (sending && !stopping && muteComputerOption.Checked && !audioReady)
            {
                audioHint = "开始同步声音后，电脑会自动静音。";
            }
            helpText.Text = operationMessage ?? audioWarning ?? audioHint
                ?? (connected ? "在对方设备上选「投屏到设备」即可接收画面。" : "");
            ArrangeSidebar(pairing);

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

            previewIdle.Visible = !receiving || picture.Image == null;
        }

        private void ArrangeSidebar(bool pairing)
        {
            sidebarLayout.SuspendLayout();
            sendButton.Visible = !pairing;
            audioOptions.Visible = !pairing;
            helpText.Visible = !pairing && helpText.Text.Length != 0;
            sidebarLayout.RowStyles[3].Height = pairing ? 0 : 12;
            sidebarLayout.RowStyles[4].Height = pairing ? 0 : sendHeight;
            sidebarLayout.RowStyles[5].Height = pairing ? 0 : 10;
            sidebarLayout.RowStyles[6].Height = pairing ? 0 : audioOptionsHeight;
            int width = Math.Max(1, sidebarLayout.ClientSize.Width);
            sidebarLayout.RowStyles[7].Height = pairing || helpText.Text.Length == 0
                ? 0
                : PreferredHeight(helpText, width) + 4;
            sidebarLayout.ResumeLayout();
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
            return new SoftSurface(Color.White, Color.FromArgb(255, 252, 253), 24) { Padding = new Padding(CardPadding) };
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
            return new SoftCheckBox
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = Color.Transparent,
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

        private sealed class SoftCheckBox : CheckBox
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled)
                {
                    base.OnPaint(e);
                    return;
                }

                System.Windows.Forms.VisualStyles.CheckBoxState state = Checked
                    ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled
                    : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
                Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(0, (Height - glyph.Height) / 2), state);
                int textLeft = glyph.Width + 5;
                Rectangle text = Rectangle.FromLTRB(textLeft, 0, Math.Max(textLeft, Width), Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, text, Muted,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
