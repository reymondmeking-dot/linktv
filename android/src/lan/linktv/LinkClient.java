package lan.linktv;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.math.BigInteger;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.SocketException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.MessageDigest;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECFieldFp;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.ECParameterSpec;
import java.security.spec.ECPoint;
import java.security.spec.ECPublicKeySpec;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.TimeUnit;
import javax.crypto.Cipher;
import javax.crypto.KeyAgreement;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;

final class LinkClient {
    static final int START = 0x10;
    static final int STOP = 0x11;
    static final int JPEG = 0x12;
    static final int AUDIO = 0x13;
    static final int STOP_REQUEST = 0x14;
    static final int H264_SEND = 0x01;
    static final int H264_RECEIVE = 0x02;
    static final int H264_BEGIN = 0x16;
    static final int H264_FRAME = 0x17;

    private static final int CAPABILITIES = 0x15;
    private static final int CAPABILITY_MASK = H264_SEND | H264_RECEIVE;
    static final int PORT = 17863;

    private static final int ACCEPT_PAIRING = 0x01;
    private static final int REJECT_PAIRING = 0x02;
    private static final String PROTOCOL_V2 = "LINKTV2";
    private static final String PROTOCOL_V3 = "LINKTV3";
    private static final int NONCE_SIZE = 32;
    private static final int COORDINATE_SIZE = 32;
    private static final int MAX_NAME_SIZE = 96;
    private static final int IV_SIZE = 16;
    private static final int MAC_SIZE = 32;
    private static final int MIN_BODY = 64;
    private static final int MAX_BODY = 2_097_152;
    static final int MAX_JPEG = MAX_BODY - MIN_BODY - 1;
    static final int MAX_AUDIO = 16_384;
    private static final int PCM_SAMPLE_FRAME_BYTES = 4;
    private static final int MAX_QUEUED_AUDIO = 6;
    private static final int H264_HEADER_BYTES = 9;
    static final int MAX_H264_ACCESS_UNIT = MAX_BODY - IV_SIZE - MAC_SIZE - 2 - H264_HEADER_BYTES;
    private static final int MAX_QUEUED_H264 = 12;
    private static final int MAX_QUEUED_H264_BYTES = 8 * 1024 * 1024;
    private static final byte[] EMPTY = new byte[0];
    private static final int IO_TIMEOUT_MS = 8000;
    private static final int COMPARE_TIMEOUT_MS = 75_000;

    interface Listener {
        void onPairingRequested(String peerName, String comparisonCode);
        void onConnected(LinkClient client);
        void onClosed(LinkClient client, String reason);
        void onMessage(LinkClient client, int type, byte[] payload);
    }

    private final InetAddress address;
    private final int port;
    private final byte[] localName;
    private final int localCapabilities;
    private final String preferredProtocol;
    private final Listener listener;
    private final SecureRandom random = new SecureRandom();
    private final Object outgoingLock = new Object();
    private final ArrayDeque<Integer> controlMessages = new ArrayDeque<>();
    private final ArrayDeque<byte[]> audioMessages = new ArrayDeque<>();
    private final ArrayDeque<byte[]> videoMessages = new ArrayDeque<>();
    private byte[] nextJpeg;
    private byte[] pendingVideoStart;
    private int queuedVideoBytes;
    private boolean localCasting;
    private boolean localH264;
    private long lastLocalPts = -1;
    private volatile boolean negotiatedH264Send;
    private volatile boolean negotiatedH264Receive;
    private boolean pairingPending;
    private int pairingDecision;
    private boolean localConfirmationSent;
    private volatile boolean connected;
    private volatile boolean closed;
    private volatile Socket socket;

    private LinkClient(InetAddress address, int port, String localName, int localCapabilities,
                       String preferredProtocol, Listener listener) {
        this.address = address;
        this.port = port;
        this.localCapabilities = localCapabilities;
        this.preferredProtocol = preferredProtocol;
        this.listener = listener;
        String name = localName == null ? "" : localName.trim();
        this.localName = (validName(name) ? name : "Android 设备").getBytes(StandardCharsets.UTF_8);
    }

    static LinkClient connect(InetAddress address, int port, String localName, Listener listener) {
        return connect(address, port, localName, 0, PROTOCOL_V2, listener);
    }

    static LinkClient connect(InetAddress address, int port, String localName, int localCapabilities,
                              Listener listener) {
        if ((localCapabilities & ~CAPABILITY_MASK) != 0) {
            throw new IllegalArgumentException("Invalid H.264 capabilities");
        }
        return connect(address, port, localName, localCapabilities, PROTOCOL_V3, listener);
    }

    private static LinkClient connect(InetAddress address, int port, String localName, int localCapabilities,
                                      String protocol, Listener listener) {
        LinkClient client = new LinkClient(address, port, localName, localCapabilities, protocol, listener);
        new Thread(client::read, "LinkTV reader").start();
        return client;
    }

    boolean canSendH264() {
        return connected && !closed && negotiatedH264Send;
    }

    boolean canReceiveH264() {
        return connected && !closed && negotiatedH264Receive;
    }

    boolean respondToPairing(boolean accepted) {
        synchronized (outgoingLock) {
            if (closed || !pairingPending || pairingDecision != 0) {
                return false;
            }
            pairingDecision = accepted ? ACCEPT_PAIRING : REJECT_PAIRING;
            outgoingLock.notifyAll();
            return true;
        }
    }

    void control(int type) {
        synchronized (outgoingLock) {
            if (closed || !connected || (type != START && type != STOP)) {
                return;
            }
            if (controlMessages.size() >= 16) {
                close("投屏指令发送超时");
                return;
            }
            localCasting = type == START;
            if (!localCasting) {
                nextJpeg = null;
                audioMessages.clear();
                clearOutgoingVideo();
            }
            controlMessages.addLast(type);
            outgoingLock.notifyAll();
        }
    }

    boolean requestRemoteStop() {
        synchronized (outgoingLock) {
            if (closed || !connected || controlMessages.size() >= 16) {
                return false;
            }
            controlMessages.addLast(STOP_REQUEST);
            outgoingLock.notifyAll();
            return true;
        }
    }

    void jpeg(byte[] data) {
        synchronized (outgoingLock) {
            if (!closed && connected && !localH264 && data.length > 0 && data.length <= MAX_JPEG) {
                nextJpeg = data;
                outgoingLock.notifyAll();
            }
        }
    }

    boolean sendH264Begin(int width, int height, int fps) {
        if (!validH264Format(width, height, fps)) {
            return false;
        }
        synchronized (outgoingLock) {
            if (!canSendH264() || !localCasting || localH264) {
                return false;
            }
            localH264 = true;
            nextJpeg = null;
            pendingVideoStart = ByteBuffer.allocate(6).putShort((short) width)
                    .putShort((short) height).putShort((short) fps).array();
            outgoingLock.notifyAll();
            return true;
        }
    }

    boolean sendH264Frame(long ptsUs, boolean keyFrame, byte[] accessUnit) {
        if (ptsUs < 0 || accessUnit == null || accessUnit.length > MAX_H264_ACCESS_UNIT
                || !hasAnnexBStartCode(accessUnit, 0)) {
            return false;
        }
        synchronized (outgoingLock) {
            if (!canSendH264() || !localCasting || !localH264 || ptsUs <= lastLocalPts
                    || (lastLocalPts < 0 && !keyFrame)) {
                return false;
            }
            int length = H264_HEADER_BYTES + accessUnit.length;
            if (videoMessages.size() >= MAX_QUEUED_H264 || queuedVideoBytes + length > MAX_QUEUED_H264_BYTES) {
                close("网络过慢，H.264 投屏已断开");
                return false;
            }
            byte[] payload = ByteBuffer.allocate(length).putLong(ptsUs)
                    .put((byte) (keyFrame ? 1 : 0)).put(accessUnit).array();
            videoMessages.addLast(payload);
            queuedVideoBytes += length;
            lastLocalPts = ptsUs;
            outgoingLock.notifyAll();
            return true;
        }
    }

    private void clearOutgoingVideo() {
        pendingVideoStart = null;
        videoMessages.clear();
        queuedVideoBytes = 0;
        localH264 = false;
        lastLocalPts = -1;
    }

    private static boolean validH264Format(int width, int height, int fps) {
        return width >= 2 && height >= 2 && width % 2 == 0 && height % 2 == 0
                && Math.max(width, height) <= 1920 && Math.min(width, height) <= 1080
                && fps >= 1 && fps <= 60;
    }

    private static boolean hasAnnexBStartCode(byte[] data, int offset) {
        int remaining = data.length - offset;
        return remaining >= 4 && data[offset] == 0 && data[offset + 1] == 0
                && (data[offset + 2] == 1
                    || (remaining >= 5 && data[offset + 2] == 0 && data[offset + 3] == 1));
    }

    boolean sendAudio(byte[] pcm) {
        synchronized (outgoingLock) {
            if (closed || !connected || !localCasting || pcm == null || !validAudioLength(pcm.length)) {
                return false;
            }
            if (audioMessages.size() == MAX_QUEUED_AUDIO) {
                audioMessages.removeFirst();
            }
            audioMessages.addLast(pcm.clone());
            outgoingLock.notifyAll();
            return true;
        }
    }

    // Signed 16-bit little-endian stereo at 44.1 kHz.
    private static boolean validAudioLength(int length) {
        return length > 0 && length <= MAX_AUDIO && length % PCM_SAMPLE_FRAME_BYTES == 0;
    }

    void close() {
        close(null);
    }

    private void close(String reason) {
        synchronized (outgoingLock) {
            if (closed) {
                return;
            }
            closed = true;
            controlMessages.clear();
            audioMessages.clear();
            nextJpeg = null;
            clearOutgoingVideo();
            outgoingLock.notifyAll();
        }
        Socket current = socket;
        if (current != null) {
            try {
                current.close();
            } catch (IOException ignored) {
                // Closing the socket is best effort.
            }
        }
        listener.onClosed(this, reason);
    }

    private void read() {
        boolean pairingShown = false;
        try {
            OpenConnection attempt;
            try {
                attempt = open(preferredProtocol);
            } catch (LegacyPeerException unavailable) {
                attempt = open(PROTOCOL_V2);
            }
            Socket current = attempt.socket;
            DataInputStream input = attempt.input;
            DataOutputStream output = attempt.output;
            PairingKeys pairing = attempt.keys;
            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(COMPARE_TIMEOUT_MS);
            synchronized (outgoingLock) {
                if (closed) {
                    return;
                }
                pairingPending = true;
            }
            pairingShown = true;
            new Thread(() -> write(output, pairing.outbound, deadline), "LinkTV writer").start();
            listener.onPairingRequested(pairing.peerName, pairing.comparisonCode);

            byte[] confirmation = readPairingFrame(current, input, pairing.inbound, deadline);
            if (confirmation.length == 1 && confirmation[0] == REJECT_PAIRING) {
                close("对方拒绝了配对");
                return;
            }
            if (confirmation.length != 1 || confirmation[0] != ACCEPT_PAIRING) {
                throw new IOException("Expected pairing confirmation");
            }
            synchronized (outgoingLock) {
                while (!closed && !localConfirmationSent) {
                    outgoingLock.wait(remainingMillis(deadline));
                }
                if (closed) {
                    return;
                }
            }
            if (PROTOCOL_V3.equals(attempt.protocol)) {
                exchangeCapabilities(current, input, output, pairing);
            }
            synchronized (outgoingLock) {
                if (closed) {
                    return;
                }
                connected = true;
                pairingPending = false;
                outgoingLock.notifyAll();
            }
            current.setSoTimeout(0);
            listener.onConnected(this);

            boolean remoteCasting = false;
            boolean remoteH264 = false;
            long lastRemotePts = -1;
            while (!closed) {
                int length = input.readInt();
                if (length < MIN_BODY || length > MAX_BODY || length % 16 != 0) {
                    throw new IOException("Invalid message length");
                }
                byte[] body = new byte[length];
                input.readFully(body);
                byte[] plaintext = pairing.inbound.decrypt(body);
                if (plaintext.length == 0) {
                    throw new IOException("Empty message");
                }
                int type = plaintext[0] & 0xff;
                if ((type == START || type == STOP) && plaintext.length == 1) {
                    boolean starting = type == START;
                    if (starting != remoteCasting) {
                        remoteH264 = false;
                        lastRemotePts = -1;
                    }
                    remoteCasting = starting;
                    listener.onMessage(this, type, EMPTY);
                } else if (type == H264_BEGIN) {
                    if (!canReceiveH264() || !remoteCasting || remoteH264 || plaintext.length != 7) {
                        throw new IOException("Unexpected H.264 start");
                    }
                    ByteBuffer format = ByteBuffer.wrap(plaintext, 1, 6);
                    if (!validH264Format(format.getShort() & 0xffff, format.getShort() & 0xffff,
                            format.getShort() & 0xffff)) {
                        throw new IOException("Invalid H.264 format");
                    }
                    remoteH264 = true;
                    listener.onMessage(this, type, Arrays.copyOfRange(plaintext, 1, plaintext.length));
                } else if (type == H264_FRAME) {
                    int offset = 1 + H264_HEADER_BYTES;
                    if (!canReceiveH264() || !remoteCasting || !remoteH264
                            || plaintext.length - offset > MAX_H264_ACCESS_UNIT
                            || !hasAnnexBStartCode(plaintext, offset) || (plaintext[9] & 0xff) > 1) {
                        throw new IOException("Invalid H.264 access unit");
                    }
                    long ptsUs = ByteBuffer.wrap(plaintext, 1, 8).getLong();
                    if (ptsUs < 0 || ptsUs <= lastRemotePts || (lastRemotePts < 0 && plaintext[9] != 1)) {
                        throw new IOException("Invalid H.264 frame sequence");
                    }
                    lastRemotePts = ptsUs;
                    listener.onMessage(this, type, Arrays.copyOfRange(plaintext, 1, plaintext.length));
                } else if ((type == JPEG && !remoteH264 && plaintext.length > 1)
                        || (type == AUDIO && validAudioLength(plaintext.length - 1))) {
                    if (remoteCasting) {
                        byte[] payload = Arrays.copyOfRange(plaintext, 1, plaintext.length);
                        listener.onMessage(this, type, payload);
                    }
                } else {
                    throw new IOException("Invalid message");
                }
            }
        } catch (SocketTimeoutException error) {
            close(pairingShown ? "确认配对超时，请重新选择设备" : "连接对方设备超时，请检查网络");
        } catch (EOFException error) {
            close(connected ? "对方设备已断开连接" : "对方结束了配对");
        } catch (IOException error) {
            close(connected ? "与对方设备的连接已中断"
                    : pairingShown ? "配对连接已中断" : "无法连接对方设备，请检查网络和应用");
        } catch (GeneralSecurityException error) {
            close("连接校验失败");
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            close("投屏连接已停止");
        }
    }

    private OpenConnection open(String protocol) throws IOException, GeneralSecurityException {
        Socket current = new Socket();
        socket = current;
        boolean retained = false;
        try {
            if (closed) {
                throw new EOFException();
            }
            current.connect(new InetSocketAddress(address, port), IO_TIMEOUT_MS);
            current.setTcpNoDelay(true);
            current.setKeepAlive(true);
            DataInputStream input = new DataInputStream(new BufferedInputStream(current.getInputStream()));
            DataOutputStream output = new DataOutputStream(new BufferedOutputStream(current.getOutputStream()));
            PairingKeys keys;
            try {
                keys = exchangeKeys(current, input, output, protocol);
            } catch (EOFException | SocketException unavailable) {
                if (!closed && PROTOCOL_V3.equals(protocol)) {
                    throw new LegacyPeerException();
                }
                throw unavailable;
            }
            retained = true;
            return new OpenConnection(current, input, output, protocol, keys);
        } finally {
            if (!retained) {
                current.close();
            }
        }
    }

    private void exchangeCapabilities(Socket current, DataInputStream input, DataOutputStream output,
                                      PairingKeys pairing) throws IOException, GeneralSecurityException {
        writeFrame(output, pairing.outbound, CAPABILITIES, new byte[] {(byte) localCapabilities});
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(IO_TIMEOUT_MS);
        byte[] packet = readPairingFrame(current, input, pairing.inbound, deadline);
        if (packet.length != 2 || packet[0] != CAPABILITIES || (packet[1] & ~CAPABILITY_MASK) != 0) {
            throw new IOException("Invalid peer capabilities");
        }
        negotiatedH264Send = (localCapabilities & H264_SEND) != 0 && (packet[1] & H264_RECEIVE) != 0;
        negotiatedH264Receive = (localCapabilities & H264_RECEIVE) != 0 && (packet[1] & H264_SEND) != 0;
    }

    private PairingKeys exchangeKeys(Socket current, DataInputStream input, DataOutputStream output, String protocol)
            throws GeneralSecurityException, IOException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(IO_TIMEOUT_MS);
        KeyPairGenerator keyGenerator = KeyPairGenerator.getInstance("EC");
        keyGenerator.initialize(new ECGenParameterSpec("secp256r1"), random);
        KeyPair local = keyGenerator.generateKeyPair();
        ECPublicKey localPublic = (ECPublicKey) local.getPublic();
        byte[] coordinates = join(fixedWidth(localPublic.getW().getAffineX().toByteArray()),
                fixedWidth(localPublic.getW().getAffineY().toByteArray()));
        byte[] identity = join(coordinates, new byte[] {(byte) localName.length}, localName);
        byte[] clientNonce = randomBytes(NONCE_SIZE);
        byte[] magic = protocol.getBytes(StandardCharsets.US_ASCII);
        byte[] clientHello = join(magic, identity, commitment(protocol, "C", identity, clientNonce));
        output.write(clientHello);
        output.flush();

        byte[] serverCoordinates = readBefore(current, input, 2 * COORDINATE_SIZE, deadline);
        int nameSize = readBefore(current, input, 1, deadline)[0] & 0xff;
        if (nameSize == 0 || nameSize > MAX_NAME_SIZE) {
            throw new IOException("Invalid pairing name length");
        }
        byte[] peerNameBytes = readBefore(current, input, nameSize, deadline);
        String peerName = StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(peerNameBytes)).toString();
        if (!validName(peerName)) {
            throw new IOException("Invalid pairing name");
        }
        byte[] serverIdentity = join(serverCoordinates, new byte[] {(byte) nameSize}, peerNameBytes);
        byte[] serverCommitment = readBefore(current, input, MAC_SIZE, deadline);
        output.write(clientNonce);
        output.flush();
        byte[] serverNonce = readBefore(current, input, NONCE_SIZE, deadline);
        if (!MessageDigest.isEqual(serverCommitment, commitment(protocol, "S", serverIdentity, serverNonce))) {
            throw new GeneralSecurityException("Pairing commitment mismatch");
        }

        KeyAgreement agreement = KeyAgreement.getInstance("ECDH");
        agreement.init(local.getPrivate());
        agreement.doPhase(peerKey(serverCoordinates, localPublic.getParams()), true);
        byte[] secret = agreement.generateSecret();
        byte[] sharedMaterial = hash(fixedWidth(secret));
        Arrays.fill(secret, (byte) 0);
        byte[] transcript = hash(join(clientHello, serverIdentity, serverCommitment, clientNonce, serverNonce));
        byte[] sessionKey = hmac(transcript, sharedMaterial);
        Arrays.fill(sharedMaterial, (byte) 0);
        long value = ByteBuffer.wrap(derive(protocol, sessionKey, "compare")).getInt() & 0xffff_ffffL;
        String code = String.format(Locale.ROOT, "%06d", value % 1_000_000);
        Channel outbound = new Channel(derive(protocol, sessionKey, "ce"), derive(protocol, sessionKey, "cm"));
        Channel inbound = new Channel(derive(protocol, sessionKey, "se"), derive(protocol, sessionKey, "sm"));
        Arrays.fill(sessionKey, (byte) 0);
        return new PairingKeys(peerName, code, outbound, inbound);
    }

    private static PublicKey peerKey(byte[] coordinates, ECParameterSpec params)
            throws GeneralSecurityException {
        BigInteger x = new BigInteger(1, Arrays.copyOfRange(coordinates, 0, COORDINATE_SIZE));
        BigInteger y = new BigInteger(1, Arrays.copyOfRange(coordinates, COORDINATE_SIZE, coordinates.length));
        BigInteger prime = ((ECFieldFp) params.getCurve().getField()).getP();
        BigInteger curveValue = x.multiply(x).multiply(x).add(params.getCurve().getA().multiply(x))
                .add(params.getCurve().getB()).mod(prime);
        if (x.compareTo(prime) >= 0 || y.compareTo(prime) >= 0 || !y.multiply(y).mod(prime).equals(curveValue)) {
            throw new GeneralSecurityException("Invalid P-256 point");
        }
        return KeyFactory.getInstance("EC").generatePublic(new ECPublicKeySpec(new ECPoint(x, y), params));
    }

    private static boolean validName(String name) {
        if (name.isEmpty() || name.getBytes(StandardCharsets.UTF_8).length > MAX_NAME_SIZE) {
            return false;
        }
        for (int offset = 0; offset < name.length(); offset++) {
            if (Character.isISOControl(name.charAt(offset))) {
                return false;
            }
        }
        return true;
    }

    private static byte[] fixedWidth(byte[] value) throws GeneralSecurityException {
        int prefix = Math.max(0, value.length - COORDINATE_SIZE);
        for (int i = 0; i < prefix; i++) {
            if (value[i] != 0) {
                throw new GeneralSecurityException("Invalid EC coordinate length");
            }
        }
        byte[] result = new byte[COORDINATE_SIZE];
        int count = value.length - prefix;
        System.arraycopy(value, prefix, result, result.length - count, count);
        return result;
    }

    private static byte[] join(byte[]... parts) {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        for (byte[] part : parts) {
            buffer.write(part, 0, part.length);
        }
        return buffer.toByteArray();
    }

    private static byte[] hash(byte[] data) throws GeneralSecurityException {
        return MessageDigest.getInstance("SHA-256").digest(data);
    }

    private static byte[] hmac(byte[] key, byte[] data) throws GeneralSecurityException {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return mac.doFinal(data);
    }

    private static byte[] commitment(String protocol, String role, byte[] identity, byte[] nonce)
            throws GeneralSecurityException {
        byte[] label = (protocol + " commit " + role).getBytes(StandardCharsets.US_ASCII);
        return hash(join(label, identity, nonce));
    }

    private static byte[] derive(String protocol, byte[] key, String label) throws GeneralSecurityException {
        return hmac(key, (protocol + " " + label + "\u0001").getBytes(StandardCharsets.US_ASCII));
    }

    private byte[] randomBytes(int size) {
        byte[] bytes = new byte[size];
        random.nextBytes(bytes);
        return bytes;
    }

    private static int remainingMillis(long deadline) throws SocketTimeoutException {
        long remaining = deadline - System.nanoTime();
        if (remaining <= 0) {
            throw new SocketTimeoutException("Pairing deadline elapsed");
        }
        return (int) Math.max(1, TimeUnit.NANOSECONDS.toMillis(remaining));
    }

    private static byte[] readBefore(Socket socket, DataInputStream input, int size, long deadline)
            throws IOException {
        byte[] bytes = new byte[size];
        int offset = 0;
        while (offset < size) {
            socket.setSoTimeout(remainingMillis(deadline));
            int read = input.read(bytes, offset, size - offset);
            if (read == -1) {
                throw new EOFException();
            }
            offset += read;
        }
        return bytes;
    }

    private static byte[] readPairingFrame(Socket socket, DataInputStream input, Channel inbound, long deadline)
            throws IOException, GeneralSecurityException {
        int size = ByteBuffer.wrap(readBefore(socket, input, 4, deadline)).getInt();
        if (size != MIN_BODY) {
            throw new IOException("Invalid pairing frame size");
        }
        return inbound.decrypt(readBefore(socket, input, size, deadline));
    }

    private void write(DataOutputStream output, Channel outbound, long deadline) {
        try {
            int decision;
            synchronized (outgoingLock) {
                while (!closed && pairingDecision == 0) {
                    outgoingLock.wait(remainingMillis(deadline));
                }
                if (closed) {
                    return;
                }
                decision = pairingDecision;
            }
            writeFrame(output, outbound, decision, EMPTY);
            if (decision == REJECT_PAIRING) {
                close("已拒绝配对");
                return;
            }
            synchronized (outgoingLock) {
                localConfirmationSent = true;
                outgoingLock.notifyAll();
                while (!closed && !connected) {
                    outgoingLock.wait(remainingMillis(deadline));
                }
            }
            while (true) {
                int type;
                byte[] payload;
                synchronized (outgoingLock) {
                    while (!closed && controlMessages.isEmpty() && nextJpeg == null && audioMessages.isEmpty()
                            && pendingVideoStart == null && videoMessages.isEmpty()) {
                        outgoingLock.wait();
                    }
                    if (closed) {
                        return;
                    }
                    if (!controlMessages.isEmpty()) {
                        type = controlMessages.removeFirst();
                        payload = EMPTY;
                    } else if (pendingVideoStart != null) {
                        type = H264_BEGIN;
                        payload = pendingVideoStart;
                        pendingVideoStart = null;
                    } else if (nextJpeg != null) {
                        type = JPEG;
                        payload = nextJpeg;
                        nextJpeg = null;
                    } else if (!audioMessages.isEmpty()) {
                        type = AUDIO;
                        payload = audioMessages.removeFirst();
                    } else {
                        type = H264_FRAME;
                        payload = videoMessages.removeFirst();
                        queuedVideoBytes -= payload.length;
                    }
                }
                writeFrame(output, outbound, type, payload);
            }
        } catch (SocketTimeoutException error) {
            close("确认配对超时，请重新选择设备");
        } catch (IOException | GeneralSecurityException error) {
            close("与对方设备的连接已中断");
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            close("投屏连接已停止");
        }
    }

    private static void writeFrame(DataOutputStream output, Channel outbound, int type, byte[] payload)
            throws IOException, GeneralSecurityException {
        byte[] message = outbound.encrypt(type, payload);
        output.writeInt(message.length);
        output.write(message);
        output.flush();
    }

    private static final class LegacyPeerException extends IOException {
        private static final long serialVersionUID = 1L;
    }

    private static final class OpenConnection {
        final Socket socket;
        final DataInputStream input;
        final DataOutputStream output;
        final String protocol;
        final PairingKeys keys;

        OpenConnection(Socket socket, DataInputStream input, DataOutputStream output,
                       String protocol, PairingKeys keys) {
            this.socket = socket;
            this.input = input;
            this.output = output;
            this.protocol = protocol;
            this.keys = keys;
        }
    }

    private final class PairingKeys {
        final String peerName;
        final String comparisonCode;
        final Channel outbound;
        final Channel inbound;

        PairingKeys(String peerName, String comparisonCode, Channel outbound, Channel inbound) {
            this.peerName = peerName;
            this.comparisonCode = comparisonCode;
            this.outbound = outbound;
            this.inbound = inbound;
        }
    }

    private final class Channel {
        private final SecretKeySpec encryptionKey;
        private final Mac mac;
        private final Cipher cipher;
        private long sequence;

        Channel(byte[] encryptionKey, byte[] macKey) throws GeneralSecurityException {
            this.encryptionKey = new SecretKeySpec(encryptionKey, "AES");
            mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(macKey, "HmacSHA256"));
            cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
        }

        byte[] encrypt(int type, byte[] payload) throws GeneralSecurityException {
            byte[] iv = randomBytes(IV_SIZE);
            cipher.init(Cipher.ENCRYPT_MODE, encryptionKey, new IvParameterSpec(iv));
            byte[] plaintext = new byte[1 + payload.length];
            plaintext[0] = (byte) type;
            System.arraycopy(payload, 0, plaintext, 1, payload.length);
            byte[] ciphertext = cipher.doFinal(plaintext);
            byte[] body = new byte[iv.length + ciphertext.length + MAC_SIZE];
            System.arraycopy(iv, 0, body, 0, iv.length);
            System.arraycopy(ciphertext, 0, body, iv.length, ciphertext.length);
            byte[] proof = proof(body);
            System.arraycopy(proof, 0, body, body.length - MAC_SIZE, MAC_SIZE);
            sequence++;
            return body;
        }

        byte[] decrypt(byte[] body) throws GeneralSecurityException {
            int proofOffset = body.length - MAC_SIZE;
            byte[] receivedProof = Arrays.copyOfRange(body, proofOffset, body.length);
            if (!MessageDigest.isEqual(receivedProof, proof(body))) {
                throw new GeneralSecurityException("Invalid frame authentication");
            }
            cipher.init(Cipher.DECRYPT_MODE, encryptionKey, new IvParameterSpec(body, 0, IV_SIZE));
            byte[] plaintext = cipher.doFinal(body, IV_SIZE, proofOffset - IV_SIZE);
            sequence++;
            return plaintext;
        }

        private byte[] proof(byte[] body) {
            mac.update(ByteBuffer.allocate(12).putLong(sequence).putInt(body.length).array());
            mac.update(body, 0, body.length - MAC_SIZE);
            return mac.doFinal();
        }
    }
}
