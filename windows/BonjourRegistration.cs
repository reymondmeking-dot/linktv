using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace LinkTV
{
    internal sealed class BonjourRegistration : IDisposable
    {
        private const uint RequestPending = 9506;
        private static readonly CompleteCallback RegisterCallback = OnRegistered;
        private static readonly CompleteCallback DeregisterCallback = OnDeregistered;
        private readonly object gate = new object();
        private RegisterRequest request;
        private GCHandle context;
        private bool registered;
        private bool disposed;

        private BonjourRegistration(IntPtr instance, uint interfaceIndex)
        {
            request = new RegisterRequest
            {
                Version = 1,
                InterfaceIndex = interfaceIndex,
                Instance = instance,
                Completion = Marshal.GetFunctionPointerForDelegate(RegisterCallback),
                Credentials = IntPtr.Zero,
                UnicastEnabled = 0
            };
        }

        public static BonjourRegistration TryStart(IPAddress address, uint interfaceIndex, string displayName, int port)
        {
            if (interfaceIndex == 0)
            {
                return null;
            }

            try
            {
                string name = displayName.Replace(".", "-").Replace("\\", "-") + "._linktv._tcp.local";
                string hostLabel = new IdnMapping().GetAscii(Environment.MachineName.Replace('_', '-')).ToLowerInvariant();
                // Windows advertises address records for its own mDNS hostname.
                string hostname = hostLabel + ".local";
                uint ip4 = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
                IntPtr instance = DnsServiceConstructInstance(name, hostname, ref ip4, IntPtr.Zero, (ushort)port,
                    0, 0, 0, IntPtr.Zero, IntPtr.Zero);
                if (instance == IntPtr.Zero)
                {
                    return null;
                }

                BonjourRegistration service = new BonjourRegistration(instance, interfaceIndex);
                return service.Register() ? service : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch (ArgumentException e)
            {
                Trace.TraceWarning("LinkTV Bonjour: " + e.Message);
                return null;
            }
        }

        private bool Register()
        {
            lock (gate)
            {
                context = GCHandle.Alloc(this);
                request.Context = GCHandle.ToIntPtr(context);
                uint status = DnsServiceRegister(ref request, IntPtr.Zero);
                if (status == RequestPending)
                {
                    return true;
                }

                Trace.TraceWarning("LinkTV Bonjour registration returned " + status);
                Release();
                return false;
            }
        }

        private static void OnRegistered(uint status, IntPtr context, IntPtr result)
        {
            BonjourRegistration service = (BonjourRegistration)GCHandle.FromIntPtr(context).Target;
            lock (service.gate)
            {
                service.FreeResult(result);
                service.registered = status == 0;
                if (!service.registered)
                {
                    Trace.TraceWarning("LinkTV Bonjour registration completed with " + status);
                    service.Release();
                }
                else if (service.disposed)
                {
                    service.BeginDeregister();
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (registered)
                {
                    BeginDeregister();
                }
                // A registration in flight will be removed by its completion callback.
            }
        }

        private void BeginDeregister()
        {
            registered = false;
            request.Completion = Marshal.GetFunctionPointerForDelegate(DeregisterCallback);
            uint status = DnsServiceDeRegister(ref request, IntPtr.Zero);
            if (status != RequestPending)
            {
                Trace.TraceWarning("LinkTV Bonjour deregistration returned " + status);
                Release();
            }
        }

        private static void OnDeregistered(uint status, IntPtr context, IntPtr result)
        {
            BonjourRegistration service = (BonjourRegistration)GCHandle.FromIntPtr(context).Target;
            lock (service.gate)
            {
                service.FreeResult(result);
                if (status != 0)
                {
                    Trace.TraceWarning("LinkTV Bonjour deregistration completed with " + status);
                }
                service.Release();
            }
        }

        private void FreeResult(IntPtr result)
        {
            if (result != IntPtr.Zero && result != request.Instance)
            {
                DnsServiceFreeInstance(result);
            }
        }

        private void Release()
        {
            DnsServiceFreeInstance(request.Instance);
            request.Instance = IntPtr.Zero;
            context.Free();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CompleteCallback(uint status, IntPtr context, IntPtr instance);

        [StructLayout(LayoutKind.Sequential)]
        private struct RegisterRequest
        {
            public uint Version;
            public uint InterfaceIndex;
            public IntPtr Instance;
            public IntPtr Completion;
            public IntPtr Context;
            public IntPtr Credentials;
            public int UnicastEnabled;
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr DnsServiceConstructInstance(string service, string hostname, ref uint ipv4,
            IntPtr ipv6, ushort port, ushort priority, ushort weight, uint properties, IntPtr keys, IntPtr values);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern uint DnsServiceRegister(ref RegisterRequest request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern uint DnsServiceDeRegister(ref RegisterRequest request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern void DnsServiceFreeInstance(IntPtr instance);
    }
}
