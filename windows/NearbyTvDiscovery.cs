using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LinkTV
{
    internal sealed class NearbyTv
    {
        public NearbyTv(IPAddress address, string name)
        {
            Address = address;
            Name = name;
        }

        public IPAddress Address { get; private set; }
        public string Name { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class NearbyTvDiscovery : IDisposable
    {
        private const int ComputerPort = 17864;
        private const int TelevisionPort = 17865;
        private const int BroadcastIntervalMs = 2000;
        private const int PeerLifetimeMs = 8000;
        private const int MaxNameBytes = 96;
        private const string TelevisionPrefix = "LINKTV2|TV|";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly IPAddress localAddress;
        private readonly byte[] localSubnet;
        private readonly byte[] subnetMask;
        private readonly IPEndPoint broadcastTo;
        private readonly byte[] announcement;
        private readonly byte[] invitation;
        private readonly UdpClient socket;
        private volatile bool disposed;

        public NearbyTvDiscovery(IPAddress address, IPAddress mask, string displayName)
        {
            localAddress = address;
            subnetMask = mask.GetAddressBytes();
            localSubnet = address.GetAddressBytes();
            byte[] broadcastIp = new byte[4];
            for (int i = 0; i < localSubnet.Length; i++)
            {
                broadcastIp[i] = (byte)(localSubnet[i] | ~subnetMask[i]);
                localSubnet[i] &= subnetMask[i];
            }

            IPAddress directedBroadcast = new IPAddress(broadcastIp);
            broadcastTo = new IPEndPoint(directedBroadcast.Equals(address) ? IPAddress.Broadcast : directedBroadcast, TelevisionPort);
            string encodedName = Convert.ToBase64String(Utf8.GetBytes(displayName));
            announcement = Encoding.ASCII.GetBytes("LINKTV2|PC|" + encodedName);
            invitation = Encoding.ASCII.GetBytes("LINKTV2|INVITE|" + encodedName);

            socket = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                // Listening on all IPv4 interfaces is needed for broadcast receive. The subnet check below
                // accepts replies only from the LAN whose address is used for the TCP listener.
                socket.ExclusiveAddressUse = true;
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, ComputerPort));
                socket.EnableBroadcast = true;
                socket.Client.ReceiveTimeout = 400;
                socket.Client.SendTimeout = 500;
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        public event Action<NearbyTv[]> DevicesChanged;

        public void Start()
        {
            Thread worker = new Thread(FindTelevisions);
            worker.Name = "LinkTV nearby devices";
            worker.IsBackground = true;
            worker.Start();
        }

        public bool Invite(NearbyTv device)
        {
            return TrySend(invitation, new IPEndPoint(device.Address, TelevisionPort));
        }

        private void FindTelevisions()
        {
            Dictionary<IPAddress, Observation> observed = new Dictionary<IPAddress, Observation>();
            Stopwatch age = Stopwatch.StartNew();
            long announceAt = 0;

            while (!disposed)
            {
                long now = age.ElapsedMilliseconds;
                if (now >= announceAt)
                {
                    if (!TrySend(announcement, broadcastTo) && !broadcastTo.Address.Equals(IPAddress.Broadcast))
                    {
                        TrySend(announcement, new IPEndPoint(IPAddress.Broadcast, TelevisionPort));
                    }
                    announceAt = now + BroadcastIntervalMs;
                }

                bool changed = false;
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    byte[] packet = socket.Receive(ref sender);
                    string peerName = ReadAdvertisement(packet, sender);
                    if (peerName != null)
                    {
                        Observation earlier;
                        bool firstSeen = !observed.TryGetValue(sender.Address, out earlier);
                        changed = firstSeen || earlier.Device.Name != peerName;
                        if (firstSeen)
                        {
                            TrySend(announcement, new IPEndPoint(sender.Address, TelevisionPort));
                        }

                        NearbyTv device = changed ? new NearbyTv(sender.Address, peerName) : earlier.Device;
                        observed[sender.Address] = new Observation(device, age.ElapsedMilliseconds);
                    }
                }
                catch (SocketException error)
                {
                    if (error.SocketErrorCode != SocketError.TimedOut && !disposed)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                now = age.ElapsedMilliseconds;
                IPAddress[] expired = observed.Where(entry => now - entry.Value.SeenAfterMs >= PeerLifetimeMs)
                    .Select(entry => entry.Key).ToArray();
                foreach (IPAddress address in expired)
                {
                    observed.Remove(address);
                }

                Action<NearbyTv[]> callback = DevicesChanged;
                if (!disposed && callback != null && (changed || expired.Length != 0))
                {
                    NearbyTv[] current = observed.Values.Select(value => value.Device)
                        .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(device => device.Address.ToString(), StringComparer.Ordinal)
                        .ToArray();
                    callback(current);
                }
            }
        }

        private string ReadAdvertisement(byte[] packet, IPEndPoint sender)
        {
            if (sender.Port != TelevisionPort || sender.Address.Equals(localAddress) || !IsOnLocalSubnet(sender.Address)
                || packet.Length <= TelevisionPrefix.Length || packet.Length > TelevisionPrefix.Length + MaxNameBytes * 2)
            {
                return null;
            }

            string text = Encoding.ASCII.GetString(packet);
            if (!text.StartsWith(TelevisionPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                byte[] nameBytes = Convert.FromBase64String(text.Substring(TelevisionPrefix.Length));
                if (nameBytes.Length > MaxNameBytes)
                {
                    return null;
                }

                string name = Utf8.GetString(nameBytes).Trim();
                return name.Length == 0 || name.Any(char.IsControl) ? null : name;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private bool IsOnLocalSubnet(IPAddress candidate)
        {
            byte[] address = candidate.GetAddressBytes();
            for (int i = 0; i < localSubnet.Length; i++)
            {
                if ((address[i] & subnetMask[i]) != localSubnet[i])
                {
                    return false;
                }
            }
            return true;
        }

        private bool TrySend(byte[] packet, IPEndPoint destination)
        {
            try
            {
                return !disposed && socket.Send(packet, packet.Length, destination) == packet.Length;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            disposed = true;
            socket.Close();
        }

        private sealed class Observation
        {
            public Observation(NearbyTv device, long seenAfterMs)
            {
                Device = device;
                SeenAfterMs = seenAfterMs;
            }

            public NearbyTv Device { get; private set; }
            public long SeenAfterMs { get; private set; }
        }
    }
}
