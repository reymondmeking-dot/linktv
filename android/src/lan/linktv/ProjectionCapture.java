package lan.linktv;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;

final class ProjectionCapture implements ScreenCapture {

    private static final int MAX_LONG_SIDE = 1920;
    private static final int MAX_SHORT_SIDE = 1080;
    private static final int MAX_FRAME_RATE = 20;
    private static final long FRAME_INTERVAL_NANOS = 1_000_000_000L / MAX_FRAME_RATE;
    private static final int[] JPEG_QUALITIES = { 90, 80, 65 };

    private final Listener listener;
    private final HandlerThread thread = new HandlerThread("LinkTV capture");
    private final MediaProjection.Callback callback = new MediaProjection.Callback() {
        @Override
        public void onStop() {
            if (!closed) {
                listener.onEnded(ProjectionCapture.this, "系统已停止投屏");
            }
        }
    };
    private Handler handler;
    private MediaProjection projection;
    private VirtualDisplay display;
    private ImageReader reader;
    private Bitmap bitmap;
    private ByteBuffer packedPixels;
    private final ByteArrayOutputStream jpeg = new ByteArrayOutputStream(192 * 1024);
    private long nextFrameTimeNanos;
    private int frameCount;
    private volatile boolean enabled;
    private volatile boolean closed;

    ProjectionCapture(Listener listener) {
        this.listener = listener;
    }

    private static final class CaptureSize {
        final int width;
        final int height;
        final int density;

        CaptureSize(int width, int height, int density) {
            this.width = width;
            this.height = height;
            this.density = density;
        }
    }

    private static CaptureSize captureSize(Context context) {
        WindowManager windows = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        DisplayMetrics metrics = new DisplayMetrics();
        windows.getDefaultDisplay().getRealMetrics(metrics);
        boolean landscape = metrics.widthPixels >= metrics.heightPixels;
        int maxWidth = landscape ? MAX_LONG_SIDE : MAX_SHORT_SIDE;
        int maxHeight = landscape ? MAX_SHORT_SIDE : MAX_LONG_SIDE;
        double scale = Math.min(1.0, Math.min((double) maxWidth / metrics.widthPixels,
                (double) maxHeight / metrics.heightPixels));
        int width = Math.max(2, ((int) (metrics.widthPixels * scale)) & ~1);
        int height = Math.max(2, ((int) (metrics.heightPixels * scale)) & ~1);
        return new CaptureSize(width, height, metrics.densityDpi);
    }

    @Override
    public void start(Context context, int resultCode, Intent permission) {
        CaptureSize size = captureSize(context);
        thread.start();
        handler = new Handler(thread.getLooper());
        MediaProjectionManager manager = (MediaProjectionManager)
                context.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        projection = manager.getMediaProjection(resultCode, permission);
        if (projection == null) {
            throw new IllegalStateException("Screen capture permission unavailable");
        }
        projection.registerCallback(callback, handler);
        bitmap = Bitmap.createBitmap(size.width, size.height, Bitmap.Config.ARGB_8888);
        reader = ImageReader.newInstance(size.width, size.height, PixelFormat.RGBA_8888, 2);
        reader.setOnImageAvailableListener(this::onImage, handler);
        display = projection.createVirtualDisplay("LinkTV", size.width, size.height, size.density,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, reader.getSurface(), null, handler);
        if (display == null) {
            throw new IllegalStateException("Virtual display unavailable");
        }
        Log.i(LinkService.TAG, "Capture surface " + size.width + "x" + size.height);
        Log.i(LinkService.TAG, "Capture FPS cap: " + MAX_FRAME_RATE);
    }

    @Override
    public void updateSize(Context context) {
        CaptureSize size = captureSize(context);
        if (handler != null && !closed) {
            handler.post(() -> resize(size));
        }
    }

    private void resize(CaptureSize size) {
        if (closed || display == null || (bitmap.getWidth() == size.width
                && bitmap.getHeight() == size.height)) {
            return;
        }
        Bitmap nextBitmap = null;
        ImageReader nextReader = null;
        try {
            nextBitmap = Bitmap.createBitmap(size.width, size.height, Bitmap.Config.ARGB_8888);
            nextReader = ImageReader.newInstance(size.width, size.height, PixelFormat.RGBA_8888, 2);
            nextReader.setOnImageAvailableListener(this::onImage, handler);
            display.resize(size.width, size.height, size.density);
            display.setSurface(nextReader.getSurface());
        } catch (RuntimeException error) {
            if (nextReader != null) {
                nextReader.close();
            }
            if (nextBitmap != null) {
                nextBitmap.recycle();
            }
            Log.w(LinkService.TAG, "Capture resize failed: " + error.getClass().getSimpleName());
            listener.onEnded(this, "屏幕尺寸改变，请重新开始投屏");
            return;
        }
        reader.close();
        bitmap.recycle();
        reader = nextReader;
        bitmap = nextBitmap;
        packedPixels = null;
        Log.i(LinkService.TAG, "Capture surface " + size.width + "x" + size.height);
    }

    @Override
    public MediaProjection mediaProjection() {
        return projection;
    }

    @Override
    public void enableFrames() {
        enabled = true;
    }

    @Override
    public void stop() {
        if (closed) {
            return;
        }
        closed = true;
        if (handler != null) {
            handler.post(this::release);
        }
    }

    private void onImage(ImageReader source) {
        if (source != reader || closed) {
            return;
        }
        try {
            try (Image image = source.acquireLatestImage()) {
                long now = SystemClock.elapsedRealtimeNanos();
                if (image == null || !enabled || closed || now < nextFrameTimeNanos) {
                    return;
                }
                if (nextFrameTimeNanos == 0) {
                    nextFrameTimeNanos = now;
                }
                // Keep the capture cadence steady and skip periods lost to a slow encode.
                long periods = (now - nextFrameTimeNanos) / FRAME_INTERVAL_NANOS + 1;
                nextFrameTimeNanos += periods * FRAME_INTERVAL_NANOS;
                copyImage(image);
            }
            if (!closed) {
                encodeFrame();
            }
        } catch (RuntimeException error) {
            if (!closed) {
                enabled = false;
                Log.w(LinkService.TAG, "Capture failed: " + error.getClass().getSimpleName());
                listener.onEnded(this, "读取本机画面失败");
            }
        }
    }

    private void copyImage(Image image) {
        Image.Plane plane = image.getPlanes()[0];
        if (plane.getPixelStride() != 4) {
            throw new IllegalStateException("Unexpected capture pixel format");
        }

        ByteBuffer pixels = plane.getBuffer();
        int rowBytes = bitmap.getWidth() * 4;
        int rowStride = plane.getRowStride();
        if (rowBytes == rowStride) {
            pixels.rewind();
            bitmap.copyPixelsFromBuffer(pixels);
            return;
        }

        if (packedPixels == null) {
            packedPixels = ByteBuffer.allocateDirect(rowBytes * bitmap.getHeight());
        }
        packedPixels.clear();
        for (int row = 0; row < bitmap.getHeight(); row++) {
            int rowOffset = row * rowStride;
            pixels.limit(rowOffset + rowBytes);
            pixels.position(rowOffset);
            packedPixels.put(pixels);
        }
        packedPixels.flip();
        bitmap.copyPixelsFromBuffer(packedPixels);
    }

    private void encodeFrame() {
        for (int quality : JPEG_QUALITIES) {
            jpeg.reset();
            if (closed || !bitmap.compress(Bitmap.CompressFormat.JPEG, quality, jpeg)) {
                return;
            }
            if (jpeg.size() <= LinkClient.MAX_JPEG && !closed) {
                listener.onEncoded(jpeg.toByteArray());
                frameCount++;
                if (frameCount == 1 || frameCount % 60 == 0) {
                    Log.i(LinkService.TAG, "TV frames encoded: " + frameCount);
                }
                return;
            }
        }
    }

    private void release() {
        if (display != null) {
            display.release();
        }
        if (reader != null) {
            reader.close();
        }
        if (projection != null) {
            projection.unregisterCallback(callback);
            projection.stop();
        }
        if (bitmap != null) {
            bitmap.recycle();
        }
        Log.i(LinkService.TAG, "TV capture stopped, frames encoded: " + frameCount);
        thread.quitSafely();
    }
}
