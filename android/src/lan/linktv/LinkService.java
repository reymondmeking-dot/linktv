package lan.linktv;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Binder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;
import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

public final class LinkService extends Service {
    static final String TAG = "LinkTV";
    private static final String IDLE = "正在搜索附近设备…";
    private static final String READY = "已连接，可以开始投屏";
    private static final String STOP_CAPTURE = "lan.linktv.STOP_CAPTURE";
    private static final String CHANNEL_ID = "linktv_capture";
    private static final int NOTIFICATION_ID = 17863;
    private static final int MAX_DECODE_PIXELS = 4096 * 2160;
    private static final int STOP_ACK_TIMEOUT_MS = 4000;

    interface Listener {
        void onStateChanged();
        void onFrame(Bitmap frame);
    }

    final class LocalBinder extends Binder {
        LinkService getService() {
            return LinkService.this;
        }
    }

    private static final class RemoteFeed {
        int frameCount;
        private PcmPlayback audio;
        volatile boolean stopRequested;
        volatile AvcReceiver avc;
        boolean avcPicture;

        void playAudio(byte[] pcm) {
            if (audio == null) {
                audio = new PcmPlayback();
            }
            audio.offer(pcm);
        }

        void stopAudio() {
            if (audio != null) {
                audio.close();
                audio = null;
            }
        }

        void stopMedia() {
            stopAudio();
            if (avc != null) {
                avc.close();
                avc = null;
            }
            avcPicture = false;
        }

        void useSurface(Surface surface) {
            if (avc != null) {
                avc.setSurface(surface);
                if (surface == null) {
                    avcPicture = false;
                }
            }
        }
    }

    private static final class EncodedFrame {
        final RemoteFeed feed;
        final byte[] jpeg;

        EncodedFrame(RemoteFeed feed, byte[] jpeg) {
            this.feed = feed;
            this.jpeg = jpeg;
        }
    }

    private static final class DecodedFrame {
        final RemoteFeed feed;
        final Bitmap bitmap;

        DecodedFrame(RemoteFeed feed, Bitmap bitmap) {
            this.feed = feed;
            this.bitmap = bitmap;
        }
    }

    private final class CaptureListener implements ScreenCapture.Listener {
        private final LinkClient source;
        private boolean enabled;
        private boolean announced;

        CaptureListener(LinkClient source) {
            this.source = source;
        }

        @Override
        public synchronized void onEncoded(byte[] jpeg) {
            if (enabled) {
                source.jpeg(jpeg);
            }
        }

        @Override
        public synchronized boolean onAvcFrame(long ptsUs, boolean keyFrame, byte[] unit) {
            return !enabled || source.sendH264Frame(ptsUs, keyFrame, unit);
        }

        @Override
        public void onEnded(ScreenCapture ended, String reason) {
            main.post(() -> {
                if (capture == ended) {
                    stopCapture(reason, true);
                }
            });
        }

        synchronized boolean start(ScreenCapture selected) {
            source.control(LinkClient.START);
            announced = true;
            if (selected instanceof AvcProjectionCapture) {
                AvcCodecProfile.EncoderMode mode = ((AvcProjectionCapture) selected).mode;
                if (!source.sendH264Begin(mode.width, mode.height, mode.fps)) {
                    return false;
                }
            }
            enabled = true;
            return true;
        }

        synchronized void stop(boolean notifyPeer) {
            enabled = false;
            if (notifyPeer && announced) {
                source.control(LinkClient.STOP);
            }
        }
    }

    private final LocalBinder binder = new LocalBinder();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Object incomingLock = new Object();
    private final ExecutorService decoder = Executors.newSingleThreadExecutor(task -> new Thread(task, "LinkTV decoder"));
    private final AtomicReference<EncodedFrame> pendingJpeg = new AtomicReference<>();
    private final AtomicReference<DecodedFrame> pendingDisplay = new AtomicReference<>();
    private final AtomicBoolean decoding = new AtomicBoolean();
    private final AtomicBoolean displaying = new AtomicBoolean();
    private volatile LinkClient client;
    private volatile RemoteFeed remoteFeed;
    private volatile boolean destroyed;
    private Listener listener;
    private ScreenCapture capture;
    private CaptureListener captureListener;
    private Bitmap lastFrame;
    private boolean connected;
    private boolean foreground;
    private boolean viewerVisible;
    private Surface viewerSurface;
    private int videoCapabilities;
    private String status = IDLE;
    private String localName = "Android TV";
    private String peerName = "设备";
    private String matchNumber;
    private boolean awaitingPeerConfirmation;
    private boolean cancellingPairing;
    private int pairAttempt;
    private List<Discovery.Peer> peers = Collections.emptyList();
    private Discovery discovery;

    @Override
    public void onCreate() {
        super.onCreate();
        localName = Discovery.deviceName(this);
        if ("Android TV".equals(localName) && !getPackageManager().hasSystemFeature("android.software.leanback")) {
            localName = "Android 设备";
        }
        if (AvcCodecProfile.encoder(this) != null) {
            videoCapabilities |= LinkClient.H264_SEND;
        }
        if (AvcCodecProfile.decoder(1920, 1080, 60) != null) {
            videoCapabilities |= LinkClient.H264_RECEIVE;
        }
        discovery = new Discovery(this, localName, new Discovery.Listener() {
            @Override
            public void onPeersChanged(List<Discovery.Peer> snapshot) {
                main.post(() -> {
                    if (!destroyed) {
                        peers = snapshot;
                        Log.i(TAG, "Nearby PCs: " + snapshot.size());
                        notifyState();
                    }
                });
            }

            @Override
            public void onInvitation(Discovery.Peer peer) {
                main.post(() -> {
                    if (!destroyed && client == null) {
                        Log.i(TAG, "PC invitation received");
                        connect(peer);
                    }
                });
            }
        });
        discovery.start();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return binder;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP_CAPTURE.equals(intent.getAction())) {
            stopCapture("已停止投屏", true);
        }
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        destroyed = true;
        discovery.close();
        disconnect(IDLE);
        decoder.shutdownNow();
        super.onDestroy();
    }

    @Override
    public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        if (capture != null) {
            capture.updateSize(this);
        }
    }

    void setListener(Listener listener) {
        this.listener = listener;
        synchronized (incomingLock) {
            viewerVisible = true;
            if (remoteFeed != null) {
                remoteFeed.useSurface(viewerSurface);
            }
        }
        listener.onStateChanged();
        listener.onFrame(lastFrame);
    }

    void clearListener(Listener expected) {
        if (listener == expected) {
            listener = null;
            synchronized (incomingLock) {
                viewerVisible = false;
                if (remoteFeed != null) {
                    remoteFeed.stopAudio();
                    remoteFeed.useSurface(null);
                }
            }
        }
    }

    boolean isConnected() {
        return connected;
    }

    boolean isConnecting() {
        return client != null && !connected;
    }

    boolean isCapturing() {
        return capture != null;
    }

    boolean isReceiving() {
        return incomingStream() != null;
    }

    Object incomingStream() {
        RemoteFeed feed = remoteFeed;
        return feed != null && !feed.stopRequested ? feed : null;
    }

    AvcReceiver displayedVideo() {
        RemoteFeed feed = remoteFeed;
        return feed != null && !feed.stopRequested ? feed.avc : null;
    }

    boolean hasAvcPicture() {
        RemoteFeed feed = remoteFeed;
        return feed != null && !feed.stopRequested && feed.avcPicture;
    }

    void attachVideoSurface(Surface surface) {
        synchronized (incomingLock) {
            viewerSurface = surface;
            if (viewerVisible && remoteFeed != null && !remoteFeed.stopRequested) {
                remoteFeed.useSurface(surface);
            }
        }
    }

    void detachVideoSurface(Surface surface) {
        synchronized (incomingLock) {
            if (viewerSurface != surface) {
                return;
            }
            viewerSurface = null;
            if (remoteFeed != null) {
                remoteFeed.useSurface(null);
            }
        }
    }

    boolean isStoppingWatching() {
        RemoteFeed feed = remoteFeed;
        return feed != null && feed.stopRequested;
    }

    void stopWatching() {
        LinkClient source;
        RemoteFeed feed;
        synchronized (incomingLock) {
            source = client;
            feed = remoteFeed;
            if (source == null || feed == null || feed.stopRequested) {
                return;
            }
            feed.stopRequested = true;
            feed.stopMedia();
        }
        clearReceivedFrame();
        if (!source.requestRemoteStop()) {
            disconnect("已停止观看");
            return;
        }
        updateStatus("正在停止观看…");
        main.postDelayed(() -> {
            if (source == client && feed == remoteFeed) {
                disconnect("已停止观看");
            }
        }, STOP_ACK_TIMEOUT_MS);
    }

    String status() {
        return IDLE.equals(status) && !peers.isEmpty() ? "选择附近设备" : status;
    }

    String localName() {
        return localName;
    }

    String peerName() {
        return peerName;
    }

    String matchNumber() {
        return matchNumber;
    }

    boolean isWaitingForPeerConfirmation() {
        return awaitingPeerConfirmation;
    }

    boolean isCancellingPairing() {
        return cancellingPairing;
    }

    List<Discovery.Peer> nearbyPeers() {
        return peers;
    }

    void scan() {
        discovery.scan();
        if (client == null) {
            updateStatus(IDLE);
        }
    }

    void connect(Discovery.Peer peer) {
        if (destroyed || client != null) {
            return;
        }
        int attempt = ++pairAttempt;
        peerName = peer.name;
        LinkClient next = LinkClient.connect(peer.address, LinkClient.PORT, localName, videoCapabilities,
                new LinkClient.Listener() {
                    @Override
                    public void onPairingRequested(String name, String code) {
                        main.post(() -> {
                            if (attempt == pairAttempt && client != null && !cancellingPairing) {
                                peerName = name;
                                matchNumber = code;
                                Log.i(TAG, "Waiting for both pairing confirmations");
                                updateStatus("请核对配对数字");
                            }
                        });
                    }

                    @Override
                    public void onConnected(LinkClient source) {
                        LinkService.this.onConnected(source);
                    }

                    @Override
                    public void onClosed(LinkClient source, String reason) {
                        LinkService.this.onClosed(source, reason);
                    }

                    @Override
                    public void onMessage(LinkClient source, int type, byte[] payload) {
                        LinkService.this.onMessage(source, type, payload);
                    }
                });
        synchronized (incomingLock) {
            client = next;
        }
        updateStatus("正在连接设备…");
    }

    void confirmPairing() {
        LinkClient source = client;
        if (!connected && !cancellingPairing && source != null && matchNumber != null
                && source.respondToPairing(true)) {
            awaitingPeerConfirmation = true;
            updateStatus("等待对方确认");
        }
    }

    void cancelPairing() {
        LinkClient source = client;
        if (connected || source == null || cancellingPairing) {
            return;
        }
        if (source.respondToPairing(false)) {
            cancellingPairing = true;
            matchNumber = null;
            updateStatus("正在取消配对…");
        } else {
            disconnect("已取消配对");
        }
    }

    void disconnect() {
        disconnect("已断开连接");
    }

    private void disconnect(String message) {
        LinkClient source;
        synchronized (incomingLock) {
            source = client;
            client = null;
            if (remoteFeed != null) {
                remoteFeed.stopMedia();
                remoteFeed = null;
            }
        }
        connected = false;
        pairAttempt++;
        peerName = "设备";
        matchNumber = null;
        awaitingPeerConfirmation = false;
        cancellingPairing = false;
        stopCapture(null, false);
        if (source != null) {
            source.close();
        }
        clearReceivedFrame();
        updateStatus(message);
    }

    void startCapture(int resultCode, Intent permission) {
        if (resultCode != Activity.RESULT_OK || permission == null) {
            updateStatus("未授权录制本机画面");
            return;
        }
        LinkClient source = client;
        if (!connected || source == null || remoteFeed != null || capture != null) {
            return;
        }
        CaptureListener callbacks = new CaptureListener(source);
        ScreenCapture next = source.canSendH264() ? AvcProjectionCapture.prepare(this, callbacks) : null;
        if (next == null) {
            next = new ProjectionCapture(callbacks);
        }
        capture = next;
        captureListener = callbacks;
        try {
            startService(new Intent(this, LinkService.class));
            enterForeground();
            next.start(this, resultCode, permission);
            if (!callbacks.start(next)) {
                throw new IllegalStateException("Unable to announce AVC stream");
            }
            next.enableFrames();
            Log.i(TAG, "TV casting started");
            updateStatus("正在把本机画面投到对方设备");
        } catch (RuntimeException error) {
            Log.w(TAG, "Unable to start capture: " + error.getClass().getSimpleName());
            stopCapture("无法开启录屏，请确认系统支持录屏", true);
        }
    }

    void stopCapture() {
        stopCapture("已停止投屏", true);
    }

    private void stopCapture(String message, boolean notifyPeer) {
        ScreenCapture previous = capture;
        CaptureListener previousListener = captureListener;
        capture = null;
        captureListener = null;
        if (previous != null && previousListener != null) {
            previousListener.stop(notifyPeer);
            previous.stop();
            stopForeground(true);
            foreground = false;
            stopSelf();
        }
        if (message != null) {
            updateStatus(message);
        }
    }

    private void onConnected(LinkClient source) {
        main.post(() -> {
            if (source == client) {
                connected = true;
                matchNumber = null;
                awaitingPeerConfirmation = false;
                Log.i(TAG, "PC session authenticated");
                updateStatus(READY);
            }
        });
    }

    private void onClosed(LinkClient source, String reason) {
        main.post(() -> {
            if (source == client) {
                Log.i(TAG, "PC session closed");
                if (isStoppingWatching()) {
                    disconnect("已停止观看");
                } else {
                    disconnect(cancellingPairing ? "已取消配对" : reason == null ? IDLE : reason);
                }
            }
        });
    }

    private void onMessage(LinkClient source, int type, byte[] payload) {
        if (type == LinkClient.H264_BEGIN) {
            startAvc(source, payload);
            return;
        }
        if (type == LinkClient.H264_FRAME) {
            offerAvc(source, payload);
            return;
        }
        RemoteFeed feed;
        synchronized (incomingLock) {
            if (destroyed || source != client) {
                return;
            }
            switch (type) {
                case LinkClient.START:
                    if (remoteFeed != null) {
                        return;
                    }
                    feed = new RemoteFeed();
                    remoteFeed = feed;
                    break;
                case LinkClient.STOP:
                    feed = remoteFeed;
                    if (feed == null) {
                        return;
                    }
                    feed.stopMedia();
                    remoteFeed = null;
                    break;
                case LinkClient.AUDIO:
                    if (remoteFeed != null && !remoteFeed.stopRequested && viewerVisible) {
                        remoteFeed.playAudio(payload);
                    }
                    return;
                case LinkClient.JPEG:
                    feed = remoteFeed;
                    if (feed != null && !feed.stopRequested) {
                        pendingJpeg.set(new EncodedFrame(feed, payload));
                        startDecode();
                    }
                    return;
                default:
                    return;
            }
        }
        if (type == LinkClient.START) {
            main.post(() -> {
                if (source == client && acceptsFrame(feed)) {
                    stopCapture(null, true);
                    clearDisplay();
                    Log.i(TAG, "PC stream started");
                    updateStatus("正在接收投屏画面");
                }
            });
        } else {
            main.post(() -> {
                if (source == client && remoteFeed == null) {
                    clearReceivedFrame();
                    Log.i(TAG, "PC stream stopped");
                    if (feed.stopRequested) {
                        updateStatus("已停止观看");
                    } else {
                        updateStatus(capture != null ? "正在把本机画面投到对方设备" : READY);
                    }
                }
            });
        }
    }

    private void startAvc(LinkClient source, byte[] declaration) {
        RemoteFeed feed = remoteFeed;
        if (source != client || feed == null || !acceptsFrame(feed) || declaration.length != 6) {
            return;
        }
        ByteBuffer format = ByteBuffer.wrap(declaration);
        int width = format.getShort() & 0xffff;
        int height = format.getShort() & 0xffff;
        int fps = format.getShort() & 0xffff;
        AvcReceiver receiver = new AvcReceiver(width, height, fps, new AvcReceiver.Listener() {
            @Override
            public void firstPicture(AvcReceiver video) {
                main.post(() -> {
                    synchronized (incomingLock) {
                        if (!acceptsFrame(feed) || feed.avc != video || viewerSurface == null || !viewerVisible) {
                            return;
                        }
                        feed.avcPicture = true;
                    }
                    notifyState();
                });
            }

            @Override
            public void failed(AvcReceiver video) {
                avcFailed(feed, video);
            }
        });
        synchronized (incomingLock) {
            if (!acceptsFrame(feed) || source != client) {
                receiver.close();
                return;
            }
            feed.avc = receiver;
            if (viewerVisible) {
                receiver.setSurface(viewerSurface);
            }
        }
        if (!receiver.supported()) {
            avcFailed(feed, receiver);
            return;
        }
        main.post(() -> {
            if (acceptsFrame(feed) && feed.avc == receiver) {
                clearDisplay();
                notifyState();
            }
        });
    }

    private void offerAvc(LinkClient source, byte[] payload) {
        RemoteFeed feed = remoteFeed;
        if (source != client || feed == null || !acceptsFrame(feed)) {
            return;
        }
        AvcReceiver video = feed.avc;
        if (video != null && !video.offerFrame(payload)) {
            avcFailed(feed, video);
        }
    }

    private void avcFailed(RemoteFeed feed, AvcReceiver video) {
        main.post(() -> {
            if (acceptsFrame(feed) && feed.avc == video) {
                videoCapabilities &= ~LinkClient.H264_RECEIVE;
                disconnect("无法播放 H.264，请重新连接以使用兼容模式");
            }
        });
    }

    private boolean acceptsFrame(RemoteFeed feed) {
        return feed == remoteFeed && !feed.stopRequested;
    }

    private void startDecode() {
        if (decoding.compareAndSet(false, true)) {
            decoder.execute(this::decode);
        }
    }

    private void decode() {
        try {
            EncodedFrame frame;
            while (!destroyed && (frame = pendingJpeg.getAndSet(null)) != null) {
                if (!acceptsFrame(frame.feed)) {
                    continue;
                }
                Bitmap bitmap = decodeJpeg(frame.jpeg);
                if (bitmap != null && acceptsFrame(frame.feed)) {
                    frame.feed.frameCount++;
                    if (frame.feed.frameCount == 1) {
                        Log.i(TAG, "PC first frame: " + bitmap.getWidth() + "x" + bitmap.getHeight());
                    }
                    if (frame.feed.frameCount == 1 || frame.feed.frameCount % 60 == 0) {
                        Log.i(TAG, "PC frames decoded: " + frame.feed.frameCount);
                    }
                    pendingDisplay.set(new DecodedFrame(frame.feed, bitmap));
                    if (displaying.compareAndSet(false, true)) {
                        main.post(this::showLatestFrame);
                    }
                }
            }
        } finally {
            decoding.set(false);
            synchronized (incomingLock) {
                if (!destroyed && pendingJpeg.get() != null) {
                    startDecode();
                }
            }
        }
    }

    private static Bitmap decodeJpeg(byte[] jpeg) {
        if (jpeg.length < 4 || (jpeg[0] & 0xff) != 0xff || (jpeg[1] & 0xff) != 0xd8) {
            return null;
        }
        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, options);
        if (options.outWidth <= 0 || options.outHeight <= 0
                || options.outWidth > 4096 || options.outHeight > 4096
                || (long) options.outWidth * options.outHeight > MAX_DECODE_PIXELS) {
            return null;
        }

        options.inJustDecodeBounds = false;
        options.inScaled = false;
        options.inPreferredConfig = Bitmap.Config.ARGB_8888;
        return BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, options);
    }

    private void showLatestFrame() {
        DecodedFrame frame = pendingDisplay.getAndSet(null);
        if (frame != null && acceptsFrame(frame.feed) && !destroyed) {
            lastFrame = frame.bitmap;
            if (listener != null) {
                listener.onFrame(lastFrame);
            }
        }
        displaying.set(false);
        if (pendingDisplay.get() != null && displaying.compareAndSet(false, true)) {
            main.post(this::showLatestFrame);
        }
    }

    private void clearReceivedFrame() {
        pendingJpeg.set(null);
        pendingDisplay.set(null);
        clearDisplay();
    }

    private void clearDisplay() {
        lastFrame = null;
        if (listener != null) {
            listener.onFrame(null);
        }
    }

    private void updateStatus(String message) {
        status = message;
        notifyState();
    }

    private void notifyState() {
        if (listener != null) {
            listener.onStateChanged();
        }
    }

    private void enterForeground() {
        if (foreground) {
            return;
        }
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= 26) {
            manager.createNotificationChannel(new NotificationChannel(CHANNEL_ID, "电视投屏",
                    NotificationManager.IMPORTANCE_LOW));
            builder = new Notification.Builder(this, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(this);
        }
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, LinkService.class)
                .setAction(STOP_CAPTURE), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notification = builder.setSmallIcon(R.drawable.linktv_notification)
                .setColor(0xffa23b70)
                .setContentTitle("正在共享本机画面")
                .setContentText("点击打开双向投屏")
                .setContentIntent(open)
                .setOngoing(true)
                .addAction(android.R.drawable.ic_menu_close_clear_cancel, "停止投屏", stop)
                .build();
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
        foreground = true;
    }
}
