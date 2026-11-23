using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinkTV
{
    public static class WindowsAudio
    {
        public const int SampleRate = 44100;
        public const int BytesPerFrame = 4;
        private const uint Loopback = 0x00020000;
        private const uint AutoConvertPcm = 0x80000000;
        private const uint DefaultQuality = 0x08000000;
        private const uint SilentBuffer = 0x00000002;

        public static void Capture(CancellationToken cancellation, Action<byte[]> push)
        {
            IAudioClient client = OpenProcessLoopback();
            IAudioCaptureClient capture = null;
            bool started = false;
            try
            {
                var format = new WaveFormat
                {
                    Tag = 1, Channels = 2, Rate = SampleRate,
                    BytesPerSecond = SampleRate * BytesPerFrame,
                    BlockAlign = BytesPerFrame, BitsPerSample = 16
                };
                Check(client.Initialize(0, Loopback | AutoConvertPcm | DefaultQuality, 0, 0, ref format, IntPtr.Zero));
                object captureService;
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref captureId, out captureService));
                capture = (IAudioCaptureClient)captureService;
                Check(client.Start());
                started = true;

                while (!cancellation.IsCancellationRequested)
                {
                    uint packetSize;
                    Check(capture.GetNextPacketSize(out packetSize));
                    while (packetSize != 0 && !cancellation.IsCancellationRequested)
                    {
                        IntPtr data;
                        uint frames, flags;
                        ulong devicePosition, clockPosition;
                        Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out clockPosition));
                        byte[] audio;
                        try
                        {
                            audio = new byte[checked((int)frames * BytesPerFrame)];
                            if ((flags & SilentBuffer) == 0 && audio.Length != 0)
                                Marshal.Copy(data, audio, 0, audio.Length);
                        }
                        finally
                        {
                            Check(capture.ReleaseBuffer(frames));
                        }
                        if (audio.Length != 0)
                            push(audio);
                        Check(capture.GetNextPacketSize(out packetSize));
                    }
                    cancellation.WaitHandle.WaitOne(8);
                }
            }
            finally
            {
                if (started)
                    client.Stop();
                if (capture != null)
                    Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
            }
        }

        private static IAudioClient OpenProcessLoopback()
        {
            const ushort VariantBlob = 65;
            var activation = new Activation
            {
                Type = 1,
                ProcessId = (uint)Process.GetCurrentProcess().Id,
                ProcessMode = 1
            };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Activation)));
            IActivationOperation operation = null;
            try
            {
                Marshal.StructureToPtr(activation, memory, false);
                var parameters = new PropVariant
                {
                    Type = VariantBlob,
                    Data = new Blob { Size = Marshal.SizeOf(typeof(Activation)), Pointer = memory }
                };
                Guid clientId = typeof(IAudioClient).GUID;
                var completion = new ActivationCompletion();
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref clientId, ref parameters, completion, out operation));
                return (IAudioClient)completion.Wait();
            }
            finally
            {
                if (operation != null)
                    Marshal.ReleaseComObject(operation);
                Marshal.FreeHGlobal(memory);
            }
        }

        internal sealed class SpeakerMute : IDisposable
        {
            private IAudioEndpointVolume volume;
            private bool previouslyMuted;

            public void Start()
            {
                if (volume != null)
                    return;
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device = null;
                IAudioEndpointVolume endpoint = null;
                try
                {
                    Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
                    Guid volumeId = typeof(IAudioEndpointVolume).GUID;
                    object interfaceObject;
                    Check(device.Activate(ref volumeId, 23, IntPtr.Zero, out interfaceObject));
                    endpoint = (IAudioEndpointVolume)interfaceObject;
                    Check(endpoint.GetMute(out previouslyMuted));
                    Check(endpoint.SetMute(true, IntPtr.Zero));
                    volume = endpoint;
                    endpoint = null;
                }
                finally
                {
                    if (endpoint != null)
                        Marshal.ReleaseComObject(endpoint);
                    if (device != null)
                        Marshal.ReleaseComObject(device);
                    Marshal.ReleaseComObject(enumerator);
                }
            }

            public void Dispose()
            {
                IAudioEndpointVolume endpoint = volume;
                volume = null;
                if (endpoint == null)
                    return;
                try
                {
                    bool stillMuted;
                    if (!previouslyMuted)
                    {
                        Check(endpoint.GetMute(out stillMuted));
                        if (stillMuted)
                            Check(endpoint.SetMute(false, IntPtr.Zero));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(endpoint);
                }
            }
        }

        private static void Check(int result)
        {
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
        }

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync(string devicePath, ref Guid interfaceId,
            ref PropVariant activationParameters, IActivationCompletion completion, out IActivationOperation operation);

        [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivationCompletion
        {
            [PreserveSig] int ActivateCompleted(IActivationOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivationOperation
        {
            [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        }

        [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class ActivationCompletion : IActivationCompletion, IAgileObject
        {
            private readonly object gate = new object();
            private IAudioClient result;
            private int status;
            private bool completed;
            private bool expired;

            public int ActivateCompleted(IActivationOperation operation)
            {
                object activated;
                int activationResult;
                int callResult = operation.GetActivateResult(out activationResult, out activated);
                lock (gate)
                {
                    status = callResult < 0 ? callResult : activationResult;
                    if (!expired && status >= 0)
                        result = activated as IAudioClient;
                    else if (activated != null)
                        Marshal.ReleaseComObject(activated);
                    completed = true;
                    Monitor.PulseAll(gate);
                }
                return 0;
            }

            internal object Wait()
            {
                lock (gate)
                {
                    if (!completed && !Monitor.Wait(gate, 4000))
                    {
                        expired = true;
                        throw new TimeoutException("Windows audio capture did not start.");
                    }
                    Check(status);
                    if (result == null)
                        throw new InvalidOperationException("Windows audio capture is unavailable.");
                    return result;
                }
            }
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint bufferSize);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int mode, IntPtr format, IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid service, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong clock);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSecond;
            public ushort BlockAlign, BitsPerSample, MoreSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Activation
        {
            public int Type;
            public uint ProcessId;
            public int ProcessMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Pointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public Blob Data;
        }
    }
}
