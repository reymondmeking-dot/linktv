package lan.linktv;

import android.content.Context;
import android.content.Intent;

interface ScreenCapture {
    interface Listener {
        void onEncoded(byte[] jpeg);
        boolean onAvcFrame(long ptsUs, boolean keyFrame, byte[] accessUnit);
        void onEnded(ScreenCapture capture, String reason);
    }

    void start(Context context, int resultCode, Intent permission);
    void enableFrames();
    void updateSize(Context context);
    void stop();
}
