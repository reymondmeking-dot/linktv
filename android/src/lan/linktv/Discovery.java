package lan.linktv;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Base64;
import android.util.Log;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;

final class Discovery implements AutoCloseable {
    private static final int PC_UDP_PORT = 17864;
    private static final int TV_UDP_PORT = 17865;
    private static final int ANNOUNCE_MS = 2000;
    private static final int EXPIRE_MS = 8000;
    private static final int INVITE_DEBOUNCE_MS = 2500;
    private static final int SOCKET_WAIT_MS = 350;
    private static final int MAX_PEERS = 24;
    private static final int MAX_PACKET = 256;
    private static final String PC_PREFIX = "LINKTV2|PC|";
    private static final String INVITE_PREFIX = "LINKTV2|INVITE|";
    private static final String TV_PREFIX = "LINKTV2|TV|";

    interface Listener {
        void onPeersChanged(List<Peer> peers);
        void onInvitation(Peer peer);
    }

    static final class Peer {
        final InetAddress address;
        final String name;

        Peer(InetAddress address, String name) {
            this.address = address;
            this.name = name;
        }

        @Override
        public boolean equals(Object value) {
            if (!(value instanceof Peer)) {
                return false;
            }
            Peer other = (Peer) value;
            return address.equals(other.address) && name.equals(other.name);
        }

        @Override
        public int hashCode() {
            return 31 * address.hashCode() + name.hashCode();
        }
    }

    private static final class SeenPeer {
        Peer peer;
        long lastSeen;
        long lastInvite = Long.MIN_VALUE / 2;

        SeenPeer(Peer peer) {
            this.peer = peer;
        }
    }

    private final Context context;
    private final Listener listener;
    private final byte[] announcement;
    private final Map<InetAddress, SeenPeer> seen = new LinkedHashMap<>();
    private final AtomicBoolean scanRequested = new AtomicBoolean(true);
    private final Thread worker = new Thread(this::run, "LinkTV discovery");
    private volatile boolean closed;
    private volatile DatagramSocket socket;
    private List<Peer> lastPublished = Collections.emptyList();

    Discovery(Context context, String name, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
        String base64Name = Base64.encodeToString(name.getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP);
        announcement = (TV_PREFIX + base64Name).getBytes(StandardCharsets.US_ASCII);
    }

    static String deviceName(Context context) {
        String name = Settings.Global.getString(context.getContentResolver(), "device_name");
        if (name == null || name.trim().isEmpty()) {
            name = Build.MODEL;
            if (name == null || name.trim().isEmpty()) {
                name = "Android TV";
            }
        }
        return cleanName(name, "Android TV");
    }

    void start() {
        worker.start();
    }

    void scan() {
        scanRequested.set(true);
    }

    @Override
    public void close() {
        closed = true;
        DatagramSocket current = socket;
        if (current != null) {
            current.close();
        }
        worker.interrupt();
    }

    private void run() {
        while (!closed) {
            try (DatagramSocket current = new DatagramSocket(null)) {
                socket = current;
                if (closed) {
                    return;
                }
                current.setBroadcast(true);
                current.setSoTimeout(SOCKET_WAIT_MS);
                current.bind(new InetSocketAddress(TV_UDP_PORT));
                listen(current);
            } catch (IOException error) {
                if (!closed) {
                    Log.w(LinkService.TAG, "Discovery socket unavailable: " + error.getClass().getSimpleName());
                    seen.clear();
                    publishPeers();
                }
            } finally {
                socket = null;
            }

            if (!closed) {
                try {
                    Thread.sleep(1000);
                } catch (InterruptedException error) {
                    return;
                }
            }
        }
    }

    private void listen(DatagramSocket current) throws IOException {
        DatagramPacket packet = new DatagramPacket(new byte[MAX_PACKET + 1], MAX_PACKET + 1);
        long nextAnnouncement = 0;
        long recentPeer = SystemClock.elapsedRealtime();
        WifiManager.MulticastLock wifiLock = null;
        try {
            while (!closed) {
                long now = SystemClock.elapsedRealtime();
                if (scanRequested.getAndSet(false) || now >= nextAnnouncement) {
                    sendAdvertisements(current);
                    nextAnnouncement = now + ANNOUNCE_MS;
                }
                expirePeers(now);
                if (wifiLock == null && now - recentPeer >= 2 * ANNOUNCE_MS) {
                    wifiLock = acquireWifiLock();
                    recentPeer = now;
                }
                try {
                    packet.setLength(packet.getData().length);
                    current.receive(packet);
                    if (readPeer(current, packet)) {
                        recentPeer = SystemClock.elapsedRealtime();
                    }
                } catch (SocketTimeoutException ignored) {
                    // The timeout lets announcements and expiry run without incoming traffic.
                }
            }
        } finally {
            if (wifiLock != null) {
                wifiLock.release();
            }
        }
    }

    private WifiManager.MulticastLock acquireWifiLock() {
        WifiManager wifi = (WifiManager) context.getSystemService(Context.WIFI_SERVICE);
        if (wifi == null) {
            return null;
        }
        try {
            WifiManager.MulticastLock lock = wifi.createMulticastLock("LinkTV-discovery");
            lock.setReferenceCounted(false);
            lock.acquire();
            return lock;
        } catch (RuntimeException error) {
            return null;
        }
    }

    private boolean readPeer(DatagramSocket current, DatagramPacket packet) {
        if (packet.getPort() != PC_UDP_PORT || !(packet.getAddress() instanceof Inet4Address)
                || packet.getLength() > MAX_PACKET) {
            return false;
        }
        String message = new String(packet.getData(), packet.getOffset(), packet.getLength(),
                StandardCharsets.US_ASCII);
        boolean invited = message.startsWith(INVITE_PREFIX);
        if (!invited && !message.startsWith(PC_PREFIX)) {
            return false;
        }
        int prefixLength = invited ? INVITE_PREFIX.length() : PC_PREFIX.length();
        String name;
        try {
            byte[] nameBytes = Base64.decode(message.substring(prefixLength), Base64.DEFAULT);
            if (nameBytes.length == 0 || nameBytes.length > 160) {
                return false;
            }
            name = cleanName(new String(nameBytes, StandardCharsets.UTF_8), "电脑");
        } catch (IllegalArgumentException invalid) {
            return false;
        }
        InetAddress address = packet.getAddress();
        Peer peer = new Peer(address, name);
        long now = SystemClock.elapsedRealtime();
        SeenPeer entry = seen.get(address);
        boolean newPeer = entry == null;
        if (newPeer) {
            if (seen.size() >= MAX_PEERS) {
                return false;
            }
            entry = new SeenPeer(peer);
            seen.put(address, entry);
        }
        boolean changed = newPeer || !peer.equals(entry.peer);
        entry.peer = peer;
        entry.lastSeen = now;
        if (changed) {
            publishPeers();
        }
        if (newPeer) {
            sendTo(current, address);
        }
        if (invited && now - entry.lastInvite >= INVITE_DEBOUNCE_MS) {
            entry.lastInvite = now;
            listener.onInvitation(peer);
        }
        return true;
    }

    private void expirePeers(long now) {
        boolean changed = false;
        Iterator<SeenPeer> entries = seen.values().iterator();
        while (entries.hasNext()) {
            if (now - entries.next().lastSeen >= EXPIRE_MS) {
                entries.remove();
                changed = true;
            }
        }
        if (changed) {
            publishPeers();
        }
    }

    private void publishPeers() {
        List<Peer> peers = new ArrayList<>();
        for (SeenPeer entry : seen.values()) {
            peers.add(entry.peer);
        }
        Collections.sort(peers, (first, second) -> {
            int nameOrder = first.name.compareToIgnoreCase(second.name);
            return nameOrder == 0
                    ? first.address.getHostAddress().compareTo(second.address.getHostAddress())
                    : nameOrder;
        });
        if (!lastPublished.equals(peers)) {
            lastPublished = Collections.unmodifiableList(peers);
            listener.onPeersChanged(lastPublished);
        }
    }

    private void sendAdvertisements(DatagramSocket current) {
        Set<InetAddress> targets = new LinkedHashSet<>();
        try {
            targets.add(InetAddress.getByAddress(new byte[] { -1, -1, -1, -1 }));
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface network = interfaces.nextElement();
                if (!network.isUp() || network.isLoopback()) {
                    continue;
                }
                for (InterfaceAddress local : network.getInterfaceAddresses()) {
                    if (local.getBroadcast() != null) {
                        targets.add(local.getBroadcast());
                    }
                }
            }
        } catch (UnknownHostException | SocketException ignored) {
            // An interface can disappear when Wi-Fi reconnects.
        }
        for (InetAddress target : targets) {
            sendTo(current, target);
        }
    }

    private void sendTo(DatagramSocket current, InetAddress address) {
        try {
            current.send(new DatagramPacket(announcement, announcement.length, address, PC_UDP_PORT));
        } catch (IOException ignored) {
            // A later announcement can use the new network route.
        }
    }

    private static String cleanName(String input, String fallback) {
        StringBuilder name = new StringBuilder();
        int byteCount = 0;
        for (int offset = 0; offset < input.length();) {
            int character = input.codePointAt(offset);
            offset += Character.charCount(character);
            if (Character.isISOControl(character) || Character.getType(character) == Character.FORMAT) {
                continue;
            }
            int utf8Length = character <= 0x7f ? 1 : character <= 0x7ff ? 2
                    : character <= 0xffff ? 3 : 4;
            if (byteCount + utf8Length > 72) {
                break;
            }
            name.appendCodePoint(character);
            byteCount += utf8Length;
        }
        String cleaned = name.toString().trim();
        return cleaned.isEmpty() ? fallback : cleaned;
    }
}
