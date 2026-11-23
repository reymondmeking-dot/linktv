using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinkTV
{
    internal sealed class PcmPlayback : IDisposable
    {
        private const int MaxPacketBytes = 16384;
        private const int MaxPendingBytes = 16384;
        private const int MaxDeviceBytes = 24576;
        private const int DeviceBufferCount = 6;
        private const uint WaveMapper = 0xffffffff;
        private const uint CallbackEvent = 0x00050000;
        private const int HeaderDone = 0x00000001;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
        private static readonly int SizeOffset = Marshal.OffsetOf(typeof(WaveHeader), "BufferLength").ToInt32();
        private static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WaveHeader), "Flags").ToInt32();

        private readonly object gate = new object();
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private readonly AutoResetEvent work = new AutoResetEvent(false);
        private int pendingBytes;
        private int generation;
        private bool receiving;
        private bool muted;
        private bool disposed;

        public PcmPlayback()
        {
            Thread thread = new Thread(Play);
            thread.Name = "LinkTV sound playback";
            thread.IsBackground = true;
            thread.Start();
        }

        public void SetReceiving(bool active)
        {
            lock (gate)
            {
                if (disposed || receiving == active)
                {
                    return;
                }

                receiving = active;
                ResetOutput();
            }
        }

        public bool IsMuted
        {
            get
            {
                lock (gate)
                {
                    return muted;
                }
            }
        }

        public void SetMuted(bool value)
        {
            lock (gate)
            {
                if (disposed || muted == value)
                {
                    return;
                }

                muted = value;
                ResetOutput();
            }
        }

        public void Enqueue(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0 || pcm.Length > MaxPacketBytes || pcm.Length % WindowsAudio.BytesPerFrame != 0)
            {
                return;
            }

            lock (gate)
            {
                if (disposed || !receiving || muted)
                {
                    return;
                }

                while (pendingBytes + pcm.Length > MaxPendingBytes)
                {
                    pendingBytes -= pending.Dequeue().Length;
                }

                pending.Enqueue(pcm);
                pendingBytes += pcm.Length;
                work.Set();
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
                ClearPending();
                work.Set();
            }
        }

        private void Play()
        {
            WaveOutput output = null;
            int openGeneration = -1;
            bool outputUnavailable = false;
            try
            {
                while (true)
                {
                    int currentGeneration;
                    byte[] next;
                    lock (gate)
                    {
                        if (disposed)
                        {
                            return;
                        }

                        currentGeneration = generation;
                        next = pending.Count == 0 ? null : pending.Peek();
                    }

                    if (currentGeneration != openGeneration)
                    {
                        if (output != null)
                        {
                            output.Dispose();
                            output = null;
                        }
                        outputUnavailable = false;
                        openGeneration = currentGeneration;
                    }

                    if (next == null || outputUnavailable)
                    {
                        work.WaitOne();
                        continue;
                    }

                    try
                    {
                        if (output == null)
                        {
                            output = new WaveOutput(work);
                        }

                        WaveBuffer buffer = output.GetBuffer(next.Length);
                        if (buffer == null)
                        {
                            output.WaitForProgress();
                            continue;
                        }

                        lock (gate)
                        {
                            if (disposed || generation != openGeneration || pending.Count == 0 || !ReferenceEquals(pending.Peek(), next))
                            {
                                continue;
                            }

                            pending.Dequeue();
                            pendingBytes -= next.Length;
                        }

                        output.Write(buffer, next);
                    }
                    catch (InvalidOperationException error)
                    {
                        Trace.TraceWarning(error.ToString());
                        if (output != null)
                        {
                            output.Dispose();
                            output = null;
                        }
                        outputUnavailable = true;
                    }
                }
            }
            finally
            {
                if (output != null)
                {
                    output.Dispose();
                }
                work.Dispose();
            }
        }

        private void ClearPending()
        {
            pending.Clear();
            pendingBytes = 0;
        }

        private void ResetOutput()
        {
            // The playback worker resets any device buffers for the previous generation.
            generation++;
            ClearPending();
            work.Set();
        }

        private static void Check(uint result)
        {
            if (result != 0)
            {
                throw new InvalidOperationException("Windows waveform output returned " + result + ".");
            }
        }

        private sealed class WaveOutput : IDisposable
        {
            private readonly ManualResetEvent progress = new ManualResetEvent(false);
            private readonly WaitHandle[] signals;
            private readonly WaveBuffer[] buffers = new WaveBuffer[DeviceBufferCount];
            private IntPtr handle;

            public WaveOutput(WaitHandle work)
            {
                signals = new WaitHandle[] { work, progress };
                WaveFormat format = new WaveFormat
                {
                    FormatTag = 1,
                    Channels = 2,
                    SamplesPerSecond = WindowsAudio.SampleRate,
                    AverageBytesPerSecond = WindowsAudio.SampleRate * WindowsAudio.BytesPerFrame,
                    BlockAlign = WindowsAudio.BytesPerFrame,
                    BitsPerSample = 16,
                    ExtraBytes = 0
                };
                try
                {
                    Check(waveOutOpen(out handle, WaveMapper, ref format,
                        progress.SafeWaitHandle.DangerousGetHandle(), UIntPtr.Zero, CallbackEvent));
                    for (int index = 0; index < buffers.Length; index++)
                    {
                        buffers[index] = new WaveBuffer(handle);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public WaveBuffer GetBuffer(int nextBytes)
            {
                WaveBuffer available = null;
                int activeBytes = 0;
                foreach (WaveBuffer buffer in buffers)
                {
                    if (buffer.ActiveBytes > 0 && (Marshal.ReadInt32(buffer.Header, FlagsOffset) & HeaderDone) != 0)
                    {
                        buffer.ActiveBytes = 0;
                    }

                    activeBytes += buffer.ActiveBytes;
                    if (buffer.ActiveBytes == 0 && available == null)
                    {
                        available = buffer;
                    }
                }
                return activeBytes + nextBytes <= MaxDeviceBytes ? available : null;
            }

            public void Write(WaveBuffer buffer, byte[] pcm)
            {
                Marshal.Copy(pcm, 0, buffer.Data, pcm.Length);
                Marshal.WriteInt32(buffer.Header, SizeOffset, pcm.Length);
                Check(waveOutWrite(handle, buffer.Header, HeaderSize));
                buffer.ActiveBytes = pcm.Length;
            }

            public void WaitForProgress()
            {
                WaitHandle.WaitAny(signals, 30);
                progress.Reset();
            }

            public void Dispose()
            {
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                    foreach (WaveBuffer buffer in buffers)
                    {
                        if (buffer != null)
                        {
                            buffer.Release(handle);
                        }
                    }
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
                progress.Dispose();
            }
        }

        private sealed class WaveBuffer
        {
            public readonly IntPtr Header;
            public readonly IntPtr Data;
            public int ActiveBytes;

            public WaveBuffer(IntPtr handle)
            {
                Data = Marshal.AllocHGlobal(MaxPacketBytes);
                try
                {
                    Header = Marshal.AllocHGlobal((int)HeaderSize);
                    WaveHeader header = new WaveHeader { Data = Data, BufferLength = MaxPacketBytes };
                    Marshal.StructureToPtr(header, Header, false);
                    Check(waveOutPrepareHeader(handle, Header, HeaderSize));
                }
                catch
                {
                    if (Header != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(Header);
                    }
                    Marshal.FreeHGlobal(Data);
                    throw;
                }
            }

            public void Release(IntPtr handle)
            {
                uint status = waveOutUnprepareHeader(handle, Header, HeaderSize);
                if (status == 0)
                {
                    Marshal.FreeHGlobal(Header);
                    Marshal.FreeHGlobal(Data);
                }
                else
                {
                    Trace.TraceWarning("Windows could not release a waveform buffer: " + status + ".");
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AverageBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public UIntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveOutOpen(out IntPtr output, uint device, ref WaveFormat format,
            IntPtr callback, UIntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern uint waveOutReset(IntPtr output);

        [DllImport("winmm.dll")]
        private static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern uint waveOutClose(IntPtr output);
    }
}
