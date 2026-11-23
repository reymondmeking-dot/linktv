package lan.linktv;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.util.Log;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;

final class PcmPlayback implements AutoCloseable {
    private static final int SAMPLE_RATE = 44100;
    private static final int CHANNELS = AudioFormat.CHANNEL_OUT_STEREO;
    private static final int FORMAT = AudioFormat.ENCODING_PCM_16BIT;
    private static final int FRAME_BYTES = 4;
    private static final int MAX_PACKET_BYTES = 16384;
    private static final int MAX_QUEUE_BYTES = 32768;
    private static final int MIN_BUFFER_BYTES = 8192;

    private final Object lock = new Object();
    private final ArrayDeque<byte[]> queue = new ArrayDeque<>();
    private final Thread worker = new Thread(this::play, "LinkTV audio");
    private int queuedBytes;
    private volatile boolean closed;

    PcmPlayback() {
        worker.start();
    }

    void offer(byte[] pcm) {
        if (pcm.length < FRAME_BYTES || pcm.length > MAX_PACKET_BYTES || pcm.length % FRAME_BYTES != 0) {
            return;
        }
        synchronized (lock) {
            if (closed) {
                return;
            }
            while (queuedBytes + pcm.length > MAX_QUEUE_BYTES) {
                queuedBytes -= queue.removeFirst().length;
            }
            queue.addLast(pcm);
            queuedBytes += pcm.length;
            lock.notifyAll();
        }
    }

    @Override
    public void close() {
        synchronized (lock) {
            closed = true;
            queuedBytes = 0;
            queue.clear();
            lock.notifyAll();
        }
        worker.interrupt();
    }

    private byte[] nextPacket() throws InterruptedException {
        synchronized (lock) {
            while (!closed && queue.isEmpty()) {
                lock.wait();
            }
            if (closed) {
                return null;
            }
            byte[] pcm = queue.removeFirst();
            queuedBytes -= pcm.length;
            return pcm;
        }
    }

    private void play() {
        AudioTrack track = null;
        boolean started = false;
        try {
            byte[] packet = nextPacket();
            if (packet == null) {
                return;
            }
            int nativeMinimum = AudioTrack.getMinBufferSize(SAMPLE_RATE, CHANNELS, FORMAT);
            if (nativeMinimum <= 0) {
                throw new IllegalStateException("Unsupported PCM format");
            }
            AudioAttributes attributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                    .build();
            AudioFormat format = new AudioFormat.Builder().setSampleRate(SAMPLE_RATE)
                    .setChannelMask(CHANNELS).setEncoding(FORMAT).build();
            track = new AudioTrack(attributes, format, Math.max(MIN_BUFFER_BYTES, nativeMinimum),
                    AudioTrack.MODE_STREAM, AudioManager.AUDIO_SESSION_ID_GENERATE);
            if (track.getState() != AudioTrack.STATE_INITIALIZED || closed) {
                return;
            }
            track.play();
            started = true;
            Log.i(LinkService.TAG, "PC audio playback started: 44100 Hz stereo PCM");
            do {
                ByteBuffer buffer = ByteBuffer.wrap(packet);
                while (!closed && buffer.hasRemaining()) {
                    int written = track.write(buffer, buffer.remaining(), AudioTrack.WRITE_NON_BLOCKING);
                    if (written < 0) {
                        throw new IllegalStateException("AudioTrack write failed");
                    }
                    if (written == 0) {
                        Thread.sleep(4);
                    }
                }
                packet = nextPacket();
            } while (packet != null);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        } catch (RuntimeException error) {
            if (!closed) {
                Log.w(LinkService.TAG, "PC audio unavailable: " + error.getClass().getSimpleName());
            }
        } finally {
            if (track != null) {
                if (started) {
                    try {
                        track.pause();
                        track.flush();
                    } catch (IllegalStateException ignored) {
                        // Audio may already have been halted by the system.
                    }
                }
                track.release();
            }
            if (started) {
                Log.i(LinkService.TAG, "PC audio playback stopped");
            }
            synchronized (lock) {
                closed = true;
                queue.clear();
                queuedBytes = 0;
            }
        }
    }
}
