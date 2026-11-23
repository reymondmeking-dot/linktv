package lan.linktv;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.BitmapDrawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.media.AudioManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
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
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.window.OnBackInvokedDispatcher;
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
    private static final int APP_AUDIO_REQUEST = 482;
    private static final String AUDIO_SETTINGS = "linktv_sender";
    private static final String INCLUDE_APP_AUDIO = "include_app_audio";
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
                service.setPlaybackVisible(resumed);
            }
            if (pendingPermission != null) {
                Intent authorized = pendingPermission;
                pendingPermission = null;
                service.startCapture(pendingResultCode, authorized, pendingCaptureAudio);
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
    private LinearLayout playbackRow;
    private Button mutePlaybackButton;
    private WindowInsets appliedInsets;
    private boolean backCallbackRegistered;
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
    private boolean resumed;
    private boolean bound;
    private boolean fullscreen;
    private int revealKeyCode = KeyEvent.KEYCODE_UNKNOWN;
    private int revealKeyDevice;
    private long revealKeyDownTime = -1;
    private boolean requestingCapture;
    private int pendingResultCode;
    private Intent pendingPermission;
    private boolean pendingCaptureAudio;
    private boolean includeAppAudio;
    private LinkService service;

    @Override
    protected void onCreate(Bundle previous) {
        super.onCreate(previous);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setVolumeControlStream(AudioManager.STREAM_MUSIC);
        includeAppAudio = hasAppAudioPermission()
                && getSharedPreferences(AUDIO_SETTINGS, MODE_PRIVATE).getBoolean(INCLUDE_APP_AUDIO, false);
        if (Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
        }
        buildLayout();
        updateSystemBars();
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
        resumed = true;
        if (service != null) {
            service.setPlaybackVisible(true);
        }
        if (fullscreen) {
            hideFullscreenActions();
        }
    }

    @Override
    protected void onPause() {
        resumed = false;
        if (service != null) {
            service.setPlaybackVisible(false);
        }
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
        setBackCallback(false);
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
        boolean wasCompact = compact;
        compact = configuration.screenWidthDp < 650;
        if (compact != wasCompact) {
            arrangeCastingActions();
            status.setMaxWidth(dp(compact ? 128 : 365));
        }
        if (fullscreen) {
            hideFullscreenActions();
        }
        appliedInsets = null;
        applyScreenPadding(fullscreen);
        root.requestApplyInsets();
        updateSystemBars();
        onStateChanged();
    }

    private void buildLayout() {
        Configuration configuration = getResources().getConfiguration();
        compact = configuration.screenWidthDp < 650;
        television = (configuration.uiMode & Configuration.UI_MODE_TYPE_MASK)
                == Configuration.UI_MODE_TYPE_TELEVISION
                || getPackageManager().hasSystemFeature("android.software.leanback");
        root = column();
        applyScreenPadding(false);
        if (Build.VERSION.SDK_INT >= 30) {
            root.setOnApplyWindowInsetsListener((view, insets) -> {
                appliedInsets = insets;
                applyScreenPadding(fullscreen);
                return insets;
            });
        }
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
            int markSize = dp(television ? 52 : 34);
            LinearLayout.LayoutParams markLayout = new LinearLayout.LayoutParams(markSize, markSize);
            markLayout.rightMargin = dp(10);
            header.addView(mark, markLayout);
        }
        TextView title = text("双向投屏", television ? 28 : 23, FOREGROUND);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title, new LinearLayout.LayoutParams(0, dp(46), 1));
        status = text("正在搜索附近设备…", 15, RASPBERRY);
        status.setGravity(Gravity.CENTER);
        status.setPadding(dp(14), 0, dp(14), 0);
        status.setBackground(box(LAVENDER, BORDER, dp(22)));
        status.setMaxWidth(dp(compact ? 128 : 365));
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
        addWeightedButton(choices, confirmButton, true);
        addWeightedButton(choices, cancelButton, false);
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
        actionBar = new LinearLayout(this);
        arrangeCastingActions();
        LinearLayout.LayoutParams actions = wide();
        actions.bottomMargin = dp(10);
        connected.addView(actionBar, actions);
        playbackRow = new LinearLayout(this);
        playbackRow.setGravity(Gravity.END);
        mutePlaybackButton = button("静音", false);
        mutePlaybackButton.setOnClickListener(view -> {
            if (service != null && service.hasPlaybackAudio()) {
                service.togglePlaybackMute();
            } else {
                toggleAppAudio();
            }
        });
        addButton(playbackRow, mutePlaybackButton, 154);
        playbackRow.setVisibility(View.GONE);
        LinearLayout.LayoutParams audio = wide();
        audio.bottomMargin = dp(8);
        connected.addView(playbackRow, audio);

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

    private void arrangeCastingActions() {
        View focused = actionBar.findFocus();
        detachControl(primaryAction);
        detachControl(stopAction);
        detachControl(fullscreenButton);
        detachControl(disconnectButton);
        detachControl(peerName);
        actionBar.removeAllViews();
        actionBar.setOrientation(compact ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
        actionBar.setGravity(Gravity.CENTER_VERTICAL);
        if (compact) {
            peerName.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            actionBar.addView(peerName, wide());
            addCompactRow(primaryAction, stopAction);
            addCompactRow(fullscreenButton, disconnectButton);
        } else {
            addButton(actionBar, primaryAction, 128);
            addButton(actionBar, stopAction, 136);
            addButton(actionBar, fullscreenButton, 128);
            addButton(actionBar, disconnectButton, 122);
            peerName.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
            actionBar.addView(peerName, new LinearLayout.LayoutParams(0, dp(48), 1));
        }
        if (focused != null) {
            focused.requestFocus();
        }
    }

    private static void detachControl(View control) {
        if (control.getParent() instanceof ViewGroup) {
            ((ViewGroup) control.getParent()).removeView(control);
        }
    }

    private void addCompactRow(Button first, Button second) {
        LinearLayout row = new LinearLayout(this);
        addWeightedButton(row, first, true);
        addWeightedButton(row, second, false);
        LinearLayout.LayoutParams line = wide();
        line.topMargin = dp(6);
        actionBar.addView(row, line);
    }

    private void addWeightedButton(LinearLayout row, Button button, boolean withGap) {
        LinearLayout.LayoutParams child = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        if (withGap) {
            child.setMarginEnd(dp(8));
        }
        row.addView(button, child);
    }

    private void applyScreenPadding(boolean full) {
        int horizontal = full ? 0 : dp(compact ? 16 : 34);
        int vertical = full ? 0 : dp(compact ? 12 : 22);
        if (Build.VERSION.SDK_INT >= 30 && appliedInsets != null) {
            int types = WindowInsets.Type.displayCutout();
            if (!full && !television) {
                types |= WindowInsets.Type.systemBars();
            }
            Insets safe = appliedInsets.getInsets(types);
            root.setPadding(horizontal + safe.left, vertical + safe.top,
                    horizontal + safe.right, vertical + safe.bottom);
        } else {
            root.setPadding(horizontal, vertical, horizontal, vertical);
        }
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
            service.startCapture(result, data, includeAppAudio && hasAppAudioPermission());
        } else if (result == RESULT_OK && data != null) {
            pendingResultCode = result;
            pendingPermission = data;
            pendingCaptureAudio = includeAppAudio && hasAppAudioPermission();
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
        setBackCallback(fullscreen || receiving || pairingActive);
        updatePlaybackControls();
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

    private boolean hasAppAudioPermission() {
        return Build.VERSION.SDK_INT >= 29
                && checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    private void toggleAppAudio() {
        if (Build.VERSION.SDK_INT < 29 || service == null || !service.isConnected()
                || service.isReceiving() || service.isStoppingWatching() || service.isCapturing()
                || requestingCapture) {
            return;
        }
        if (hasAppAudioPermission()) {
            setIncludeAppAudio(!includeAppAudio);
        } else {
            new AlertDialog.Builder(this)
                    .setTitle("发送应用声音")
                    .setMessage("安卓的录音权限用于采集允许共享的应用播放声音。")
                    .setNegativeButton("取消", null)
                    .setPositiveButton("继续", (dialog, which) -> requestPermissions(
                            new String[]{Manifest.permission.RECORD_AUDIO}, APP_AUDIO_REQUEST))
                    .show();
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode == APP_AUDIO_REQUEST && results.length > 0) {
            setIncludeAppAudio(results[0] == PackageManager.PERMISSION_GRANTED);
        }
    }

    private void setIncludeAppAudio(boolean include) {
        includeAppAudio = include;
        getSharedPreferences(AUDIO_SETTINGS, MODE_PRIVATE).edit()
                .putBoolean(INCLUDE_APP_AUDIO, include).apply();
        updatePlaybackControls();
    }

    private void updatePlaybackControls() {
        boolean received = service != null && service.hasPlaybackAudio();
        boolean canChooseOutgoing = Build.VERSION.SDK_INT >= 29 && service != null
                && service.isConnected() && !service.isReceiving() && !service.isStoppingWatching()
                && !service.isCapturing() && !requestingCapture;
        boolean available = !fullscreen && (received || canChooseOutgoing);
        playbackRow.setVisibility(available ? View.VISIBLE : View.GONE);
        disconnectButton.setNextFocusDownId(available ? mutePlaybackButton.getId() : View.NO_ID);
        mutePlaybackButton.setNextFocusUpId(disconnectButton.getId());
        if (received) {
            mutePlaybackButton.setText(service.isPlaybackMuted() ? "取消静音" : "静音");
        } else if (canChooseOutgoing) {
            mutePlaybackButton.setText(includeAppAudio && hasAppAudioPermission()
                    ? "发送应用声音  开" : "发送应用声音  关");
        }
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
        updatePlaybackControls();
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
        Window window = getWindow();
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController bars = window.getInsetsController();
            if (bars != null) {
                int mask = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                        | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
                bars.setSystemBarsAppearance(fullscreen || television ? 0 : mask, mask);
                bars.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                if (fullscreen || television) {
                    bars.hide(WindowInsets.Type.systemBars());
                } else {
                    bars.show(WindowInsets.Type.systemBars());
                }
            }
            root.requestApplyInsets();
            return;
        }
        if (television) {
            window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        }
        int flags = fullscreen ? View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY : 0;
        if (!fullscreen && !television && Build.VERSION.SDK_INT >= 23) {
            flags |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        }
        if (!fullscreen && !television && Build.VERSION.SDK_INT >= 26) {
            flags |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        }
        window.getDecorView().setSystemUiVisibility(flags);
    }

    private void setBackCallback(boolean active) {
        if (Build.VERSION.SDK_INT >= 33 && active != backCallbackRegistered) {
            if (active) {
                BackApi.register(this);
            } else {
                BackApi.unregister(this);
            }
            backCallbackRegistered = active;
        }
    }

    private Object backCallback;

    private static final class BackApi {
        static void register(MainActivity activity) {
            android.window.OnBackInvokedCallback callback = activity::onBackPressed;
            activity.backCallback = callback;
            activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback);
        }

        static void unregister(MainActivity activity) {
            activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(
                    (android.window.OnBackInvokedCallback) activity.backCallback);
            activity.backCallback = null;
        }
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
