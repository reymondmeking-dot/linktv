package lan.linktv;

import android.content.Context;
import android.content.Intent;
import android.media.projection.MediaProjection;

interface ScreenCapture {
    interface Listener {
        void onEncoded(byte[] jpeg);
        boolean onAvcFrame(long ptsUs, boolean keyFrame, byte[] accessUnit);
        void onEnded(ScreenCapture capture, String reason);
    }

    void start(Context context, int resultCode, Intent permission);
    MediaProjection mediaProjection();
    void enableFrames();
    void updateSize(Context context);
    void stop();
}
