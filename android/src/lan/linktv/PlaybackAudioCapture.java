package lan.linktv;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.projection.MediaProjection;
import android.os.Process;
import android.util.Log;
import java.util.Arrays;

final class PlaybackAudioCapture implements AutoCloseable {
    private static final int SAMPLE_RATE = 44_100;
    private static final int CHANNELS = AudioFormat.CHANNEL_IN_STEREO;
    private static final int ENCODING = AudioFormat.ENCODING_PCM_16BIT;
    private static final int PACKET_BYTES = SAMPLE_RATE * 4 / 50;

    interface Listener {
        boolean onPcm(byte[] pcm);
        void unavailable();
    }

    private final Object lock = new Object();
    private final MediaProjection projection;
    private final Listener listener;
    private final Thread worker = new Thread(this::capture, "LinkTV app audio");
    private AudioRecord recording;
    private volatile boolean closed;

    PlaybackAudioCapture(MediaProjection projection, Listener listener) {
        this.projection = projection;
        this.listener = listener;
    }

    void start() {
        worker.start();
    }

    @Override
    public void close() {
        synchronized (lock) {
            closed = true;
            if (recording != null) {
                stopRecording(recording);
            }
        }
        worker.interrupt();
    }

    private void capture() {
        AudioRecord record = null;
        try {
            int minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNELS, ENCODING);
            if (minimum <= 0) {
                throw new IllegalStateException("Unsupported stereo PCM capture");
            }
            AudioFormat format = new AudioFormat.Builder()
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(CHANNELS)
                    .setEncoding(ENCODING)
                    .build();
            AudioPlaybackCaptureConfiguration playback = new AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .excludeUid(Process.myUid())
                    .build();
            record = new AudioRecord.Builder()
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(Math.max(minimum, 4 * PACKET_BYTES))
                    .setAudioPlaybackCaptureConfig(playback)
                    .build();
            if (record.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("AudioPlaybackCapture unavailable");
            }
            synchronized (lock) {
                if (closed) {
                    return;
                }
                recording = record;
                record.startRecording();
            }
            if (record.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) {
                throw new IllegalStateException("Unable to start playback capture");
            }
            Log.i(LinkService.TAG, "App playback audio capture started: 44100 Hz stereo PCM");

            byte[] pcm = new byte[PACKET_BYTES];
            int filled = 0;
            while (!closed) {
                int count = record.read(pcm, filled, pcm.length - filled, AudioRecord.READ_BLOCKING);
                if (count < 0 && !closed) {
                    throw new IllegalStateException("App playback read failed");
                }
                if (count <= 0) {
                    if (!closed) {
                        Thread.sleep(4);
                    }
                    continue;
                }
                filled += count;
                if (filled == pcm.length) {
                    if (closed || !listener.onPcm(Arrays.copyOf(pcm, pcm.length))) {
                        return;
                    }
                    filled = 0;
                }
            }
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        } catch (RuntimeException error) {
            if (!closed) {
                Log.w(LinkService.TAG, "App playback audio unavailable: " + error.getClass().getSimpleName());
                listener.unavailable();
            }
        } finally {
            if (record != null) {
                synchronized (lock) {
                    recording = null;
                    stopRecording(record);
                }
                record.release();
            }
        }
    }

    private static void stopRecording(AudioRecord record) {
        try {
            if (record.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                record.stop();
            }
        } catch (IllegalStateException ignored) {
            // A revoked projection can stop the recorder first.
        }
    }
}
