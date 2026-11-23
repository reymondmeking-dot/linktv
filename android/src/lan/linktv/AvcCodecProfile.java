package lan.linktv;

import android.content.Context;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;
import java.util.Locale;

final class AvcCodecProfile {
    static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;
    private static final String SONY_DECODER = "OMX.MTK.VIDEO.DECODER.AVC";
    private static final int[][] TARGETS = {
            {1920, 1080, 60}, {1920, 1080, 30},
            {1280, 720, 60}, {1280, 720, 30},
            {960, 540, 30}, {640, 360, 30}
    };

    static final class EncoderMode {
        final String name;
        final int width;
        final int height;
        final int fps;
        final int bitrate;
        final int densityDpi;
        final boolean baseline;

        EncoderMode(String name, int width, int height, int fps, int bitrate, int densityDpi, boolean baseline) {
            this.name = name;
            this.width = width;
            this.height = height;
            this.fps = fps;
            this.bitrate = bitrate;
            this.densityDpi = densityDpi;
            this.baseline = baseline;
        }
    }

    private AvcCodecProfile() {}

    static EncoderMode encoder(Context context) {
        DisplayMetrics metrics = new DisplayMetrics();
        WindowManager manager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        if (manager == null) {
            return null;
        }
        manager.getDefaultDisplay().getRealMetrics(metrics);
        if (metrics.widthPixels < 2 || metrics.heightPixels < 2) {
            return null;
        }

        MediaCodecInfo[] codecs = codecInfos();
        boolean landscape = metrics.widthPixels >= metrics.heightPixels;
        for (int[] target : TARGETS) {
            int maxWidth = landscape ? target[0] : target[1];
            int maxHeight = landscape ? target[1] : target[0];
            double scale = Math.min(1.0, Math.min((double) maxWidth / metrics.widthPixels,
                    (double) maxHeight / metrics.heightPixels));
            for (MediaCodecInfo codec : codecs) {
                if (!codec.isEncoder() || !hardware(codec)) {
                    continue;
                }
                try {
                    MediaCodecInfo.CodecCapabilities caps = codec.getCapabilitiesForType(MIME);
                    if (!supportsSurface(caps)) {
                        continue;
                    }
                    MediaCodecInfo.VideoCapabilities video = caps.getVideoCapabilities();
                    int fittedWidth = aligned(metrics.widthPixels * scale, video.getWidthAlignment());
                    int fittedHeight = aligned(metrics.heightPixels * scale, video.getHeightAlignment());
                    int fps = target[2];
                    if (!video.areSizeAndRateSupported(fittedWidth, fittedHeight, fps)) {
                        continue;
                    }
                    boolean baseline = supportsBaseline(caps);
                    if (!baseline && Build.VERSION.SDK_INT < 29) {
                        continue;
                    }
                    int desiredBits = (int) Math.min(12_000_000L, Math.max(1_500_000L,
                            (long) fittedWidth * fittedHeight * fps / 10));
                    return new EncoderMode(codec.getName(), fittedWidth, fittedHeight, fps,
                            video.getBitrateRange().clamp(desiredBits), metrics.densityDpi, baseline);
                } catch (IllegalArgumentException ignored) {
                    // The codec does not support AVC or the aligned display dimensions.
                }
            }
        }
        return null;
    }

    static String decoder(int width, int height, int fps) {
        String selected = null;
        for (MediaCodecInfo codec : codecInfos()) {
            if (codec.isEncoder() || !hardware(codec)) {
                continue;
            }
            try {
                MediaCodecInfo.VideoCapabilities video = codec.getCapabilitiesForType(MIME).getVideoCapabilities();
                boolean supports = video.areSizeAndRateSupported(width, height, fps);
                // The actual Sony decode run verified this mode, despite older codec reports.
                boolean provenSony = SONY_DECODER.equals(codec.getName())
                        && width == 1920 && height == 1080 && fps <= 60;
                if (!supports && !provenSony) {
                    continue;
                }
                if (SONY_DECODER.equals(codec.getName())) {
                    return codec.getName();
                }
                if (selected == null) {
                    selected = codec.getName();
                }
            } catch (IllegalArgumentException ignored) {
                // Some hardware codecs only handle other formats.
            }
        }
        return selected;
    }

    private static MediaCodecInfo[] codecInfos() {
        try {
            return new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos();
        } catch (RuntimeException error) {
            Log.w(LinkService.TAG, "AVC codec discovery unavailable: " + error.getClass().getSimpleName());
            return new MediaCodecInfo[0];
        }
    }

    private static boolean hardware(MediaCodecInfo codec) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codec.isHardwareAccelerated();
        }
        String name = codec.getName().toLowerCase(Locale.ROOT);
        return !name.startsWith("omx.google.") && !name.startsWith("c2.android.")
                && !name.startsWith("omx.ffmpeg.") && !name.contains("software") && !name.contains(".sw.");
    }

    private static boolean supportsSurface(MediaCodecInfo.CodecCapabilities caps) {
        for (int format : caps.colorFormats) {
            if (format == MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface) {
                return true;
            }
        }
        return false;
    }

    private static boolean supportsBaseline(MediaCodecInfo.CodecCapabilities caps) {
        for (MediaCodecInfo.CodecProfileLevel profile : caps.profileLevels) {
            if (profile.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline) {
                return true;
            }
        }
        return false;
    }

    private static int aligned(double value, int alignment) {
        int step = Math.max(2, alignment);
        return (int) value / step * step;
    }
}
