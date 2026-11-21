using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Uselim
{
    // The readout on the taskbar, the Mac's menu bar readout carried over: the asterisk and the two lines, in a
    // small window attached to the taskbar just left of its right-hand group (the notification area with anything Windows
    // docks beside it, such as News and interests), the way TrafficMonitor shows its speeds.
    // A child of the taskbar's own window, so it rides along as the taskbar hides, moves or changes size; a layered
    // window drawn with real transparency, so it sits on any taskbar colour. A click opens the panel, a right click the
    // tray's menu. On Windows 10 the task buttons' strip gives up the room it needs, and gets it back on the way out.
    // A taskbar standing upright, or one whose notification area stays out of reach, leaves the tray icon on its own.
    // Every two seconds it checks its place, and after Explorer restarts it attaches to the new taskbar.

    internal sealed class TaskbarReadout : NativeWindow, IDisposable
    {
        private readonly Tray tray;
        private readonly Timer follow = new Timer { Interval = 2000 };
        private IntPtr taskbar;
        private IntPtr rebar;
        private RECT rebarFrom;
        private bool rebarMoved;
        private List<Piece> top = new List<Piece>(), bottom = new List<Piece>();
        private bool stale, dot;
        private string lastPlace = "";
        private bool running;

        private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        public TaskbarReadout(Tray tray)
        {
            this.tray = tray;
            follow.Tick += (o, e) => Safely(Follow);
        }

        public bool IsShown => Handle != IntPtr.Zero && IsWindowVisible(Handle);

        public void Start()
        {
            if (running) return;
            running = true;
            follow.Start();
            Safely(Follow);
        }

        public void Stop()
        {
            running = false;
            follow.Stop();
            Safely(Detach);
        }

        public void Update(List<Piece> top, List<Piece> bottom, bool stale, bool dot)
        {
            this.top = top;
            this.bottom = bottom;
            this.stale = stale;
            this.dot = dot;
            if (running) Safely(Render);
        }

        // a failure stays inside the readout: logged once per kind, the readout hidden, the tray carrying on
        private void Safely(Action work)
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                Note("failed: " + e.GetType().Name + ", " + e.Message);
                try { if (Handle != IntPtr.Zero) ShowWindow(Handle, SW_HIDE); } catch (Exception) { }
            }
        }

        // attached to the taskbar of the moment, then placed and drawn
        private void Follow()
        {
            if (!running) return;
            var current = FindWindow("Shell_TrayWnd", null);
            if (current == IntPtr.Zero) return;
            if (Handle != IntPtr.Zero && current != taskbar) Detach();
            if (Handle == IntPtr.Zero)
            {
                // a fresh taskbar after Explorer restarts carries fresh parts
                taskbar = current;
                rebar = IntPtr.Zero;
                rebarMoved = false;
                var cp = new CreateParams
                {
                    Caption = "Uselim readout",
                    Parent = taskbar,
                    Style = WS_CHILD | WS_CLIPSIBLINGS,
                    ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    X = 0, Y = 0, Width = 1, Height = 1,
                };
                try
                {
                    CreateHandle(cp);
                }
                catch (Exception e)
                {
                    Log.Note("taskbar readout: attach failed, " + e.Message);
                    return;
                }
            }
            Render();
        }

        private void Detach()
        {
            RestoreRebar();
            rebar = IntPtr.Zero;
            if (Handle != IntPtr.Zero)
            {
                try { DestroyHandle(); } catch (Exception) { }
            }
            taskbar = IntPtr.Zero;
        }

        // the place left of the taskbar's right-hand group, null where the taskbar stands upright or its parts are out of
        // reach
        private Rectangle? Place(int width)
        {
            if (!GetWindowRect(taskbar, out var tb)) return null;
            if (tb.Right - tb.Left <= tb.Bottom - tb.Top) return null;
            var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero || !GetWindowRect(notify, out var nr) || nr.Right - nr.Left <= 0) return null;
            var s = Scale;
            var right = RightGroupLeft(nr) - (int)(4 * s);
            var left = right - width;
            if (!IsWindows11) MakeRoom(tb, left);
            return new Rectangle(left, tb.Top, width, tb.Bottom - tb.Top);
        }

        // The left edge of the right-hand group: the notification area, or further left any visible part of the taskbar
        // Windows docks between the task buttons and it (News and interests on Windows 10, a toolbar a person added), so
        // the readout sits beside them, over none of them
        private int RightGroupLeft(RECT notify)
        {
            if (rebar == IntPtr.Zero) rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            var start = rebarMoved ? rebarFrom.Left : rebar != IntPtr.Zero && GetWindowRect(rebar, out var rr) ? rr.Left : int.MinValue;
            var left = notify.Left;
            for (var child = GetWindow(taskbar, GW_CHILD); child != IntPtr.Zero; child = GetWindow(child, GW_HWNDNEXT))
            {
                if (child == Handle || child == rebar || !IsWindowVisible(child) || !GetWindowRect(child, out var cr)) continue;
                if (cr.Right - cr.Left <= 0 || cr.Left <= start || cr.Right > notify.Left + 1) continue;
                if (cr.Left < left) left = cr.Left;
            }
            return left;
        }

        // Windows 10: the task buttons' strip ends where the readout starts
        private void MakeRoom(RECT tb, int left)
        {
            if (rebar == IntPtr.Zero) rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            if (rebar == IntPtr.Zero || !GetWindowRect(rebar, out var rr)) return;
            if (!rebarMoved)
            {
                rebarFrom = rr;
                rebarMoved = true;
            }
            var width = left - rr.Left;
            if (width > 0 && rr.Right != left)
                MoveWindow(rebar, rr.Left - tb.Left, rr.Top - tb.Top, width, rr.Bottom - rr.Top, true);
        }

        private void RestoreRebar()
        {
            if (!rebarMoved || rebar == IntPtr.Zero || !IsWindow(rebar) || !GetWindowRect(taskbar, out var tb)) return;
            MoveWindow(rebar, rebarFrom.Left - tb.Left, rebarFrom.Top - tb.Top, rebarFrom.Right - rebarFrom.Left, rebarFrom.Bottom - rebarFrom.Top, true);
            rebarMoved = false;
        }

        private static float Scale
        {
            get
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
        }

        private void Render()
        {
            if (Handle == IntPtr.Zero) return;
            var s = Scale;
            var light = Palette.TaskbarLight;
            var ink = Glyph.Ink(light);
            var family = FontFamilyName();
            using (var topFont = new Font(family, 12.5f * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var bottomFont = new Font(family, 11f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                float topW, bottomW;
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                {
                    topW = Measure(g, top, topFont, format);
                    bottomW = Measure(g, bottom, bottomFont, format);
                }
                var pad = 8 * s;
                var star = 16 * s;
                var gap = 6 * s;
                var dotSize = 6 * s;
                var textW = Math.Max(topW, bottomW);
                var width = (int)Math.Ceiling(pad + star + gap + textW + (dot ? 4 * s + dotSize : 0) + pad);
                var place = Place(width);
                if (place == null)
                {
                    ShowWindow(Handle, SW_HIDE);
                    Note("hidden: the taskbar's notification area is out of reach or the taskbar stands upright");
                    return;
                }
                var box = place.Value;
                var height = box.Height;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        // alpha 1 everywhere: invisible, yet the whole box takes clicks
                        g.Clear(Color.FromArgb(1, 0, 0, 0));
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        var fade = stale ? 128 : 255;
                        Glyph.Asterisk(g, new RectangleF(pad, (height - star) / 2f, star, star), Color.FromArgb(fade, ink));
                        var x = pad + star + gap;
                        var h1 = topFont.GetHeight(g);
                        var h2 = bottomFont.GetHeight(g);
                        var y = (height - (h1 + (bottom.Count > 0 ? h2 * 0.98f : 0))) / 2f;
                        Paint(g, top, topFont, format, x, y, light, fade);
                        if (bottom.Count > 0) Paint(g, bottom, bottomFont, format, x, y + h1 * 0.98f, light, fade);
                        if (dot)
                            using (var orange = new SolidBrush(Glyph.Orange))
                                g.FillEllipse(orange, x + textW + 4 * s, (height - dotSize) / 2f, dotSize, dotSize);
                    }

                    // the box in the taskbar's own coordinates, then the picture with its alpha
                    var at = new POINT { X = box.X, Y = box.Y };
                    ScreenToClient(taskbar, ref at);
                    SetWindowPos(Handle, HWND_TOP, at.X, at.Y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    Blit(bmp);
                    Note("at " + box.X + "," + box.Y + " " + width + "x" + height);
                }
            }
        }

        private void Note(string place)
        {
            if (place == lastPlace) return;
            lastPlace = place;
            Log.Note("taskbar readout: " + place);
        }

        private static string FontFamilyName()
        {
            foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
                using (var f = new Font(name, 12f))
                    if (f.Name == name) return name;
            return SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        private static float Measure(Graphics g, List<Piece> line, Font font, StringFormat format)
        {
            var w = 0f;
            foreach (var p in line) w += g.MeasureString(p.Text, font, PointF.Empty, format).Width;
            return w;
        }

        private static void Paint(Graphics g, List<Piece> line, Font font, StringFormat format, float x, float y, bool light, int fade)
        {
            foreach (var p in line)
            {
                using (var brush = new SolidBrush(Color.FromArgb(fade, Glyph.ToneColour(p.Tone, light))))
                    g.DrawString(p.Text, font, brush, x, y, format);
                x += g.MeasureString(p.Text, font, PointF.Empty, format).Width;
            }
        }

        // the picture onto the layered window, its alpha kept
        private void Blit(Bitmap bmp)
        {
            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memory, hBitmap);
            try
            {
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var source = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(hBitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                case WM_LBUTTONUP:
                    tray.OpenPanel();
                    return;
                case WM_RBUTTONUP:
                    tray.OpenMenu();
                    return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Stop();
            follow.Dispose();
        }

        // Windows

        private const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_HIDE = 0, ULW_ALPHA = 2, MA_NOACTIVATE = 3;
        private const int WM_MOUSEACTIVATE = 0x21, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        private const uint GW_HWNDNEXT = 2, GW_CHILD = 5;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint relation);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int how);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dst, IntPtr at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
