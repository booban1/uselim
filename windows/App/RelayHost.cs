using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Uselim
{
    // What the relay asks of Windows: the limits the tray holds, the PC's idle time and lock, links into VS Code, the
    // phone app's files from inside the exe, and pictures fitted with Windows' own codecs. Every call may come from the
    // relay's threads, so each one reads what the tray hands it as a whole.

    internal sealed class RelayHost : IRelayHost
    {
        // the limits and their time, swapped whole by the tray
        public volatile Tuple<List<Limit>, DateTime?> Snapshot = Tuple.Create(new List<Limit>(), (DateTime?)null);
        public volatile bool IsLocked;

        public List<Limit> Limits(out DateTime? at)
        {
            var held = Snapshot;
            at = held.Item2;
            return held.Item1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInput
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);

        public double IdleSeconds(out bool locked)
        {
            locked = IsLocked;
            var info = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.Time) / 1000.0;
        }

        // the extension's own link: VS Code opens the chat, or a fresh one
        public void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Note("relay: the link failed to open, " + e.Message); }
        }

        public byte[] WebFile(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/" + name))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        public void Saved(string name, string value) => Prefs.Set(name, value);

        // A picture is fitted the way Culpmit fits a screenshot: the long side at most the size given and the area two
        // thirds of that square, so the chat reads it whole; one the Claude Code panel takes as it is (JPEG, PNG, GIF)
        // and that fits already goes byte for byte, every other one becomes a JPEG, or a PNG where it carries
        // transparency. Data null: the file goes as it is. Anything Windows cannot draw counts as a plain file.
        public Fitted FitFile(string path, int longSide)
        {
            try
            {
                using (var stream = File.OpenRead(path)) return Fit(stream, longSide, false);
            }
            catch (Exception) { return null; }
        }

        public Fitted FitBytes(byte[] data, int longSide)
        {
            try
            {
                using (var stream = new MemoryStream(data))
                {
                    var fitted = Fit(stream, longSide, true);
                    if (fitted != null && fitted.Data == null) fitted.Data = data;
                    return fitted;
                }
            }
            catch (Exception) { return null; }
        }

        private static Fitted Fit(Stream stream, int longSide, bool keepBytes)
        {
            Image image;
            try { image = Image.FromStream(stream, true, true); }
            catch (Exception) { return null; }
            using (image)
            {
                var format = image.RawFormat.Guid;
                string ext = format == ImageFormat.Jpeg.Guid ? "jpg" : format == ImageFormat.Png.Guid ? "png" : format == ImageFormat.Gif.Guid ? "gif" : null;
                var turn = Orientation(image);
                double w = image.Width, h = image.Height;
                if (w <= 0 || h <= 0) return null;
                var scale = Math.Min(1, Math.Min(longSide / Math.Max(w, h), Math.Sqrt(0.667 * longSide * longSide / (w * h))));
                if (scale >= 1 && turn == RotateFlipType.RotateNoneFlipNone && ext != null) return new Fitted { Data = null, Ext = ext };
                var isClear = format == ImageFormat.Png.Guid || format == ImageFormat.Gif.Guid || Image.IsAlphaPixelFormat(image.PixelFormat);
                using (var upright = new Bitmap(image))
                {
                    if (turn != RotateFlipType.RotateNoneFlipNone) upright.RotateFlip(turn);
                    var tw = Math.Max(1, (int)Math.Round(upright.Width * scale));
                    var th = Math.Max(1, (int)Math.Round(upright.Height * scale));
                    using (var fitted = new Bitmap(tw, th, isClear ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(fitted))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            if (!isClear) g.Clear(Color.White);
                            g.DrawImage(upright, 0, 0, tw, th);
                        }
                        using (var output = new MemoryStream())
                        {
                            if (isClear) fitted.Save(output, ImageFormat.Png);
                            else
                            {
                                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                using (var settings = new EncoderParameters(1))
                                {
                                    settings.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                                    fitted.Save(output, codec, settings);
                                }
                            }
                            return new Fitted { Data = output.ToArray(), Ext = isClear ? "png" : "jpg" };
                        }
                    }
                }
            }
        }

        // the photo's EXIF orientation as the turn that sets it upright, since the model reads the pixels as stored
        private static RotateFlipType Orientation(Image image)
        {
            if (!image.PropertyIdList.Contains(0x0112)) return RotateFlipType.RotateNoneFlipNone;
            switch (image.GetPropertyItem(0x0112).Value[0])
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }
    }
}
