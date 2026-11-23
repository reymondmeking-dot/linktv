package lan.linktv;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
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
    private final AudioManager audioManager;
    private final AudioManager.OnAudioFocusChangeListener focusListener = this::onAudioFocusChanged;
    private final ArrayDeque<byte[]> queue = new ArrayDeque<>();
    private final Thread worker = new Thread(this::play, "LinkTV audio");
    private int queuedBytes;
    private int focusEpoch;
    private volatile boolean focused = true;
    private volatile boolean closed;

    PcmPlayback(Context context) {
        audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
        worker.start();
    }

    void offer(byte[] pcm) {
        if (pcm.length < FRAME_BYTES || pcm.length > MAX_PACKET_BYTES || pcm.length % FRAME_BYTES != 0) {
            return;
        }
        synchronized (lock) {
            if (closed || !focused) {
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
            clearQueue();
            lock.notifyAll();
        }
        worker.interrupt();
    }

    private void onAudioFocusChanged(int change) {
        synchronized (lock) {
            if (closed) {
                return;
            }
            focused = change == AudioManager.AUDIOFOCUS_GAIN;
            if (!focused) {
                focusEpoch++;
                clearQueue();
            }
            if (change == AudioManager.AUDIOFOCUS_LOSS) {
                closed = true;
            }
            lock.notifyAll();
        }
    }

    private void clearQueue() {
        queuedBytes = 0;
        queue.clear();
    }

    private byte[] nextPacket(int expectedEpoch) throws InterruptedException {
        synchronized (lock) {
            while (!closed && focusEpoch == expectedEpoch && (!focused || queue.isEmpty())) {
                lock.wait();
            }
            if (closed || focusEpoch != expectedEpoch) {
                return null;
            }
            byte[] pcm = queue.removeFirst();
            queuedBytes -= pcm.length;
            return pcm;
        }
    }

    private int epoch() {
        synchronized (lock) {
            return focusEpoch;
        }
    }

    private Object requestAudioFocus(AudioAttributes attributes) {
        if (audioManager == null) {
            return null;
        }
        if (Build.VERSION.SDK_INT >= 26) {
            return FocusApi.request(audioManager, attributes, focusListener);
        }
        int result = audioManager.requestAudioFocus(focusListener, AudioManager.STREAM_MUSIC,
                AudioManager.AUDIOFOCUS_GAIN);
        return result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED ? focusListener : null;
    }

    private void abandonAudioFocus(Object request) {
        if (request == null) {
            return;
        }
        if (Build.VERSION.SDK_INT >= 26) {
            FocusApi.abandon(audioManager, request);
        } else {
            audioManager.abandonAudioFocus(focusListener);
        }
    }

    private void play() {
        AudioTrack track = null;
        Object focusRequest = null;
        boolean playing = false;
        try {
            int generation = epoch();
            byte[] packet = nextPacket(generation);
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
            focusRequest = requestAudioFocus(attributes);
            if (focusRequest == null || closed) {
                return;
            }
            AudioFormat format = new AudioFormat.Builder().setSampleRate(SAMPLE_RATE)
                    .setChannelMask(CHANNELS).setEncoding(FORMAT).build();
            track = new AudioTrack(attributes, format, Math.max(MIN_BUFFER_BYTES, nativeMinimum),
                    AudioTrack.MODE_STREAM, AudioManager.AUDIO_SESSION_ID_GENERATE);
            if (track.getState() != AudioTrack.STATE_INITIALIZED || closed) {
                return;
            }
            Log.i(LinkService.TAG, "Cast audio ready: 44100 Hz stereo PCM");
            while (!closed) {
                int currentEpoch = epoch();
                if (currentEpoch != generation) {
                    if (playing) {
                        track.pause();
                        track.flush();
                        playing = false;
                    }
                    packet = null;
                    generation = currentEpoch;
                }
                if (packet == null) {
                    packet = nextPacket(generation);
                    continue;
                }
                if (!focused || closed) {
                    packet = null;
                    continue;
                }
                if (!playing) {
                    track.play();
                    playing = true;
                }
                ByteBuffer buffer = ByteBuffer.wrap(packet);
                while (!closed && focused && epoch() == generation && buffer.hasRemaining()) {
                    int written = track.write(buffer, buffer.remaining(), AudioTrack.WRITE_NON_BLOCKING);
                    if (written < 0) {
                        throw new IllegalStateException("AudioTrack write failed");
                    }
                    if (written == 0) {
                        Thread.sleep(4);
                    }
                }
                packet = null;
            }
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        } catch (RuntimeException error) {
            if (!closed) {
                Log.w(LinkService.TAG, "Cast audio unavailable: " + error.getClass().getSimpleName());
            }
        } finally {
            if (track != null) {
                if (playing) {
                    try {
                        track.pause();
                        track.flush();
                    } catch (IllegalStateException ignored) {
                        // Audio may already have been halted by the system.
                    }
                }
                track.release();
                Log.i(LinkService.TAG, "Cast audio playback stopped");
            }
            abandonAudioFocus(focusRequest);
            synchronized (lock) {
                closed = true;
                clearQueue();
            }
        }
    }

    private static final class FocusApi {
        static Object request(AudioManager manager, AudioAttributes attributes,
                              AudioManager.OnAudioFocusChangeListener listener) {
            AudioFocusRequest request = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                    .setAudioAttributes(attributes)
                    .setWillPauseWhenDucked(true)
                    .setOnAudioFocusChangeListener(listener, new Handler(Looper.getMainLooper()))
                    .build();
            return manager.requestAudioFocus(request) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
                    ? request : null;
        }

        static void abandon(AudioManager manager, Object request) {
            manager.abandonAudioFocusRequest((AudioFocusRequest) request);
        }
    }
}
