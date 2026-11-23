package lan.linktv;

import android.content.Context;
import android.content.Intent;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;
import java.io.IOException;
import java.nio.ByteBuffer;

final class AvcProjectionCapture implements ScreenCapture {
    private static final String MAX_INPUT_FPS = "max-fps-to-encoder";
    final AvcCodecProfile.EncoderMode mode;

    private final Listener listener;
    private final MediaCodec encoder;
    private final Surface inputSurface;
    private final AvcAnnexB.ParameterSets parameters = new AvcAnnexB.ParameterSets();
    private final MediaProjection.Callback projectionStopped = new MediaProjection.Callback() {
        @Override
        public void onStop() {
            if (!closed) {
                listener.onEnded(AvcProjectionCapture.this, "系统已停止投屏");
            }
        }
    };
    private VirtualDisplay display;
    private MediaProjection projection;
    private Thread drain;
    private volatile boolean closed;

    private AvcProjectionCapture(AvcCodecProfile.EncoderMode mode, Listener listener,
                                 MediaCodec encoder, Surface inputSurface) {
        this.mode = mode;
        this.listener = listener;
        this.encoder = encoder;
        this.inputSurface = inputSurface;
    }

    static AvcProjectionCapture prepare(Context context, Listener listener) {
        MediaCodec encoder = null;
        Surface surface = null;
        try {
            AvcCodecProfile.EncoderMode mode = AvcCodecProfile.encoder(context);
            if (mode == null) {
                return null;
            }
            MediaFormat format = MediaFormat.createVideoFormat(AvcCodecProfile.MIME, mode.width, mode.height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            format.setInteger(MediaFormat.KEY_BIT_RATE, mode.bitrate);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, mode.fps);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
            format.setFloat(MAX_INPUT_FPS, mode.fps);
            if (Build.VERSION.SDK_INT >= 29) {
                format.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0);
            } else if (mode.baseline) {
                format.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline);
            }
            encoder = MediaCodec.createByCodecName(mode.name);
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            surface = encoder.createInputSurface();
            encoder.start();
            return new AvcProjectionCapture(mode, listener, encoder, surface);
        } catch (IOException | RuntimeException error) {
            Log.w(LinkService.TAG, "AVC capture unavailable: " + error.getClass().getSimpleName());
            if (encoder != null) {
                releaseCodec(encoder);
            }
            if (surface != null) {
                surface.release();
            }
            return null;
        }
    }

    @Override
    public void start(Context context, int resultCode, Intent permission) {
        MediaProjectionManager manager = (MediaProjectionManager)
                context.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        projection = manager.getMediaProjection(resultCode, permission);
        if (projection == null) {
            throw new IllegalStateException("Screen capture permission unavailable");
        }
        projection.registerCallback(projectionStopped, new Handler(Looper.getMainLooper()));
        display = projection.createVirtualDisplay("LinkTV AVC", mode.width, mode.height, mode.densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, inputSurface, null, null);
        if (display == null) {
            throw new IllegalStateException("Virtual display unavailable");
        }
        Log.i(LinkService.TAG, "H264 encoder " + mode.name + " " + mode.width + "x" + mode.height
                + " @" + mode.fps + ", bitrate " + mode.bitrate);
    }

    @Override
    public void enableFrames() {
        drain = new Thread(this::encode, "LinkTV AVC encoder");
        drain.start();
    }

    @Override
    public void updateSize(Context context) {
        // Keep the wire format fixed during one cast. The virtual display fits a rotated source
        // into the existing input surface, avoiding a second projection permission.
    }

    @Override
    public void stop() {
        if (closed) {
            return;
        }
        closed = true;
        if (drain == null) {
            release();
        } else {
            drain.interrupt();
        }
    }

    private void encode() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        long firstPts = -1;
        long lastPts = -1;
        int frames = 0;
        try {
            while (!closed) {
                int index = encoder.dequeueOutputBuffer(info, 10_000);
                if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    readCodecParameters(encoder.getOutputFormat());
                    continue;
                }
                if (index < 0) {
                    continue;
                }
                byte[] raw;
                try {
                    if (info.size <= 0) {
                        continue;
                    }
                    if (info.size > LinkClient.MAX_H264_ACCESS_UNIT) {
                        throw new IOException("AVC sample too large");
                    }
                    ByteBuffer encoded = encoder.getOutputBuffer(index);
                    if (encoded == null) {
                        throw new IOException("No AVC sample buffer");
                    }
                    encoded.position(info.offset);
                    encoded.limit(info.offset + info.size);
                    raw = new byte[info.size];
                    encoded.get(raw);
                } finally {
                    encoder.releaseOutputBuffer(index, false);
                }
                byte[] unit = AvcAnnexB.normalize(raw);
                boolean key = parameters.accept(unit, 0);
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    continue;
                }
                if (key && !parameters.ready()) {
                    continue;
                }
                if (firstPts < 0) {
                    if (!key) {
                        continue;
                    }
                    firstPts = info.presentationTimeUs;
                }
                long pts = info.presentationTimeUs - firstPts;
                if (pts <= lastPts) {
                    throw new IOException("AVC output has reordered timestamps");
                }
                if (key) {
                    unit = parameters.prependTo(unit);
                }
                if (!closed && !listener.onAvcFrame(pts, key, unit)) {
                    throw new IOException("AVC sending stopped");
                }
                lastPts = pts;
                frames++;
                if (frames == 1 || frames % 120 == 0) {
                    Log.i(LinkService.TAG, "H264 frames encoded: " + frames);
                }
            }
        } catch (IOException | RuntimeException error) {
            if (!closed) {
                Log.w(LinkService.TAG, "AVC encoder stopped: " + error.getClass().getSimpleName());
                listener.onEnded(this, "H.264 投屏已中断");
            }
        } finally {
            release();
        }
    }

    private void readCodecParameters(MediaFormat format) {
        String[] keys = {"csd-0", "csd-1"};
        for (String key : keys) {
            ByteBuffer set = format.getByteBuffer(key);
            if (set != null && set.hasRemaining()) {
                ByteBuffer input = set.duplicate();
                byte[] bytes = new byte[input.remaining()];
                input.get(bytes);
                parameters.accept(AvcAnnexB.normalize(bytes), 0);
            }
        }
    }

    private void release() {
        if (display != null) {
            display.release();
        }
        if (projection != null) {
            projection.unregisterCallback(projectionStopped);
            projection.stop();
        }
        releaseCodec(encoder);
        inputSurface.release();
    }

    private static void releaseCodec(MediaCodec codec) {
        try {
            codec.stop();
        } catch (IllegalStateException ignored) {
            // An encoder may have failed before it started.
        }
        try {
            codec.release();
        } catch (IllegalStateException ignored) {
            // The codec service may already have torn down a failed encoder.
        }
    }
}
