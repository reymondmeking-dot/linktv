package lan.linktv;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;
import android.view.Surface;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

final class AvcReceiver {
    private static final int FRAME_HEADER = 9;
    private static final long LATENCY_NS = 20_000_000;
    private static final long MAX_LEAD_NS = 120_000_000;
    private static final long MAX_LAG_NS = 220_000_000;
    private static final long MAX_RELATIVE_PTS = 1_200_000_000L;

    interface Listener {
        void firstPicture(AvcReceiver receiver);
        void failed(AvcReceiver receiver);
    }

    final int width;
    final int height;
    final int fps;

    private final String decoder;
    private final Listener listener;
    private final AvcAnnexB.ParameterSets parameters = new AvcAnnexB.ParameterSets();
    private Worker worker;
    private Surface surface;
    private boolean closed;

    AvcReceiver(int width, int height, int fps, Listener listener) {
        this.width = width;
        this.height = height;
        this.fps = fps;
        this.listener = listener;
        decoder = AvcCodecProfile.decoder(width, height, fps);
    }

    boolean supported() {
        return decoder != null;
    }

    synchronized void setSurface(Surface next) {
        if (closed || surface == next) {
            return;
        }
        stopWorker();
        surface = next;
    }

    synchronized void close() {
        closed = true;
        surface = null;
        stopWorker();
    }

    private void stopWorker() {
        if (worker != null) {
            worker.close();
            worker = null;
        }
    }

    boolean offerFrame(byte[] payload) {
        if (payload.length <= FRAME_HEADER) {
            return false;
        }
        ByteBuffer header = ByteBuffer.wrap(payload);
        long pts = header.getLong();
        boolean key = header.get() == 1;

        Worker current;
        synchronized (this) {
            if (closed) {
                return true;
            }
            boolean idr = key && parameters.accept(payload, FRAME_HEADER);
            if (worker == null) {
                // Without a visible Surface there is no decoder to maintain. A new Surface
                // starts cleanly at the next IDR and never queues frames from a broken chain.
                if (surface == null || !surface.isValid() || !idr || !parameters.ready()) {
                    return true;
                }
                worker = new Worker(surface, parameters.sps, parameters.pps);
                worker.thread.start();
            }
            current = worker;
        }
        return current.offer(new AccessUnit(pts, payload));
    }

    private synchronized void firstPicture(Worker source) {
        if (source == worker && !closed) {
            listener.firstPicture(this);
        }
    }

    private synchronized void fail(Worker source) {
        if (source == worker && !closed) {
            listener.failed(this);
        }
    }

    private static final class AccessUnit {
        final long pts;
        final byte[] payload;

        AccessUnit(long pts, byte[] payload) {
            this.pts = pts;
            this.payload = payload;
        }
    }

    private final class Worker {
        final Thread thread = new Thread(this::decode, "LinkTV AVC decoder");
        private final ArrayBlockingQueue<AccessUnit> frames = new ArrayBlockingQueue<>(10);
        private final Surface output;
        private final byte[] sps;
        private final byte[] pps;
        private volatile boolean stopped;
        private volatile boolean failed;

        Worker(Surface output, byte[] sps, byte[] pps) {
            this.output = output;
            this.sps = sps;
            this.pps = pps;
        }

        boolean offer(AccessUnit unit) {
            try {
                return stopped || !failed && (frames.offer(unit, 180, TimeUnit.MILLISECONDS) || stopped);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                return stopped;
            }
        }

        void close() {
            stopped = true;
            frames.clear();
            thread.interrupt();
        }

        private void decode() {
            MediaCodec codec = null;
            try {
                codec = MediaCodec.createByCodecName(decoder);
                MediaFormat format = MediaFormat.createVideoFormat(AvcCodecProfile.MIME, width, height);
                format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
                format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, LinkClient.MAX_H264_ACCESS_UNIT);
                format.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
                format.setByteBuffer("csd-1", ByteBuffer.wrap(pps));
                codec.configure(format, output, null, 0);
                codec.start();
                Log.i(LinkService.TAG, "H264 decoder " + decoder + " " + width + "x" + height + " @" + fps);
                drain(codec);
            } catch (IOException | RuntimeException error) {
                if (!stopped) {
                    failed = true;
                    Log.w(LinkService.TAG, "AVC decoder stopped: " + error.getClass().getSimpleName());
                    fail(this);
                }
            } finally {
                if (codec != null) {
                    try {
                        codec.stop();
                    } catch (IllegalStateException ignored) {
                        // Stop remains best effort on a failed decoder.
                    }
                    try {
                        codec.release();
                    } catch (IllegalStateException ignored) {
                        // An invalidated Surface can stop the codec service first.
                    }
                }
                frames.clear();
            }
        }

        private void drain(MediaCodec codec) throws IOException {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            AccessUnit pending = null;
            int pictureCount = 0;
            long firstPts = -1;
            long renderBase = 0;
            while (!stopped) {
                if (pending == null) {
                    pending = frames.poll();
                }
                boolean submitted = false;
                if (pending != null) {
                    int index = codec.dequeueInputBuffer(0);
                    if (index >= 0) {
                        ByteBuffer input = codec.getInputBuffer(index);
                        int bytes = pending.payload.length - FRAME_HEADER;
                        if (input == null || input.capacity() < bytes) {
                            throw new IOException("AVC decoder input capacity");
                        }
                        input.clear();
                        input.put(pending.payload, FRAME_HEADER, bytes);
                        codec.queueInputBuffer(index, 0, bytes, pending.pts, 0);
                        pending = null;
                        submitted = true;
                    }
                }

                int index = codec.dequeueOutputBuffer(info, submitted ? 0 : 4000);
                if (index < 0) {
                    continue;
                }
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    codec.releaseOutputBuffer(index, false);
                    continue;
                }
                long now = System.nanoTime();
                if (firstPts < 0 || info.presentationTimeUs - firstPts > MAX_RELATIVE_PTS) {
                    firstPts = info.presentationTimeUs;
                    renderBase = now + LATENCY_NS;
                }
                long renderAt = renderBase + (info.presentationTimeUs - firstPts) * 1000;
                if (renderAt - now > MAX_LEAD_NS || now - renderAt > MAX_LAG_NS) {
                    firstPts = info.presentationTimeUs;
                    renderBase = now + LATENCY_NS;
                    renderAt = renderBase;
                }
                codec.releaseOutputBuffer(index, Math.max(now, renderAt));
                pictureCount++;
                if (pictureCount == 1) {
                    firstPicture(this);
                }
                if (pictureCount == 1 || pictureCount % 120 == 0) {
                    Log.i(LinkService.TAG, "H264 frames submitted to Surface: " + pictureCount);
                }
            }
        }
    }
}
