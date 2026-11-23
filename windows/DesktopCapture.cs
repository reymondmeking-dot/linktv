using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LinkTV
{
    internal static class DesktopCapture
    {
        private const int MaxWidth = 1920;
        private const int MaxHeight = 1080;
        private const int FramesPerSecond = 20;
        private static readonly long FrameIntervalTicks = (Stopwatch.Frequency + FramesPerSecond - 1) / FramesPerSecond;
        private const int HalfTone = 4;
        private const uint SourceCopy = 0x00CC0020;
        private const long JpegQuality = 90;
        private const long FallbackJpegQuality = 75;
        // Leave room for framing, authentication, and encryption padding.
        private const int MaxEncodedBytes = 2 * 1024 * 1024 - 64;

        public static void Run(CancellationToken cancellation, Func<bool> canSend, Action<byte[]> send)
        {
            Rectangle desktop = Screen.PrimaryScreen.Bounds;
            double ratio = Math.Min(1, Math.Min((double)MaxWidth / desktop.Width, (double)MaxHeight / desktop.Height));
            int width = Math.Max(1, (int)Math.Round(desktop.Width * ratio));
            int height = Math.Max(1, (int)Math.Round(desktop.Height * ratio));
            bool needsScaling = width != desktop.Width || height != desktop.Height;

            using (Bitmap original = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format24bppRgb))
            using (Bitmap scaled = needsScaling ? new Bitmap(width, height, PixelFormat.Format24bppRgb) : null)
            using (Graphics sourceGraphics = Graphics.FromImage(original))
            using (Graphics scaleGraphics = needsScaling ? Graphics.FromImage(scaled) : null)
            using (ImageAttributes scaleAttributes = needsScaling ? new ImageAttributes() : null)
            using (EncoderParameters jpegParameters = new EncoderParameters(1))
            using (EncoderParameters fallbackParameters = new EncoderParameters(1))
            using (MemoryStream encoded = new MemoryStream())
            {
                jpegParameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                fallbackParameters.Param[0] = new EncoderParameter(Encoder.Quality, FallbackJpegQuality);
                ImageCodecInfo jpegCodec = Array.Find(ImageCodecInfo.GetImageEncoders(), codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                if (needsScaling)
                {
                    scaleGraphics.CompositingMode = CompositingMode.SourceCopy;
                    scaleGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    scaleGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                    scaleAttributes.SetWrapMode(WrapMode.TileFlipXY);
                }

                Rectangle outputArea = new Rectangle(0, 0, width, height);
                Bitmap frame = scaled ?? original;
                Stopwatch clock = Stopwatch.StartNew();
                long nextSend = 0;
                while (!cancellation.IsCancellationRequested)
                {
                    if (!canSend())
                    {
                        WaitUntil(clock, clock.ElapsedTicks + FrameIntervalTicks, cancellation);
                        continue;
                    }

                    if (!needsScaling || !TryScaledScreenCapture(scaleGraphics, desktop, outputArea))
                    {
                        sourceGraphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size, CopyPixelOperation.SourceCopy);
                        if (needsScaling)
                        {
                            scaleGraphics.DrawImage(original, outputArea, 0, 0, desktop.Width, desktop.Height, GraphicsUnit.Pixel, scaleAttributes);
                        }
                    }

                    encoded.SetLength(0);
                    frame.Save(encoded, jpegCodec, jpegParameters);
                    if (encoded.Length > MaxEncodedBytes)
                    {
                        encoded.SetLength(0);
                        frame.Save(encoded, jpegCodec, fallbackParameters);
                    }
                    if (encoded.Length > MaxEncodedBytes)
                    {
                        WaitUntil(clock, clock.ElapsedTicks + FrameIntervalTicks, cancellation);
                        continue;
                    }

                    // Keep at most this one frame. A slow capture or send does not accumulate overdue slots.
                    WaitUntil(clock, nextSend, cancellation);
                    if (!cancellation.IsCancellationRequested)
                    {
                        byte[] jpeg = encoded.ToArray();
                        nextSend = clock.ElapsedTicks + FrameIntervalTicks;
                        send(jpeg);
                    }
                }
            }
        }

        private static void WaitUntil(Stopwatch clock, long deadline, CancellationToken cancellation)
        {
            long ticks = deadline - clock.ElapsedTicks;
            while (ticks > 0)
            {
                int milliseconds = (int)Math.Ceiling(ticks * 1000.0 / Stopwatch.Frequency);
                if (cancellation.WaitHandle.WaitOne(milliseconds))
                {
                    return;
                }
                ticks = deadline - clock.ElapsedTicks;
            }
        }

        private static bool TryScaledScreenCapture(Graphics output, Rectangle sourceArea, Rectangle outputArea)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return TryStretchCopy(output, screen, sourceArea, outputArea);
            }
            catch (ExternalException)
            {
                return false;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static bool TryStretchCopy(Graphics output, IntPtr source, Rectangle sourceArea, Rectangle outputArea)
        {
            IntPtr destination = output.GetHdc();
            try
            {
                if (SetStretchBltMode(destination, HalfTone) == 0 || !SetBrushOrgEx(destination, 0, 0, IntPtr.Zero))
                {
                    return false;
                }
                return StretchBlt(destination, outputArea.X, outputArea.Y, outputArea.Width, outputArea.Height, source,
                    sourceArea.X, sourceArea.Y, sourceArea.Width, sourceArea.Height, SourceCopy);
            }
            finally
            {
                output.ReleaseHdc(destination);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll")]
        private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source,
            int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    }
}
