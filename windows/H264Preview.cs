using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinkTV
{
    internal sealed class H264Preview : IDisposable
    {
        private const int QueueFrames = 8;
        private const int QueueBytes = 2 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly AutoResetEvent work = new AutoResetEvent(false);
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private readonly string executable;
        private readonly int width;
        private readonly int height;
        private readonly Action<Bitmap> display;
        private int pendingBytes;
        private int generation;
        private bool waitingForKey = true;
        private bool disposed;
        private Process child;

        public H264Preview(string executable, int width, int height, Action<Bitmap> display)
        {
            this.executable = executable;
            this.width = width;
            this.height = height;
            this.display = display;
            Thread worker = new Thread(Decode) { IsBackground = true, Name = "LinkTV H.264 input" };
            worker.Start();
        }

        public void Enqueue(bool keyFrame, byte[] accessUnit)
        {
            Process obsolete = null;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                if (pending.Count >= QueueFrames || pendingBytes + accessUnit.Length > QueueBytes)
                {
                    ResetQueue();
                    obsolete = child;
                }
                if (!waitingForKey || keyFrame)
                {
                    waitingForKey = false;
                    pending.Enqueue(accessUnit);
                    pendingBytes += accessUnit.Length;
                    work.Set();
                }
            }
            FfmpegVideo.Kill(obsolete);
        }

        public void Dispose()
        {
            Process obsolete;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                ResetQueue();
                obsolete = child;
                work.Set();
            }
            FfmpegVideo.Kill(obsolete);
        }

        private void ResetQueue()
        {
            pending.Clear();
            pendingBytes = 0;
            waitingForKey = true;
            generation++;
        }

        private void Decode()
        {
            Process active = null;
            Thread output = null;
            int activeGeneration = -1;
            try
            {
                while (true)
                {
                    byte[] packet = null;
                    int selectedGeneration;
                    lock (gate)
                    {
                        if (disposed)
                        {
                            return;
                        }
                        selectedGeneration = generation;
                        if (pending.Count > 0)
                        {
                            packet = pending.Dequeue();
                            pendingBytes -= packet.Length;
                        }
                    }
                    if (activeGeneration != selectedGeneration && active != null)
                    {
                        FinishProcess(active, output);
                        active = null;
                        output = null;
                    }
                    if (packet == null)
                    {
                        work.WaitOne();
                        continue;
                    }

                    try
                    {
                        if (active == null)
                        {
                            string scale = "scale=" + width + ":" + height + ":flags=fast_bilinear";
                            active = FfmpegVideo.Start(executable, "-probesize 32 -analyzeduration 1 -threads 2 "
                                + "-flags low_delay -f h264 -i pipe:0 -an -sn -dn -vf " + scale
                                + " -fps_mode passthrough -threads:v 1 -c:v rawvideo -pix_fmt bgr24 -flush_packets 1 -f rawvideo pipe:1", true);
                            bool usable;
                            lock (gate)
                            {
                                child = active;
                                usable = !disposed && generation == selectedGeneration;
                            }
                            if (!usable)
                            {
                                FinishProcess(active, null);
                                active = null;
                                continue;
                            }

                            Process reading = active;
                            int decoderGeneration = selectedGeneration;
                            output = new Thread(() => ReadFrames(reading, decoderGeneration))
                            {
                                IsBackground = true,
                                Name = "LinkTV H.264 output"
                            };
                            activeGeneration = selectedGeneration;
                            output.Start();
                        }
                        Stream stdin = active.StandardInput.BaseStream;
                        stdin.Write(packet, 0, packet.Length);
                        stdin.Flush();
                    }
                    catch (IOException error)
                    {
                        Recover(selectedGeneration, error);
                    }
                    catch (Win32Exception error)
                    {
                        Recover(selectedGeneration, error);
                    }
                    catch (InvalidOperationException error)
                    {
                        Recover(selectedGeneration, error);
                    }
                }
            }
            finally
            {
                if (active != null)
                {
                    FinishProcess(active, output);
                }
                work.Dispose();
            }
        }

        private void Recover(int expected, Exception error)
        {
            Trace.TraceWarning("LinkTV preview decoder: " + error.Message);
            Process obsolete = null;
            lock (gate)
            {
                if (!disposed && generation == expected)
                {
                    ResetQueue();
                    obsolete = child;
                    work.Set();
                }
            }
            FfmpegVideo.Kill(obsolete);
        }

        private void ReadFrames(Process process, int sourceGeneration)
        {
            try
            {
                Stream stdout = process.StandardOutput.BaseStream;
                int rowBytes = width * 3;
                byte[] packed = new byte[rowBytes * height];
                while (ReadFrame(stdout, packed))
                {
                    lock (gate)
                    {
                        if (disposed || generation != sourceGeneration)
                        {
                            return;
                        }
                    }
                    Bitmap frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    try
                    {
                        BitmapData pixels = frame.LockBits(new Rectangle(0, 0, width, height),
                            ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            if (pixels.Stride == rowBytes)
                            {
                                Marshal.Copy(packed, 0, pixels.Scan0, packed.Length);
                            }
                            else
                            {
                                for (int row = 0; row < height; row++)
                                {
                                    Marshal.Copy(packed, row * rowBytes, IntPtr.Add(pixels.Scan0, row * pixels.Stride), rowBytes);
                                }
                            }
                        }
                        finally
                        {
                            frame.UnlockBits(pixels);
                        }
                        display(frame);
                        frame = null;
                    }
                    finally
                    {
                        if (frame != null)
                        {
                            frame.Dispose();
                        }
                    }
                }
                Recover(sourceGeneration, new IOException("FFmpeg preview stream ended."));
            }
            catch (IOException error)
            {
                Recover(sourceGeneration, error);
            }
            catch (InvalidOperationException error)
            {
                Recover(sourceGeneration, error);
            }
            catch (ExternalException error)
            {
                Recover(sourceGeneration, error);
            }
        }

        private static bool ReadFrame(Stream pipe, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = pipe.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }

        private void FinishProcess(Process process, Thread output)
        {
            lock (gate)
            {
                if (child == process)
                {
                    child = null;
                }
            }
            FfmpegVideo.Kill(process);
            process.WaitForExit(750);
            if (output != null)
            {
                output.Join(750);
            }
            process.Dispose();
        }
    }
}
