using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LinkTV
{
    internal static class FfmpegVideo
    {
        public const int DesktopFps = 60;
        public const int MaxAccessUnitBytes = 2097093;

        internal sealed class Installation
        {
            public readonly string Path;
            public readonly bool CanDecode;
            public readonly bool HasNvenc;

            public Installation(string path, bool canDecode, bool hasNvenc)
            {
                Path = path;
                CanDecode = canDecode;
                HasNvenc = hasNvenc;
            }
        }

        public static Installation FindInstallation()
        {
            HashSet<string> searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in Candidates())
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }
                string path = Path.GetFullPath(candidate);
                if (searched.Add(path))
                {
                    Installation usable = Inspect(path);
                    if (usable != null)
                    {
                        return usable;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            string configured = Environment.GetEnvironmentVariable("LINKTV_FFMPEG");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                yield return configured.Trim().Trim('"');
            }
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            string search = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in search.Split(Path.PathSeparator))
            {
                string folder = entry.Trim().Trim('"');
                if (folder.Length == 0)
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(folder, "ffmpeg.exe");
                }
                catch (ArgumentException)
                {
                    continue;
                }
                yield return candidate;
            }
        }

        private static Installation Inspect(string executable)
        {
            try
            {
                using (Process process = Start(executable, "-codecs", false))
                {
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(2200))
                    {
                        Kill(process);
                        return null;
                    }
                    if (process.ExitCode != 0 || !output.Wait(500))
                    {
                        return null;
                    }
                    using (StringReader codecs = new StringReader(output.Result))
                    {
                        string line;
                        while ((line = codecs.ReadLine()) != null)
                        {
                            string[] fields = line.Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                            if (fields.Length < 3 || fields[0].Length != 6 || fields[1] != "h264")
                            {
                                continue;
                            }

                            bool canDecode = fields[0][0] == 'D';
                            bool hasNvenc = fields[0][1] == 'E' && fields[2].Contains(" h264_nvenc");
                            return canDecode || hasNvenc ? new Installation(executable, canDecode, hasNvenc) : null;
                        }
                    }
                }
            }
            catch (Win32Exception error)
            {
                Trace.TraceWarning("LinkTV codec probe: " + error.Message);
            }
            catch (InvalidOperationException error)
            {
                Trace.TraceWarning("LinkTV codec probe: " + error.Message);
            }
            catch (AggregateException error)
            {
                Trace.TraceWarning("LinkTV codec probe: " + error.Message);
            }
            return null;
        }

        public static void CaptureDesktop(string executable, CancellationToken stop,
            Action<int, int, int> begin, Action<long, bool, byte[]> send)
        {
            Screen monitor = Screen.PrimaryScreen;
            Rectangle desktop = monitor.Bounds;
            bool landscape = desktop.Width >= desktop.Height;
            double ratio = Math.Min(1, Math.Min((landscape ? 1920.0 : 1080.0) / desktop.Width,
                (landscape ? 1080.0 : 1920.0) / desktop.Height));
            int width = Math.Max(2, (int)(desktop.Width * ratio) & ~1);
            int height = Math.Max(2, (int)(desktop.Height * ratio) & ~1);
            string dimensions = width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture);
            string device = monitor.DeviceName;
            int displayNumber;
            int outputIndex = device.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(device.Substring(11), out displayNumber) && displayNumber >= 1 ? displayNumber - 1 : 0;
            string dda = "-f lavfi -i ddagrab=output_idx=" + outputIndex + ":framerate=60:draw_mouse=1:dup_frames=1 ";
            string cpuScale = "-vf hwdownload,format=bgra,scale=" + dimensions + ":flags=fast_bilinear,format=nv12 ";
            string gdi = "-f gdigrab -framerate 60 -draw_mouse 1 -offset_x " + desktop.X
                + " -offset_y " + desktop.Y + " -video_size " + desktop.Width + "x" + desktop.Height
                + " -i desktop -vf scale=" + dimensions + ":flags=fast_bilinear,format=nv12 ";

            // On hybrid systems the display D3D device may not be usable by NVENC; the CPU scale
            // also bridges those devices. Try the direct GPU path when scaling is unnecessary.
            string[] inputs = desktop.Width == width && desktop.Height == height
                ? new[] { dda, dda + cpuScale, gdi }
                : new[] { dda + cpuScale, dda + "-vf scale_d3d11=width=" + width + ":height=" + height + ":format=nv12 ", gdi };

            foreach (string input in inputs)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }
                if (StreamDesktop(executable, input, width, height, stop, begin, send))
                {
                    return;
                }
            }
        }

        private static bool StreamDesktop(string executable, string input, int width, int height, CancellationToken stop,
            Action<int, int, int> begin, Action<long, bool, byte[]> send)
        {
            const string encoder = "-map 0:v:0 -an -fps_mode passthrough -c:v h264_nvenc -preset p1 -tune ull "
                + "-profile:v high -level:v 4.2 -rc cbr -b:v 12M -maxrate:v 12M -bufsize:v 1200k -g 60 -bf 0 "
                + "-zerolatency 1 -rc-lookahead 0 -forced-idr 1 -aud 1 "
                + "-bsf:v h264_metadata=aud=insert,dump_extra=freq=keyframe -f h264 pipe:1";
            Process process;
            try
            {
                process = Start(executable, "-filter_threads 4 " + input + encoder, false);
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            using (process)
            using (stop.Register(() => Kill(process)))
            using (System.Threading.Timer watchdog = new System.Threading.Timer(state => Kill(process), null, 5500, Timeout.Infinite))
            {
                try
                {
                    AnnexBReader parser = new AnnexBReader();
                    byte[] buffer = new byte[65536];
                    long frameIndex = 0;
                    bool begun = false;
                    Action<bool, byte[]> publish = (key, frame) =>
                    {
                        if (stop.IsCancellationRequested)
                        {
                            return;
                        }
                        if (!begun)
                        {
                            if (!key)
                            {
                                return;
                            }
                            begin(width, height, DesktopFps);
                            begun = true;
                        }

                        watchdog.Change(8000, Timeout.Infinite);
                        send(frameIndex * 1000000L / DesktopFps, key, frame);
                        frameIndex++;
                    };
                    Stream stdout = process.StandardOutput.BaseStream;
                    int count;
                    try
                    {
                        while (!stop.IsCancellationRequested && (count = ReadPipe(stdout, buffer)) > 0)
                        {
                            parser.Read(buffer, count, false, publish);
                        }
                        if (!stop.IsCancellationRequested)
                        {
                            parser.Read(buffer, 0, true, publish);
                        }
                    }
                    catch (InvalidDataException error)
                    {
                        Trace.TraceWarning(error.Message);
                    }
                    return begun;
                }
                finally
                {
                    Kill(process);
                    process.WaitForExit(1000);
                }
            }
        }

        internal static int ReadPipe(Stream pipe, byte[] buffer)
        {
            try
            {
                return pipe.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        internal static Process Start(string executable, string arguments, bool input)
        {
            Process process = new Process();
            process.StartInfo = new ProcessStartInfo(executable, "-hide_banner -nostdin -loglevel error " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = input,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Trace.TraceWarning("LinkTV FFmpeg: " + e.Data);
                }
            };
            try
            {
                process.Start();
                process.BeginErrorReadLine();
                return process;
            }
            catch
            {
                Kill(process);
                process.Dispose();
                throw;
            }
        }

        internal static void Kill(Process process)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        internal sealed class AnnexBReader
        {
            private readonly MemoryStream pending = new MemoryStream();
            private readonly MemoryStream picture = new MemoryStream();
            private byte[] sequence;
            private byte[] parameters;
            private bool hasPicture;
            private bool keyFrame;

            public void Read(byte[] chunk, int count, bool final, Action<bool, byte[]> publish)
            {
                pending.Write(chunk, 0, count);
                int bytes = (int)pending.Length;
                byte[] data = pending.GetBuffer();
                int header;
                int marker = FindStart(data, 0, bytes, out header);
                while (marker >= 0 && header < bytes)
                {
                    int nextHeader;
                    int next = FindStart(data, header + 1, bytes, out nextHeader);
                    if (next < 0 && !final)
                    {
                        break;
                    }
                    Accept(data, marker, header, next < 0 ? bytes : next, publish);
                    marker = next;
                    header = nextHeader;
                }
                if (final && hasPicture)
                {
                    Publish(publish);
                }
                int keep = marker >= 0 ? bytes - marker : final ? 0 : Math.Min(3, bytes);
                if (keep > MaxAccessUnitBytes)
                {
                    throw new InvalidDataException("FFmpeg H.264 NAL exceeded the LinkTV frame bound.");
                }
                if (keep > 0)
                {
                    Buffer.BlockCopy(data, bytes - keep, data, 0, keep);
                }
                pending.SetLength(keep);
                pending.Position = keep;
            }

            private void Accept(byte[] data, int marker, int header, int end, Action<bool, byte[]> publish)
            {
                int type = data[header] & 31;
                if (hasPicture && (type == 7 || type == 8 || type == 9))
                {
                    Publish(publish);
                }
                if (type == 7 || type == 8)
                {
                    byte[] parameter = new byte[end - marker];
                    Buffer.BlockCopy(data, marker, parameter, 0, parameter.Length);
                    if (type == 7)
                    {
                        sequence = parameter;
                    }
                    else
                    {
                        parameters = parameter;
                    }
                    return;
                }

                if (picture.Length + end - marker > MaxAccessUnitBytes)
                {
                    throw new InvalidDataException("FFmpeg H.264 picture exceeded the LinkTV frame bound.");
                }
                picture.Write(data, marker, end - marker);
                hasPicture |= type >= 1 && type <= 5;
                keyFrame |= type == 5;
            }

            private void Publish(Action<bool, byte[]> publish)
            {
                if (keyFrame && sequence != null && parameters != null)
                {
                    byte[] output = new byte[picture.Length + sequence.Length + parameters.Length];
                    if (output.Length > MaxAccessUnitBytes)
                    {
                        throw new InvalidDataException("FFmpeg H.264 key frame exceeded the LinkTV frame bound.");
                    }
                    byte[] nalData = picture.GetBuffer();
                    int firstHeader;
                    FindStart(nalData, 0, (int)picture.Length, out firstHeader);
                    int afterAud = 0;
                    if (firstHeader >= 0 && (nalData[firstHeader] & 31) == 9)
                    {
                        int ignored;
                        afterAud = FindStart(nalData, firstHeader + 1, (int)picture.Length, out ignored);
                        afterAud = Math.Max(0, afterAud);
                    }
                    Buffer.BlockCopy(nalData, 0, output, 0, afterAud);
                    Buffer.BlockCopy(sequence, 0, output, afterAud, sequence.Length);
                    Buffer.BlockCopy(parameters, 0, output, afterAud + sequence.Length, parameters.Length);
                    Buffer.BlockCopy(nalData, afterAud, output, afterAud + sequence.Length + parameters.Length,
                        (int)picture.Length - afterAud);
                    publish(true, output);
                }
                else if (!keyFrame)
                {
                    publish(false, picture.ToArray());
                }
                picture.SetLength(0);
                picture.Position = 0;
                hasPicture = false;
                keyFrame = false;
            }

            private static int FindStart(byte[] data, int first, int length, out int header)
            {
                for (int index = first; index + 2 < length; index++)
                {
                    if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
                    {
                        header = index + 3;
                        return index > 0 && data[index - 1] == 0 ? index - 1 : index;
                    }
                }
                header = -1;
                return -1;
            }
        }
    }
}
