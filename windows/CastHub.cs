using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkTV
{
    internal sealed class CastHub : IDisposable
    {
        private const byte StartCasting = 0x10;
        private const byte StopCasting = 0x11;
        private const byte JpegFrame = 0x12;
        private const byte AudioFrame = 0x13;
        private const byte StopRequest = 0x14;
        private const byte CapabilitiesPacket = 0x15;
        private const byte H264Begin = 0x16;
        private const byte H264Frame = 0x17;
        public const byte H264Send = 0x01;
        public const byte H264Receive = 0x02;
        private const byte CapabilityMask = H264Send | H264Receive;
        private const int H264FrameHeaderSize = 9;
        private const byte AcceptPairing = 0x01;
        private const byte RejectPairing = 0x02;
        private const int NonceSize = 32;
        private const int PublicKeySize = 64;
        private const int MaxNameSize = 96;
        private const int MacSize = 32;
        private const int IvSize = 16;
        private const int MaxBodySize = 2 * 1024 * 1024;
        private const int MaxJpegSize = MaxBodySize - IvSize - MacSize - 2;
        public const int MaxH264AccessUnitSize = MaxJpegSize - H264FrameHeaderSize;
        private const int PcmSampleFrameBytes = 4;
        private const int MaxAudioSize = 16384;
        private const int SetupTimeoutMs = 8000;
        private const int CompareTimeoutMs = 75000;
        private const int SendTimeoutMs = 2000;
        private const string ProtocolV2 = "LINKTV2";
        private const string ProtocolV3 = "LINKTV3";
        private static readonly byte[] MagicV2 = Encoding.ASCII.GetBytes(ProtocolV2);
        private static readonly byte[] MagicV3 = Encoding.ASCII.GetBytes(ProtocolV3);
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] Empty = new byte[0];

        private readonly object stateLock = new object();
        private readonly IPAddress bindAddress;
        private readonly int port;
        private readonly byte[] localName;
        private readonly byte localCapabilities;
        private TcpListener listener;
        private TcpClient currentClient;
        private Session activeSession;
        private PairingAttempt pendingPairing;
        private bool disposed;

        public CastHub(IPAddress bindAddress, int port, string localName, byte localCapabilities = 0)
        {
            if (bindAddress == null || bindAddress.AddressFamily != AddressFamily.InterNetwork ||
                bindAddress.Equals(IPAddress.Any))
            {
                throw new ArgumentException("Specify a local IPv4 address.", "bindAddress");
            }

            this.bindAddress = bindAddress;
            if ((localCapabilities & ~CapabilityMask) != 0)
                throw new ArgumentOutOfRangeException("localCapabilities");

            this.port = port;
            this.localCapabilities = localCapabilities;
            string name = (localName ?? string.Empty).Trim();
            this.localName = IsValidName(name) ? Utf8.GetBytes(name) : Utf8.GetBytes("Windows");
        }

        public bool IsConnected
        {
            get
            {
                lock (stateLock)
                {
                    return !disposed && activeSession != null && activeSession.IsOpen;
                }
            }
        }

        public bool CanSendH264
        {
            get
            {
                Session session = GetActiveSession();
                return session != null && session.IsOpen && session.CanSendH264;
            }
        }

        public bool CanReceiveH264
        {
            get
            {
                Session session = GetActiveSession();
                return session != null && session.IsOpen && session.CanReceiveH264;
            }
        }

        public event Action<string, string> PairingRequested;
        public event Action<bool> PairingFinished;
        public event Action<bool> ConnectionChanged;
        public event Action<bool> RemoteCastingChanged;
        public event Action RemoteStopRequested;
        public event Action<byte[]> JpegReceived;
        public event Action<byte[]> AudioReceived;
        public event Action<int, int, int> H264Started;
        public event Action<long, bool, byte[]> H264FrameReceived;

        public void Start()
        {
            lock (stateLock)
            {
                if (disposed)
                    throw new ObjectDisposedException("CastHub");
                if (listener != null)
                    return;

                var socket = new TcpListener(bindAddress, port);
                socket.ExclusiveAddressUse = true;
                socket.Start(3);
                listener = socket;

                var worker = new Thread(() => AcceptClients(socket));
                worker.Name = "LinkTV listener";
                worker.IsBackground = true;
                worker.Start();
            }
        }

        public bool RespondToPairing(bool accepted)
        {
            PairingAttempt attempt;
            lock (stateLock)
            {
                attempt = disposed ? null : pendingPairing;
            }

            return attempt != null && attempt.Respond(accepted);
        }

        public void Disconnect()
        {
            lock (stateLock)
            {
                if (activeSession != null)
                    activeSession.Close();
                else if (pendingPairing != null)
                    pendingPairing.Cancel();
                else if (currentClient != null)
                    currentClient.Close();
            }
        }

        public bool SendStart()
        {
            return Send(StartCasting, Empty);
        }

        public bool SendStop()
        {
            return Send(StopCasting, Empty);
        }

        public bool SendJpeg(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0 || jpeg.Length > MaxJpegSize)
                return false;

            return Send(JpegFrame, jpeg);
        }

        public bool SendAudio(byte[] pcm)
        {
            return pcm != null && IsAudioLengthValid(pcm.Length) && Send(AudioFrame, pcm);
        }

        public bool SendH264Begin(int width, int height, int fps)
        {
            if (!ValidH264Format(width, height, fps))
                return false;

            byte[] payload = new byte[6];
            WriteBigEndian((uint)width, payload, 0, 2);
            WriteBigEndian((uint)height, payload, 2, 2);
            WriteBigEndian((uint)fps, payload, 4, 2);
            return Send(H264Begin, payload);
        }

        public bool SendH264Frame(long ptsUs, bool keyFrame, byte[] accessUnit)
        {
            if (ptsUs < 0 || accessUnit == null || accessUnit.Length > MaxH264AccessUnitSize ||
                !HasAnnexBStartCode(accessUnit, 0))
                return false;

            var payload = new byte[H264FrameHeaderSize + accessUnit.Length];
            WriteBigEndian((ulong)ptsUs, payload, 0, 8);
            payload[8] = keyFrame ? (byte)1 : (byte)0;
            Buffer.BlockCopy(accessUnit, 0, payload, H264FrameHeaderSize, accessUnit.Length);
            return Send(H264Frame, payload);
        }

        // Signed 16-bit little-endian stereo at 44.1 kHz.
        private static bool IsAudioLengthValid(int size)
        {
            return size > 0 && size <= MaxAudioSize && size % PcmSampleFrameBytes == 0;
        }

        private bool Send(byte type, byte[] payload)
        {
            Session session = GetActiveSession();
            return session != null && session.Send(type, payload);
        }

        private Session GetActiveSession()
        {
            lock (stateLock)
            {
                return disposed ? null : activeSession;
            }
        }

        private static bool ValidH264Format(int width, int height, int fps)
        {
            return width >= 2 && height >= 2 && width % 2 == 0 && height % 2 == 0 &&
                Math.Max(width, height) <= 1920 && Math.Min(width, height) <= 1080 && fps >= 1 && fps <= 60;
        }

        private static bool HasAnnexBStartCode(byte[] data, int offset)
        {
            int remaining = data.Length - offset;
            return remaining >= 4 && data[offset] == 0 && data[offset + 1] == 0 &&
                (data[offset + 2] == 1 ||
                    (remaining >= 5 && data[offset + 2] == 0 && data[offset + 3] == 1));
        }

        private static int ReadUInt16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            ulong value = 0;
            for (int index = 0; index < 8; index++)
                value = (value << 8) | data[offset + index];

            return value;
        }

        private void AcceptClients(TcpListener socket)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = socket.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                lock (stateLock)
                {
                    if (disposed)
                    {
                        client.Close();
                        return;
                    }

                    currentClient = client;
                }

                RunConnection(client);
            }
        }

        private void RunConnection(TcpClient client)
        {
            Session session = null;
            var remoteCasting = false;
            var remoteH264 = false;
            long lastRemotePts = -1;
            var pairingShown = false;
            var pairingSucceeded = false;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = SetupTimeoutMs;
                client.SendTimeout = SendTimeoutMs;
                EnableKeepAlive(client.Client);
                string peerName;
                string comparisonCode;
                session = ExchangeKeys(client, out peerName, out comparisonCode);
                var attempt = new PairingAttempt(session);

                lock (stateLock)
                {
                    if (disposed)
                        return;

                    pendingPairing = attempt;
                    pairingShown = true;
                }

                var requested = PairingRequested;
                if (requested != null)
                    Publish(() => requested(peerName, comparisonCode));

                byte[] confirmation = session.Receive(attempt.Timer, CompareTimeoutMs);
                if (confirmation.Length != 1)
                    throw new InvalidDataException("Unexpected pairing confirmation payload.");
                if (confirmation[0] == RejectPairing)
                    return;
                if (confirmation[0] != AcceptPairing)
                    throw new InvalidDataException("Expected pairing confirmation.");
                if (!attempt.WaitForAcceptance() || !ExchangeCapabilities(session))
                    return;

                lock (stateLock)
                {
                    if (disposed || !session.IsOpen)
                        return;

                    pendingPairing = null;
                    activeSession = session;
                    pairingSucceeded = true;
                }

                Publish(PairingFinished, true);
                Publish(ConnectionChanged, true);

                while (session.IsOpen)
                {
                    byte[] message = session.Receive();
                    switch (message[0])
                    {
                        case StartCasting:
                        case StopCasting:
                            if (message.Length != 1)
                                throw new InvalidDataException("Unexpected casting command payload.");

                            bool starting = message[0] == StartCasting;
                            if (remoteCasting != starting)
                            {
                                remoteCasting = starting;
                                remoteH264 = false;
                                lastRemotePts = -1;
                                Publish(RemoteCastingChanged, starting);
                            }
                            break;

                        case StopRequest:
                            if (message.Length != 1)
                                throw new InvalidDataException("Unexpected stop request payload.");

                            Publish(RemoteStopRequested);
                            break;

                        case H264Begin:
                            if (!session.CanReceiveH264 || !remoteCasting || remoteH264 || message.Length != 7)
                                throw new InvalidDataException("Unexpected H.264 start.");

                            int width = ReadUInt16(message, 1);
                            int height = ReadUInt16(message, 3);
                            int fps = ReadUInt16(message, 5);
                            if (!ValidH264Format(width, height, fps))
                                throw new InvalidDataException("Invalid H.264 format.");

                            remoteH264 = true;
                            var videoStarted = H264Started;
                            if (videoStarted != null)
                                Publish(() => videoStarted(width, height, fps));
                            break;

                        case H264Frame:
                            int accessUnitOffset = 1 + H264FrameHeaderSize;
                            int accessUnitLength = message.Length - accessUnitOffset;
                            if (!session.CanReceiveH264 || !remoteCasting || !remoteH264 ||
                                accessUnitLength > MaxH264AccessUnitSize ||
                                !HasAnnexBStartCode(message, accessUnitOffset) || message[9] > 1)
                                throw new InvalidDataException("Invalid H.264 access unit.");

                            ulong timestamp = ReadUInt64(message, 1);
                            bool keyFrame = message[9] == 1;
                            if (timestamp > long.MaxValue || (long)timestamp <= lastRemotePts ||
                                (lastRemotePts < 0 && !keyFrame))
                                throw new InvalidDataException("Invalid H.264 frame sequence.");

                            lastRemotePts = (long)timestamp;
                            var videoFrame = H264FrameReceived;
                            if (videoFrame != null)
                            {
                                var accessUnit = new byte[accessUnitLength];
                                Buffer.BlockCopy(message, accessUnitOffset, accessUnit, 0, accessUnitLength);
                                Publish(() => videoFrame((long)timestamp, keyFrame, accessUnit));
                            }
                            break;

                        case JpegFrame:
                        case AudioFrame:
                            bool isAudio = message[0] == AudioFrame;
                            if (!isAudio && remoteH264)
                                throw new InvalidDataException("JPEG frame within an H.264 stream.");
                            int payloadSize = message.Length - 1;
                            if (isAudio && !IsAudioLengthValid(payloadSize))
                                throw new InvalidDataException("Invalid PCM payload size.");

                            var mediaReceiver = isAudio ? AudioReceived : JpegReceived;
                            if (mediaReceiver != null && remoteCasting && payloadSize > 0)
                            {
                                var payload = new byte[payloadSize];
                                Buffer.BlockCopy(message, 1, payload, 0, payloadSize);
                                Publish(mediaReceiver, payload);
                            }
                            break;

                        default:
                            throw new InvalidDataException("Unsupported frame type.");
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                Trace.WriteLine("LinkTV rejected a protocol message: " + ex.Message);
            }
            catch (IOException ex)
            {
                Trace.WriteLine("LinkTV connection closed: " + ex.Message);
            }
            catch (SocketException ex)
            {
                Trace.WriteLine("LinkTV socket closed: " + ex.Message);
            }
            catch (ObjectDisposedException)
            {
                // Dispose may close the socket while this thread is reading.
            }
            catch (CryptographicException ex)
            {
                Trace.WriteLine("LinkTV rejected an unauthenticated frame: " + ex.Message);
            }
            finally
            {
                if (session != null)
                    session.Close();
                else
                    client.Close();

                bool wasActive;
                lock (stateLock)
                {
                    currentClient = null;
                    pendingPairing = null;
                    wasActive = activeSession != null && activeSession == session;
                    if (wasActive)
                        activeSession = null;
                }

                if (pairingShown && !pairingSucceeded)
                    Publish(PairingFinished, false);

                if (wasActive)
                {
                    if (remoteCasting)
                        Publish(RemoteCastingChanged, false);

                    Publish(ConnectionChanged, false);
                }
            }
        }

        private bool ExchangeCapabilities(Session session)
        {
            if (!session.HasCapabilities)
                return true;

            if (!session.Send(CapabilitiesPacket, new[] { localCapabilities }))
                return false;

            byte[] packet = session.Receive(Stopwatch.StartNew());
            if (packet.Length != 2 || packet[0] != CapabilitiesPacket || (packet[1] & ~CapabilityMask) != 0)
                throw new InvalidDataException("Invalid peer capabilities.");

            session.SetCapabilities(localCapabilities, packet[1]);
            return true;
        }

        private Session ExchangeKeys(TcpClient client, out string peerName, out string comparisonCode)
        {
            NetworkStream stream = client.GetStream();
            var timer = Stopwatch.StartNew();
            byte[] magic = ReadExactly(stream, MagicV3.Length, timer);
            string protocol;
            if (FixedEquals(MagicV3, magic, 0))
                protocol = ProtocolV3;
            else if (FixedEquals(MagicV2, magic, 0))
                protocol = ProtocolV2;
            else
                throw new InvalidDataException("Unsupported protocol.");

            var clientHello = PairingHello.Read(stream, timer);
            peerName = clientHello.Name;
            byte[] serverNonce = RandomBytes(NonceSize);

            using (var ecdh = new ECDiffieHellmanCng(256))
            {
                ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
                ecdh.HashAlgorithm = CngAlgorithm.Sha256;
                byte[] publicBlob = ecdh.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                var coordinates = new byte[PublicKeySize];
                Buffer.BlockCopy(publicBlob, 8, coordinates, 0, coordinates.Length);
                byte[] serverIdentity = PairingHello.EncodeIdentity(coordinates, localName);
                byte[] serverCommitment = Commitment(protocol, "S", serverIdentity, serverNonce);
                byte[] serverHello = JoinBytes(serverIdentity, serverCommitment);
                stream.Write(serverHello, 0, serverHello.Length);

                byte[] clientNonce = ReadExactly(stream, NonceSize, timer);
                byte[] clientCommitment = Commitment(protocol, "C", clientHello.Identity, clientNonce);
                if (!FixedEquals(clientCommitment, clientHello.Commitment, 0))
                    throw new InvalidDataException("Pairing commitment mismatch.");

                stream.Write(serverNonce, 0, serverNonce.Length);
                byte[] clientBlob = JoinBytes(new byte[] { 0x45, 0x43, 0x4b, 0x31, 32, 0, 0, 0 },
                    clientHello.Coordinates);
                byte[] sharedMaterial;
                using (CngKey peerPublicKey = CngKey.Import(clientBlob, CngKeyBlobFormat.EccPublicBlob))
                    sharedMaterial = ecdh.DeriveKeyMaterial(peerPublicKey);

                byte[] transcript = Hash(JoinBytes(magic, clientHello.Identity, clientHello.Commitment,
                    serverHello, clientNonce, serverNonce));
                byte[] sessionKey = Hmac(transcript, sharedMaterial);
                Array.Clear(sharedMaterial, 0, sharedMaterial.Length);
                uint code = ReadUInt32(Derive(protocol, sessionKey, "compare")) % 1000000u;
                comparisonCode = code.ToString("D6", CultureInfo.InvariantCulture);
                var session = new Session(client, stream, protocol == ProtocolV3,
                    Derive(protocol, sessionKey, "ce"), Derive(protocol, sessionKey, "cm"),
                    Derive(protocol, sessionKey, "se"), Derive(protocol, sessionKey, "sm"));
                Array.Clear(sessionKey, 0, sessionKey.Length);
                return session;
            }
        }

        private static byte[] Commitment(string protocol, string role, byte[] identity, byte[] nonce)
        {
            return Hash(JoinBytes(Encoding.ASCII.GetBytes(protocol + " commit " + role), identity, nonce));
        }

        private static byte[] Derive(string protocol, byte[] key, string label)
        {
            return Hmac(key, Encoding.ASCII.GetBytes(protocol + " " + label + "\u0001"));
        }

        private static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(data);
        }

        private static byte[] Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        private static byte[] JoinBytes(params byte[][] parts)
        {
            int length = 0;
            foreach (byte[] part in parts)
                length += part.Length;

            var result = new byte[length];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private static bool IsValidName(string name)
        {
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > MaxNameSize)
                return false;

            foreach (char character in name)
            {
                if (char.IsControl(character))
                    return false;
            }

            return true;
        }

        private sealed class PairingHello
        {
            public readonly byte[] Coordinates;
            public readonly string Name;
            public readonly byte[] Identity;
            public readonly byte[] Commitment;

            private PairingHello(byte[] coordinates, string name, byte[] identity, byte[] commitment)
            {
                Coordinates = coordinates;
                Name = name;
                Identity = identity;
                Commitment = commitment;
            }

            public static byte[] EncodeIdentity(byte[] coordinates, byte[] nameBytes)
            {
                return JoinBytes(coordinates, new byte[] { (byte)nameBytes.Length }, nameBytes);
            }

            public static PairingHello Read(Stream stream, Stopwatch timer)
            {
                byte[] coordinates = ReadExactly(stream, PublicKeySize, timer);
                int nameLength = ReadExactly(stream, 1, timer)[0];
                if (nameLength == 0 || nameLength > MaxNameSize)
                    throw new InvalidDataException("Invalid pairing name length.");

                byte[] nameBytes = ReadExactly(stream, nameLength, timer);
                byte[] commitment = ReadExactly(stream, MacSize, timer);
                string name;
                try
                {
                    name = Utf8.GetString(nameBytes);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException("Invalid pairing name encoding.", ex);
                }

                if (!IsValidName(name))
                    throw new InvalidDataException("Invalid pairing name.");

                return new PairingHello(coordinates, name, EncodeIdentity(coordinates, nameBytes), commitment);
            }
        }

        private sealed class PairingAttempt
        {
            private readonly Session session;
            public readonly Stopwatch Timer = Stopwatch.StartNew();
            private readonly TaskCompletionSource<bool> localDecision = new TaskCompletionSource<bool>();
            private int responded;

            public PairingAttempt(Session session)
            {
                this.session = session;
            }

            public bool Respond(bool accepted)
            {
                if (!session.IsOpen || Timer.ElapsedMilliseconds >= CompareTimeoutMs ||
                    Interlocked.CompareExchange(ref responded, 1, 0) != 0)
                {
                    return false;
                }

                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool sent = session.Send(accepted ? AcceptPairing : RejectPairing, Empty);
                    localDecision.TrySetResult(sent && accepted);
                    if (!sent || !accepted)
                        session.Close();
                });
                return true;
            }

            public bool WaitForAcceptance()
            {
                while (true)
                {
                    int remaining = CompareTimeoutMs - (int)Timer.ElapsedMilliseconds;
                    if (remaining <= 0 || session.HasPeerClosed())
                        return false;

                    if (localDecision.Task.Wait(Math.Min(remaining, 250)))
                        return localDecision.Task.Result;
                }
            }

            public void Cancel()
            {
                session.Close();
                localDecision.TrySetResult(false);
            }
        }
        private static void EnableKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var options = new byte[12];
                Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, options, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(5000u), 0, options, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(1000u), 0, options, 8, 4);
                socket.IOControl(IOControlCode.KeepAliveValues, options, null);
            }
            catch (SocketException ex)
            {
                Trace.WriteLine("LinkTV could not adjust TCP keep-alive: " + ex.Message);
            }
        }

        private static void Publish<T>(Action<T> callback, T value)
        {
            if (callback != null)
                Publish(() => callback(value));
        }

        private static void Publish(Action callback)
        {
            if (callback == null)
                return;

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("LinkTV observer failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                    return;

                disposed = true;
                if (listener != null)
                    listener.Stop();

                Disconnect();
            }
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        private static byte[] ReadExactly(Stream stream, int count, Stopwatch timer = null, int timeoutMs = SetupTimeoutMs)
        {
            var bytes = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                if (timer != null)
                {
                    int timeLeft = timeoutMs - (int)timer.ElapsedMilliseconds;
                    if (timeLeft <= 0)
                        throw new IOException("Pairing timed out.");

                    stream.ReadTimeout = timeLeft;
                }

                int read = stream.Read(bytes, offset, count - offset);
                if (read == 0)
                    throw new EndOfStreamException();

                offset += read;
            }

            return bytes;
        }

        private static bool FixedEquals(byte[] expected, byte[] actual, int offset)
        {
            int differences = 0;
            for (int i = 0; i < expected.Length; i++)
                differences |= expected[i] ^ actual[offset + i];

            return differences == 0;
        }

        private static uint ReadUInt32(byte[] bytes)
        {
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) | bytes[3];
        }

        private static void WriteBigEndian(ulong value, byte[] bytes, int offset, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bytes[offset + i] = (byte)value;
                value >>= 8;
            }
        }

        private static byte[] ComputeFrameMac(byte[] key, ulong sequence, uint length,
            byte[] data, int dataOffset, int dataLength)
        {
            var prefix = new byte[12];
            WriteBigEndian(sequence, prefix, 0, 8);
            WriteBigEndian(length, prefix, 8, 4);

            using (var hmac = new HMACSHA256(key))
            {
                hmac.TransformBlock(prefix, 0, prefix.Length, prefix, 0);
                hmac.TransformBlock(data, dataOffset, dataLength, data, dataOffset);
                hmac.TransformFinalBlock(Empty, 0, 0);
                return hmac.Hash;
            }
        }

        private sealed class Session
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            private readonly byte[] receiveKey;
            private readonly byte[] receiveMac;
            private readonly byte[] sendKey;
            private readonly byte[] sendMac;
            private readonly object sendLock = new object();
            private ulong receiveSequence;
            private ulong sendSequence;
            private int closed;
            private bool localCasting;
            private bool localH264;
            private long lastLocalPts = -1;

            public bool HasCapabilities { get; private set; }
            public bool CanSendH264 { get; private set; }
            public bool CanReceiveH264 { get; private set; }

            public Session(TcpClient client, NetworkStream stream, bool hasCapabilities,
                byte[] receiveKey, byte[] receiveMac, byte[] sendKey, byte[] sendMac)
            {
                this.client = client;
                this.stream = stream;
                HasCapabilities = hasCapabilities;
                this.receiveKey = receiveKey;
                this.receiveMac = receiveMac;
                this.sendKey = sendKey;
                this.sendMac = sendMac;
            }

            public void SetCapabilities(byte local, byte remote)
            {
                CanSendH264 = (local & H264Send) != 0 && (remote & H264Receive) != 0;
                CanReceiveH264 = (local & H264Receive) != 0 && (remote & H264Send) != 0;
            }

            public bool IsOpen
            {
                get { return Volatile.Read(ref closed) == 0; }
            }

            public void Close()
            {
                if (Interlocked.Exchange(ref closed, 1) == 0)
                    client.Close();
            }

            public bool HasPeerClosed()
            {
                Socket socket = client.Client;
                try
                {
                    return !IsOpen || socket == null ||
                        (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
                }
                catch (SocketException)
                {
                    return true;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }

            public byte[] Receive(Stopwatch timer = null, int timeoutMs = SetupTimeoutMs)
            {
                if (timer == null && stream.ReadTimeout != Timeout.Infinite)
                    stream.ReadTimeout = Timeout.Infinite;

                const int minBodySize = IvSize + 16 + MacSize;
                int maxSize = timer == null ? MaxBodySize : minBodySize;
                uint length = ReadUInt32(ReadExactly(stream, 4, timer, timeoutMs));
                if (length < minBodySize || length > maxSize || length % 16 != 0)
                    throw new InvalidDataException("Invalid encrypted frame size.");

                byte[] body = ReadExactly(stream, (int)length, timer, timeoutMs);
                int macOffset = body.Length - MacSize;
                byte[] expectedMac = ComputeFrameMac(receiveMac, receiveSequence, length, body, 0, macOffset);
                if (!FixedEquals(expectedMac, body, macOffset))
                    throw new InvalidDataException("Invalid encrypted frame signature.");

                var iv = new byte[IvSize];
                Buffer.BlockCopy(body, 0, iv, 0, iv.Length);
                using (var aes = Aes.Create())
                using (var decryptor = aes.CreateDecryptor(receiveKey, iv))
                {
                    byte[] cleartext = decryptor.TransformFinalBlock(body, IvSize, macOffset - IvSize);
                    if (cleartext.Length == 0)
                        throw new InvalidDataException("Missing encrypted frame type.");

                    receiveSequence++;
                    return cleartext;
                }
            }

            public bool Send(byte type, byte[] payload)
            {
                lock (sendLock)
                {
                    if (!IsOpen || !CanSend(type, payload))
                        return false;

                    if ((type == StartCasting && localCasting) || (type == StopCasting && !localCasting))
                        return true;

                    try
                    {
                        WriteFrame(type, payload);
                        if (type == StartCasting || type == StopCasting)
                        {
                            localCasting = type == StartCasting;
                            localH264 = false;
                            lastLocalPts = -1;
                        }
                        else if (type == H264Begin)
                            localH264 = true;
                        else if (type == H264Frame)
                            lastLocalPts = (long)ReadUInt64(payload, 0);

                        return true;
                    }
                    catch (IOException)
                    {
                        Close();
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        Close();
                        return false;
                    }
                    catch (CryptographicException)
                    {
                        Close();
                        return false;
                    }
                }
            }

            private bool CanSend(byte type, byte[] payload)
            {
                switch (type)
                {
                    case JpegFrame:
                        return localCasting && !localH264;
                    case AudioFrame:
                        return localCasting;
                    case H264Begin:
                        return CanSendH264 && localCasting && !localH264;
                    case H264Frame:
                        return CanSendH264 && localCasting && localH264 &&
                            (long)ReadUInt64(payload, 0) > lastLocalPts &&
                            (lastLocalPts >= 0 || payload[8] == 1);
                    default:
                        return true;
                }
            }

            private void WriteFrame(byte type, byte[] payload)
            {
                var cleartext = new byte[1 + payload.Length];
                cleartext[0] = type;
                Buffer.BlockCopy(payload, 0, cleartext, 1, payload.Length);

                byte[] iv = RandomBytes(IvSize);
                byte[] ciphertext;
                using (var aes = Aes.Create())
                using (var encryptor = aes.CreateEncryptor(sendKey, iv))
                    ciphertext = encryptor.TransformFinalBlock(cleartext, 0, cleartext.Length);

                int length = iv.Length + ciphertext.Length + MacSize;
                var packet = new byte[4 + length];
                WriteBigEndian((ulong)length, packet, 0, 4);
                Buffer.BlockCopy(iv, 0, packet, 4, iv.Length);
                Buffer.BlockCopy(ciphertext, 0, packet, 4 + iv.Length, ciphertext.Length);
                int macOffset = packet.Length - MacSize;
                byte[] mac = ComputeFrameMac(sendMac, sendSequence, (uint)length, packet, 4, macOffset - 4);
                Buffer.BlockCopy(mac, 0, packet, macOffset, mac.Length);
                stream.Write(packet, 0, packet.Length);
                sendSequence++;
            }
        }
    }
}
