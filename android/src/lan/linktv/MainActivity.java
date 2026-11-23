package lan.linktv;

import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.BitmapDrawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.Collections;
import java.util.List;

public final class MainActivity extends Activity implements LinkService.Listener {
    private static final int CAPTURE_REQUEST = 481;
    private static final int FULLSCREEN_CONTROLS_TIMEOUT_MS = 3000;
    private static final int BACKGROUND = 0xfffff8f3;
    private static final int PANEL = 0xfffffcfa;
    private static final int BLUSH = 0xfffce0ec;
    private static final int LAVENDER = 0xffede2fa;
    private static final int BORDER = 0xffe1c8dc;
    private static final int RASPBERRY = 0xffa23b70;
    private static final int FOREGROUND = 0xff432c4d;
    private static final int DIM = 0xff73596e;

    private final Handler fullscreenTimer = new Handler(Looper.getMainLooper());
    private final Runnable fullscreenTimeout = this::hideFullscreenActions;

    private final ServiceConnection connection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName name, IBinder binder) {
            service = ((LinkService.LocalBinder) binder).getService();
            if (videoSurface != null) {
                service.attachVideoSurface(videoSurface);
            }
            if (active) {
                service.setListener(MainActivity.this);
            }
            if (pendingPermission != null) {
                service.startCapture(pendingResultCode, pendingPermission);
                pendingPermission = null;
            }
        }

        @Override
        public void onServiceDisconnected(ComponentName name) {
            service = null;
            onStateChanged();
        }
    };

    private LinearLayout root;
    private LinearLayout header;
    private LinearLayout nearby;
    private LinearLayout deviceRows;
    private LinearLayout pairing;
    private LinearLayout connected;
    private LinearLayout actionBar;
    private FrameLayout canvas;
    private Button scanButton;
    private Button confirmButton;
    private Button cancelButton;
    private Button primaryAction;
    private Button stopAction;
    private Button fullscreenButton;
    private Button disconnectButton;
    private LinearLayout fullscreenActions;
    private Button exitFullscreenAction;
    private Object automaticallyOpenedStream;
    private TextView status;
    private TextView placeholder;
    private TextView nearbyHint;
    private TextView pairingTitle;
    private TextView matchNumber;
    private TextView matchHint;
    private TextView peerName;
    private ImageView display;
    private SurfaceView video;
    private Surface videoSurface;
    private AvcReceiver displayedVideo;
    private View currentPanel;
    private List<Discovery.Peer> renderedPeers;
    private Bitmap lastFrame;
    private boolean compact;
    private boolean television;
    private boolean active;
    private boolean bound;
    private boolean fullscreen;
    private int revealKeyCode = KeyEvent.KEYCODE_UNKNOWN;
    private int revealKeyDevice;
    private long revealKeyDownTime = -1;
    private boolean requestingCapture;
    private int pendingResultCode;
    private Intent pendingPermission;
    private LinkService service;

    @Override
    protected void onCreate(Bundle previous) {
        super.onCreate(previous);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        buildLayout();
        bound = bindService(new Intent(this, LinkService.class), connection, Context.BIND_AUTO_CREATE);
        onStateChanged();
    }

    @Override
    protected void onStart() {
        super.onStart();
        active = true;
        if (service != null) {
            service.setListener(this);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (fullscreen) {
            hideFullscreenActions();
        }
    }

    @Override
    protected void onPause() {
        clearRevealKey();
        if (fullscreen) {
            hideFullscreenActions();
        }
        super.onPause();
    }

    @Override
    protected void onStop() {
        active = false;
        if (service != null) {
            service.clearListener(this);
        }
        super.onStop();
    }

    @Override
    protected void onDestroy() {
        fullscreenTimer.removeCallbacks(fullscreenTimeout);
        detachVideoSurface();
        if (bound) {
            unbindService(connection);
        }
        super.onDestroy();
    }

    @Override
    public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        boolean restoreFullscreen = fullscreen;
        fullscreenTimer.removeCallbacks(fullscreenTimeout);
        fullscreen = false;
        currentPanel = null;
        renderedPeers = null;
        detachVideoSurface();
        displayedVideo = null;
        buildLayout();
        onStateChanged();
        onFrame(lastFrame);
        if (restoreFullscreen && service != null && service.isReceiving()) {
            setFullscreen(true);
        } else {
            updateSystemBars();
        }
    }

    private void buildLayout() {
        Configuration configuration = getResources().getConfiguration();
        compact = configuration.screenWidthDp < 650;
        television = (configuration.uiMode & Configuration.UI_MODE_TYPE_MASK)
                == Configuration.UI_MODE_TYPE_TELEVISION
                || getPackageManager().hasSystemFeature("android.software.leanback");
        root = column();
        applyScreenPadding(false);
        root.setBackground(new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,
                new int[] { BACKGROUND, 0xfff9edf6 }));

        header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        int mascot = getResources().getIdentifier("linktv_mascot", "drawable", getPackageName());
        if (mascot != 0) {
            ImageView mark = new ImageView(this);
            mark.setImageResource(mascot);
            mark.setScaleType(ImageView.ScaleType.FIT_CENTER);
            mark.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            int markSize = dp(compact ? 34 : 52);
            LinearLayout.LayoutParams markLayout = new LinearLayout.LayoutParams(markSize, markSize);
            markLayout.rightMargin = dp(10);
            header.addView(mark, markLayout);
        }
        TextView title = text("双向投屏", compact ? 23 : 28, FOREGROUND);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title, new LinearLayout.LayoutParams(0, dp(46), 1));
        status = text("正在搜索附近设备…", 15, RASPBERRY);
        status.setGravity(Gravity.CENTER);
        status.setPadding(dp(14), 0, dp(14), 0);
        status.setBackground(box(LAVENDER, BORDER, dp(22)));
        status.setMaxWidth(dp(compact ? 136 : 365));
        status.setSingleLine(true);
        status.setEllipsize(android.text.TextUtils.TruncateAt.END);
        header.addView(status, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(38)));
        LinearLayout.LayoutParams headerLayout = wide();
        headerLayout.bottomMargin = dp(8);
        root.addView(header, headerLayout);

        buildNearbyPanel();
        buildPairingPanel();
        buildConnectedPanel();
        setContentView(root);
    }

    private void buildNearbyPanel() {
        nearby = column();
        LinearLayout heading = new LinearLayout(this);
        heading.setGravity(Gravity.CENTER_VERTICAL);
        TextView label = text("附近设备", 22, FOREGROUND);
        label.setTypeface(null, Typeface.BOLD);
        heading.addView(label, new LinearLayout.LayoutParams(0, dp(50), 1));
        scanButton = button("重新搜索", false);
        scanButton.setOnClickListener(view -> {
            if (service != null) {
                service.scan();
            }
        });
        heading.addView(scanButton, new LinearLayout.LayoutParams(dp(128), LinearLayout.LayoutParams.WRAP_CONTENT));
        nearby.addView(heading, wide());

        nearbyHint = text("请在同一网络中的设备上打开「双向投屏」。", 15, DIM);
        nearbyHint.setPadding(dp(14), dp(10), dp(14), dp(10));
        nearbyHint.setBackground(box(LAVENDER, BORDER, dp(18)));
        LinearLayout.LayoutParams hintLayout = wide();
        hintLayout.bottomMargin = dp(12);
        nearby.addView(nearbyHint, hintLayout);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(false);
        scroll.setFocusable(false);
        deviceRows = column();
        scroll.addView(deviceRows, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT, ScrollView.LayoutParams.WRAP_CONTENT));
        nearby.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1));
        addPanel(nearby);
    }

    private void buildPairingPanel() {
        pairing = column();
        pairing.setGravity(Gravity.CENTER);
        pairing.setPadding(dp(16), dp(12), dp(16), dp(12));
        pairing.setBackground(box(LAVENDER, BORDER, dp(24)));

        pairingTitle = text("正在连接设备", 24, FOREGROUND);
        pairingTitle.setTypeface(null, Typeface.BOLD);
        pairingTitle.setGravity(Gravity.CENTER);
        pairing.addView(pairingTitle, wide());

        matchNumber = text("", 46, RASPBERRY);
        matchNumber.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        matchNumber.setLetterSpacing(0.10f);
        matchNumber.setGravity(Gravity.CENTER);
        matchNumber.setPadding(0, dp(9), 0, dp(9));
        pairing.addView(matchNumber, wide());

        matchHint = text("", 16, DIM);
        matchHint.setGravity(Gravity.CENTER);
        pairing.addView(matchHint, wide());

        LinearLayout choices = new LinearLayout(this);
        choices.setGravity(Gravity.CENTER);
        confirmButton = button("确认", true);
        confirmButton.setOnClickListener(view -> {
            if (service != null) {
                service.confirmPairing();
            }
        });
        cancelButton = button("取消", false);
        cancelButton.setOnClickListener(view -> cancelPairing());
        addButton(choices, confirmButton, 124);
        addButton(choices, cancelButton, 124);
        LinearLayout.LayoutParams row = wide();
        row.topMargin = dp(16);
        pairing.addView(choices, row);
        addPanel(pairing);
    }

    private void buildConnectedPanel() {
        connected = column();
        primaryAction = button("投屏到设备", true);
        primaryAction.setOnClickListener(view -> requestCapture());
        stopAction = button("停止投屏", false);
        stopAction.setOnClickListener(view -> {
            if (service == null) {
                return;
            }
            if (service.isReceiving()) {
                service.stopWatching();
            } else if (service.isCapturing()) {
                service.stopCapture();
            }
        });
        fullscreenButton = button("全屏观看", false);
        fullscreenButton.setOnClickListener(view -> setFullscreen(true));
        disconnectButton = button("断开连接", false);
        disconnectButton.setOnClickListener(view -> {
            if (service != null) {
                service.disconnect();
            }
        });

        peerName = text("", 14, DIM);
        peerName.setSingleLine(true);
        peerName.setEllipsize(android.text.TextUtils.TruncateAt.END);
        if (compact) {
            actionBar = column();
            actionBar.addView(peerName, wide());
            addCompactRow(primaryAction, stopAction);
            addCompactRow(fullscreenButton, disconnectButton);
        } else {
            actionBar = new LinearLayout(this);
            actionBar.setGravity(Gravity.CENTER_VERTICAL);
            addButton(actionBar, primaryAction, 128);
            addButton(actionBar, stopAction, 136);
            addButton(actionBar, fullscreenButton, 128);
            addButton(actionBar, disconnectButton, 122);
            peerName.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
            actionBar.addView(peerName, new LinearLayout.LayoutParams(0, dp(48), 1));
        }
        LinearLayout.LayoutParams actions = wide();
        actions.bottomMargin = dp(10);
        connected.addView(actionBar, actions);

        canvas = new FrameLayout(this);
        canvas.setBackground(box(Color.BLACK, BORDER, dp(20)));
        canvas.setOnClickListener(view -> revealFullscreenActions());
        canvas.setClickable(false);
        canvas.setFocusable(false);
        addVideoSurface();
        display = new ImageView(this);
        display.setScaleType(ImageView.ScaleType.FIT_CENTER);
        display.setContentDescription("投屏画面");
        display.setFocusable(false);
        canvas.addView(display, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        placeholder = text("", 16, 0xfff5dce8);
        placeholder.setGravity(Gravity.CENTER);
        placeholder.setPadding(dp(16), dp(4), dp(16), dp(4));
        canvas.addView(placeholder, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        addFullscreenActions();
        connected.addView(canvas, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1));
        addPanel(connected);
    }

    private void addVideoSurface() {
        SurfaceView owner = new SurfaceView(this);
        video = owner;
        owner.setVisibility(View.GONE);
        owner.setFocusable(false);
        owner.setContentDescription("投屏画面");
        owner.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                if (video == owner) {
                    videoSurface = holder.getSurface();
                    if (service != null) {
                        service.attachVideoSurface(videoSurface);
                    }
                }
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {}

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                if (video == owner) {
                    detachVideoSurface();
                }
            }
        });
        canvas.addView(owner, new FrameLayout.LayoutParams(1, 1, Gravity.CENTER));
        canvas.addOnLayoutChangeListener((view, left, top, right, bottom,
                                          oldLeft, oldTop, oldRight, oldBottom) ->
                sizeVideoSurface(right - left, bottom - top));
    }

    private void detachVideoSurface() {
        if (videoSurface != null) {
            if (service != null) {
                service.detachVideoSurface(videoSurface);
            }
            videoSurface = null;
        }
    }

    private void updateVideoSurface() {
        AvcReceiver stream = service == null ? null : service.displayedVideo();
        if (displayedVideo == stream) {
            return;
        }
        displayedVideo = stream;
        if (stream == null) {
            detachVideoSurface();
            video.setVisibility(View.GONE);
        } else {
            video.getHolder().setFixedSize(stream.width, stream.height);
            sizeVideoSurface(canvas.getWidth(), canvas.getHeight());
            video.setVisibility(View.VISIBLE);
        }
    }

    private void sizeVideoSurface(int availableWidth, int availableHeight) {
        AvcReceiver stream = displayedVideo;
        if (stream == null || availableWidth <= 0 || availableHeight <= 0) {
            return;
        }
        int width = availableWidth;
        int height = (int) ((long) width * stream.height / stream.width);
        if (height > availableHeight) {
            height = availableHeight;
            width = (int) ((long) height * stream.width / stream.height);
        }
        FrameLayout.LayoutParams size = (FrameLayout.LayoutParams) video.getLayoutParams();
        if (size.width != width || size.height != height) {
            size.width = width;
            size.height = height;
            video.setLayoutParams(size);
        }
    }

    private void addFullscreenActions() {
        fullscreenActions = new LinearLayout(this);
        fullscreenActions.setClickable(false);
        fullscreenActions.setFocusable(false);
        fullscreenActions.setVisibility(View.GONE);
        exitFullscreenAction = button("退出全屏", true);
        exitFullscreenAction.setOnClickListener(view -> setFullscreen(false));

        Button stopWatching = button("停止观看", false);
        stopWatching.setOnClickListener(view -> {
            if (service != null) {
                service.stopWatching();
            }
        });
        linkHorizontalFocus(exitFullscreenAction, stopWatching);
        addButton(fullscreenActions, exitFullscreenAction, 116);
        addButton(fullscreenActions, stopWatching, 116);

        FrameLayout.LayoutParams overlay = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP | Gravity.END);
        overlay.topMargin = dp(television ? 22 : 10);
        overlay.setMarginEnd(dp(television ? 20 : 8));
        canvas.addView(fullscreenActions, overlay);
    }

    private void addCompactRow(Button first, Button second) {
        LinearLayout row = new LinearLayout(this);
        LinearLayout.LayoutParams primary = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        primary.rightMargin = dp(8);
        row.addView(first, primary);
        row.addView(second, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout.LayoutParams line = wide();
        line.topMargin = dp(6);
        actionBar.addView(row, line);
    }

    private void applyScreenPadding(boolean full) {
        int horizontal = full ? 0 : dp(compact ? 16 : 34);
        int vertical = full ? 0 : dp(compact ? 12 : 22);
        root.setPadding(horizontal, vertical, horizontal, vertical);
    }

    private void addPanel(View panel) {
        panel.setVisibility(View.GONE);
        root.addView(panel, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1));
    }

    private void addButton(LinearLayout group, Button button, int width) {
        LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(dp(width), LinearLayout.LayoutParams.WRAP_CONTENT);
        layout.rightMargin = dp(8);
        group.addView(button, layout);
    }

    private LinearLayout column() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        return layout;
    }

    private LinearLayout.LayoutParams wide() {
        return new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private Button button(String title, boolean primary) {
        Button button = new Button(this);
        button.setId(View.generateViewId());
        button.setAllCaps(false);
        button.setClickable(true);
        button.setFocusable(true);
        if (television) {
            button.setFocusableInTouchMode(true);
        }
        button.setText(title);
        button.setTextSize(television ? 16 : 15);
        button.setGravity(Gravity.CENTER);
        button.setMaxLines(2);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setMinHeight(dp(48));
        button.setMinimumHeight(dp(48));
        button.setPadding(dp(12), dp(7), dp(12), dp(7));
        button.setStateListAnimator(null);
        button.setElevation(0);
        button.setTranslationZ(0);
        button.setBackgroundTintList(null);

        StateListDrawable shapes = new StateListDrawable();
        shapes.addState(new int[] { -android.R.attr.state_enabled }, box(0xfff3edf2, 0xffdfd2dd, dp(18)));
        shapes.addState(new int[] { android.R.attr.state_pressed }, box(0xff741d48, 0xffffdbea, dp(18), dp(2)));
        shapes.addState(new int[] { android.R.attr.state_focused }, box(RASPBERRY, FOREGROUND, dp(18), dp(3)));
        shapes.addState(new int[0], box(primary ? BLUSH : PANEL, BORDER, dp(18)));
        button.setBackground(shapes);
        button.setTextColor(new ColorStateList(new int[][] {
            new int[] { -android.R.attr.state_enabled },
            new int[] { android.R.attr.state_pressed },
            new int[] { android.R.attr.state_focused },
            new int[0]
        }, new int[] { 0xff886f84, Color.WHITE, Color.WHITE, FOREGROUND }));
        return button;
    }

    private GradientDrawable box(int fill, int stroke, int radius) {
        return box(fill, stroke, radius, dp(1));
    }

    private GradientDrawable box(int fill, int stroke, int radius, int strokeWidth) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill);
        shape.setStroke(strokeWidth, stroke);
        shape.setCornerRadius(radius);
        return shape;
    }

    private void setActionEnabled(Button action, boolean enabled) {
        action.setEnabled(enabled);
        action.setClickable(enabled);
        action.setFocusableInTouchMode(enabled && television);
        action.setFocusable(enabled);
    }

    private void linkHorizontalFocus(Button... controls) {
        Button previous = null;
        for (Button control : controls) {
            control.setNextFocusLeftId(View.NO_ID);
            control.setNextFocusRightId(View.NO_ID);
            control.setNextFocusForwardId(View.NO_ID);
            if (control.isEnabled()) {
                if (previous != null) {
                    previous.setNextFocusRightId(control.getId());
                    previous.setNextFocusForwardId(control.getId());
                    control.setNextFocusLeftId(previous.getId());
                }
                previous = control;
            }
        }
    }

    private void linkCastingFocus() {
        if (!compact) {
            linkHorizontalFocus(primaryAction, stopAction, fullscreenButton, disconnectButton);
            return;
        }
        linkHorizontalFocus(primaryAction, stopAction);
        linkHorizontalFocus(fullscreenButton, disconnectButton);
        primaryAction.setNextFocusDownId(enabledId(fullscreenButton, disconnectButton));
        stopAction.setNextFocusDownId(enabledId(disconnectButton, fullscreenButton));
        fullscreenButton.setNextFocusUpId(enabledId(primaryAction, stopAction));
        disconnectButton.setNextFocusUpId(enabledId(stopAction, primaryAction));
    }

    private int enabledId(Button preferred, Button alternate) {
        if (preferred.isEnabled()) {
            return preferred.getId();
        }
        return alternate.isEnabled() ? alternate.getId() : View.NO_ID;
    }

    private int dp(int value) {
        return Math.round(getResources().getDisplayMetrics().density * value);
    }

    private void showNearbyDevices(List<Discovery.Peer> peers) {
        if (peers.equals(renderedPeers)) {
            return;
        }
        boolean firstPeer = !peers.isEmpty() && (renderedPeers == null || renderedPeers.isEmpty());
        renderedPeers = peers;
        View focused = getCurrentFocus();
        Object focusedPeer = focused == null ? null : focused.getTag();
        deviceRows.removeAllViews();
        scanButton.setNextFocusDownId(View.NO_ID);
        Button previous = scanButton;
        View replacement = null;
        for (Discovery.Peer peer : peers) {
            Button row = button(peer.name, false);
            row.setTag(peer);
            row.setTextSize(17);
            row.setSingleLine(true);
            row.setEllipsize(android.text.TextUtils.TruncateAt.END);
            row.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            row.setPadding(dp(15), 0, dp(15), 0);
            row.setOnClickListener(view -> {
                if (service != null) {
                    service.connect(peer);
                }
            });
            LinearLayout.LayoutParams item = wide();
            item.height = LinearLayout.LayoutParams.WRAP_CONTENT;
            item.bottomMargin = dp(8);
            deviceRows.addView(row, item);
            previous.setNextFocusDownId(row.getId());
            row.setNextFocusUpId(previous.getId());
            previous = row;
            if (peer.equals(focusedPeer)) {
                replacement = row;
            }
        }
        if (peers.isEmpty()) {
            TextView empty = text("正在等待附近设备出现…", 16, DIM);
            empty.setPadding(dp(15), 0, dp(15), 0);
            empty.setBackground(box(BLUSH, BORDER, dp(18)));
            LinearLayout.LayoutParams item = wide();
            item.height = dp(57);
            deviceRows.addView(empty, item);
        }
        if (replacement != null) {
            replacement.requestFocus();
        } else if (currentPanel == nearby && (focusedPeer != null || firstPeer)) {
            nearbyDefaultFocus().requestFocus();
        }
    }

    private View nearbyDefaultFocus() {
        View first = deviceRows.getChildAt(0);
        return first instanceof Button ? first : scanButton;
    }

    private void cancelPairing() {
        if (service != null) {
            service.cancelPairing();
        }
    }

    private void requestCapture() {
        if (service == null || !service.isConnected() || service.isReceiving()
                || service.isStoppingWatching() || service.isCapturing() || requestingCapture) {
            return;
        }
        MediaProjectionManager manager = (MediaProjectionManager)
                getSystemService(MEDIA_PROJECTION_SERVICE);
        if (manager == null) {
            status.setText("设备未开放录屏功能");
            return;
        }
        try {
            requestingCapture = true;
            onStateChanged();
            startActivityForResult(manager.createScreenCaptureIntent(), CAPTURE_REQUEST);
        } catch (RuntimeException error) {
            requestingCapture = false;
            onStateChanged();
            status.setText("设备无法弹出录屏授权窗口");
        }
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != CAPTURE_REQUEST) {
            return;
        }
        requestingCapture = false;
        onStateChanged();
        if (service != null) {
            service.startCapture(result, data);
        } else if (result == RESULT_OK && data != null) {
            pendingResultCode = result;
            pendingPermission = data;
        }
    }

    @Override
    public void onStateChanged() {
        boolean paired = service != null && service.isConnected();
        boolean pairingActive = service != null && service.isConnecting();
        Object incoming = paired ? service.incomingStream() : null;
        boolean receiving = incoming != null;
        boolean stoppingWatching = paired && service.isStoppingWatching();
        boolean capturing = paired && service.isCapturing();
        if (!receiving && fullscreen) {
            setFullscreen(false);
        }
        if (requestingCapture) {
            status.setText(television ? "请使用遥控器确认录屏授权" : "请确认录屏授权");
        } else {
            status.setText(service == null ? "正在搜索附近设备…" : service.status());
        }
        setActionEnabled(scanButton, service != null);
        updateVideoSurface();
        updatePlaceholder();
        if (paired) {
            stopAction.setText(receiving ? "停止观看" : "停止投屏");
            setActionEnabled(primaryAction, !receiving && !capturing && !stoppingWatching && !requestingCapture);
            setActionEnabled(stopAction, receiving || capturing);
            setActionEnabled(fullscreenButton, receiving);
            setActionEnabled(disconnectButton, true);
            linkCastingFocus();
            peerName.setText(service.peerName());
            if (receiving) {
                placeholder.setText("正在等待投屏画面…");
            } else if (stoppingWatching) {
                placeholder.setText("正在停止观看…");
            } else if (capturing) {
                placeholder.setText("正在共享本机画面，可以切换到其他应用。");
            } else {
                placeholder.setText("在对方设备上点击「投屏到设备」即可播放。");
            }
            View nextFocus = receiving ? fullscreenButton : capturing ? stopAction
                    : primaryAction.isEnabled() ? primaryAction : disconnectButton;
            switchPanel(connected, nextFocus);
            if (active && receiving && incoming != automaticallyOpenedStream) {
                automaticallyOpenedStream = incoming;
                setFullscreen(true);
            } else if (!fullscreen && !hasEnabledFocus()) {
                nextFocus.requestFocus();
            }
        } else if (pairingActive) {
            String number = service.matchNumber();
            boolean awaiting = service.isWaitingForPeerConfirmation();
            boolean cancelling = service.isCancellingPairing();
            pairingTitle.setText("连接到 " + service.peerName());
            matchNumber.setText(number == null ? "" : number);
            if (cancelling) {
                matchHint.setText("正在取消配对…");
            } else if (number == null) {
                matchHint.setText("正在建立配对…");
            } else if (awaiting) {
                matchHint.setText("本机已确认，请在对方设备上确认。");
            } else {
                matchHint.setText("请确认对方设备上显示同样的数字。");
            }
            setActionEnabled(confirmButton, number != null && !awaiting && !cancelling);
            setActionEnabled(cancelButton, !cancelling);
            linkHorizontalFocus(confirmButton, cancelButton);
            switchPanel(pairing, cancelButton);
            if (!hasEnabledFocus() && !cancelling) {
                cancelButton.requestFocus();
            }
        } else {
            if (service != null) {
                nearbyHint.setText("同一网络中的其他设备也可以选择「" + service.localName() + "」连接。");
            }
            showNearbyDevices(service == null ? Collections.emptyList() : service.nearbyPeers());
            switchPanel(nearby, nearbyDefaultFocus());
            if (!nearby.hasFocus() && service != null) {
                nearbyDefaultFocus().requestFocus();
            }
        }
    }

    private boolean hasEnabledFocus() {
        View focused = root.findFocus();
        return focused instanceof Button && focused.isEnabled() && focused.isShown();
    }

    private void switchPanel(View next, View focus) {
        if (next == currentPanel) {
            return;
        }
        if (currentPanel != null) {
            currentPanel.setVisibility(View.GONE);
        }
        currentPanel = next;
        next.setVisibility(View.VISIBLE);
        focus.requestFocus();
    }

    @Override
    public void onFrame(Bitmap frame) {
        lastFrame = frame;
        BitmapDrawable image = null;
        if (frame != null) {
            image = new BitmapDrawable(getResources(), frame);
            image.setFilterBitmap(true);
        }
        display.setImageDrawable(image);
        updatePlaceholder();
    }

    private void updatePlaceholder() {
        boolean avcPicture = service != null && service.hasAvcPicture();
        placeholder.setVisibility(lastFrame == null && !avcPicture ? View.VISIBLE : View.GONE);
    }

    private void setFullscreen(boolean next) {
        if (next && (service == null || !service.isReceiving())) {
            return;
        }
        fullscreen = next;
        int controls = next ? View.GONE : View.VISIBLE;
        header.setVisibility(controls);
        actionBar.setVisibility(controls);
        canvas.setClickable(next);
        display.setFocusableInTouchMode(next);
        display.setFocusable(next);
        applyScreenPadding(next);
        canvas.setBackground(next ? box(Color.BLACK, Color.BLACK, 0) : box(Color.BLACK, BORDER, dp(20)));
        hideFullscreenActions();
        updateSystemBars();
        if (!next) {
            if (service != null && service.isReceiving()) {
                fullscreenButton.requestFocus();
            } else {
                (primaryAction.isEnabled() ? primaryAction : disconnectButton).requestFocus();
            }
        }
    }

    private void revealFullscreenActions() {
        if (!fullscreen) {
            return;
        }
        if (fullscreenActions.getVisibility() != View.VISIBLE) {
            fullscreenActions.setVisibility(View.VISIBLE);
            exitFullscreenAction.requestFocus();
        }
        renewFullscreenTimeout();
    }

    private void renewFullscreenTimeout() {
        fullscreenTimer.removeCallbacks(fullscreenTimeout);
        fullscreenTimer.postDelayed(fullscreenTimeout, FULLSCREEN_CONTROLS_TIMEOUT_MS);
    }

    private void hideFullscreenActions() {
        fullscreenTimer.removeCallbacks(fullscreenTimeout);
        fullscreenActions.setVisibility(View.GONE);
        if (fullscreen) {
            display.requestFocus();
        }
    }

    private void updateSystemBars() {
        int flags = fullscreen ? View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY : 0;
        getWindow().getDecorView().setSystemUiVisibility(flags);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && fullscreen) {
            updateSystemBars();
        }
    }

    private void clearRevealKey() {
        revealKeyCode = KeyEvent.KEYCODE_UNKNOWN;
        revealKeyDownTime = -1;
    }

    private boolean isNavigationKey(KeyEvent event) {
        switch (event.getKeyCode()) {
            case KeyEvent.KEYCODE_BACK:
            case KeyEvent.KEYCODE_DPAD_UP:
            case KeyEvent.KEYCODE_DPAD_DOWN:
            case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_RIGHT:
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
            case KeyEvent.KEYCODE_MENU:
            case KeyEvent.KEYCODE_BUTTON_A:
                return true;
            default:
                return !event.isSystem();
        }
    }

    private boolean interceptFullscreenKey(KeyEvent event) {
        int action = event.getAction();
        if (event.getDownTime() == revealKeyDownTime
                && event.getKeyCode() == revealKeyCode && event.getDeviceId() == revealKeyDevice) {
            if (action == KeyEvent.ACTION_UP) {
                clearRevealKey();
            }
            if (fullscreen) {
                if (action == KeyEvent.ACTION_DOWN) {
                    revealFullscreenActions();
                } else if (action == KeyEvent.ACTION_UP && fullscreenActions.getVisibility() == View.VISIBLE) {
                    renewFullscreenTimeout();
                }
            }
            return true;
        }
        if (!fullscreen) {
            return false;
        }
        boolean shown = fullscreenActions.getVisibility() == View.VISIBLE;
        if (action == KeyEvent.ACTION_DOWN) {
            revealFullscreenActions();
            if (!shown && isNavigationKey(event)) {
                revealKeyCode = event.getKeyCode();
                revealKeyDevice = event.getDeviceId();
                revealKeyDownTime = event.getDownTime();
                return true;
            }
        } else if (action == KeyEvent.ACTION_UP) {
            if (shown) {
                renewFullscreenTimeout();
            } else {
                return isNavigationKey(event);
            }
        }
        return false;
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (interceptFullscreenKey(event) || super.dispatchKeyEvent(event)) {
            return true;
        }
        int key = event.getKeyCode();
        if (television && event.getAction() == KeyEvent.ACTION_UP
                && (key == KeyEvent.KEYCODE_DPAD_CENTER || key == KeyEvent.KEYCODE_ENTER
                || key == KeyEvent.KEYCODE_BUTTON_A) && hasEnabledFocus()) {
            return root.findFocus().performClick();
        }
        return false;
    }

    @Override
    public void onBackPressed() {
        if (fullscreen) {
            if (fullscreenActions.getVisibility() == View.VISIBLE) {
                setFullscreen(false);
            } else {
                revealFullscreenActions();
            }
        } else if (service != null && service.isReceiving()) {
            service.stopWatching();
        } else if (service != null && service.isConnecting()) {
            cancelPairing();
        } else {
            super.onBackPressed();
        }
    }
}
