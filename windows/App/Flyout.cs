using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Uselim
{
    // The panel a click on the readout or the tray icon opens, the Mac menu as a Windows 11 flyout: the plan, the chats
    // waiting on an answer, a card per limit with its bar, reset and time left, Open at login, Show on the taskbar, the
    // update row, Remove Uselim, and the figures' age with the version. Drawn by hand in one pass that lays out, paints and records the clickable rows.
    // It closes as it loses focus or on Escape; while open, a one second tick keeps the countdowns moving.

    internal sealed class Flyout : Form
    {
        private readonly Tray tray;
        private readonly Timer tick = new Timer { Interval = 1000 };
        private readonly List<Hit> hits = new List<Hit>();
        private Hit hover;
        private Palette palette = Palette.Current;
        private float s = 1;
        private Font body, bold, small, title;
        private DateTime hiddenAt = DateTime.MinValue;
        private const int Width0 = 360;

        private sealed class Hit
        {
            public Rectangle Box;
            public Action Act;
        }

        public Flyout(Tray tray)
        {
            this.tray = tray;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "Uselim";
            tick.Tick += (o, e) => Redraw();
            Deactivate += (o, e) => Hide();
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            MouseMove += (o, e) => Hover(e.Location);
            MouseLeave += (o, e) => Hover(new Point(-1, -1));
            MouseClick += (o, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                hits.FirstOrDefault(h => h.Box.Contains(e.Location))?.Act();
            };
            VisibleChanged += (o, e) =>
            {
                if (Visible) tick.Start();
                else
                {
                    tick.Stop();
                    hiddenAt = DateTime.UtcNow;
                }
            };
        }

        // a click on the icon while the panel is open first takes its focus away, which hides it; that same click then
        // leaves it hidden
        public bool JustHidden => (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x80; // a tool window, kept out of Alt+Tab
                p.ClassStyle |= 0x20000; // its drop shadow
                return p;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 rounds the corners; Windows 10 keeps them square
            var round = Native.DwmCornerRound;
            try { Native.DwmSetWindowAttribute(Handle, Native.DwmCornerPreference, ref round, sizeof(int)); } catch (Exception) { }
        }

        private void Fonts()
        {
            s = DeviceDpi / 96f;
            body?.Dispose(); bold?.Dispose(); small?.Dispose(); title?.Dispose();
            var family = Pick("Segoe UI Variable Text", "Segoe UI");
            body = new Font(family, 14 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            bold = new Font(family, 14 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            small = new Font(family, 12 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            title = new Font(family, 15 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private static string Pick(params string[] names)
        {
            using (var installed = new InstalledFontCollection())
                foreach (var n in names)
                    if (installed.Families.Any(f => f.Name == n)) return n;
            return SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        public void ShowNear(Point at)
        {
            palette = Palette.Current;
            Fonts();
            BackColor = palette.Back;
            var height = Pass(null);
            var screen = Screen.FromPoint(at);
            var wa = screen.WorkingArea;
            var b = screen.Bounds;
            var m = (int)(12 * s);
            var w = (int)(Width0 * s);
            height = Math.Min(height, wa.Height - 2 * m);
            var x = Math.Max(wa.Left + m, Math.Min(at.X - w / 2, wa.Right - w - m));
            var y = wa.Bottom - height - m;
            if (wa.Top > b.Top) y = wa.Top + m;
            if (wa.Right < b.Right) x = wa.Right - w - m;
            if (wa.Left > b.Left) x = wa.Left + m;
            Bounds = new Rectangle(x, y, w, height);
            Show();
            // a tray click lends the app the foreground, so the panel takes focus and closes as it loses it
            Native.SetForegroundWindow(Handle);
            Activate();
        }

        // a fresh layout at the current height, the bottom edge held where it sits
        public void Redraw()
        {
            if (!Visible) return;
            palette = Palette.Current;
            BackColor = palette.Back;
            var height = Pass(null);
            if (height != Height)
            {
                var bottom = Bottom;
                var wa = Screen.FromControl(this).WorkingArea;
                var top = wa.Top > Screen.FromControl(this).Bounds.Top ? Top : bottom - height;
                Bounds = new Rectangle(Left, top, Width, height);
            }
            Invalidate();
        }

        private void Hover(Point p)
        {
            var next = hits.FirstOrDefault(h => h.Box.Contains(p));
            if (next?.Box == hover?.Box) return;
            hover = next;
            Cursor = next == null ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(palette.Back);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Pass(e.Graphics);
            using (var line = new Pen(palette.CardLine))
                e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
        }

        // One pass: given a Graphics it paints, given null it measures; either way it returns the height and records the rows
        private int Pass(Graphics g)
        {
            if (body == null) Fonts();
            hits.Clear();
            var now = DateTime.UtcNow;
            var w = (int)(Width0 * s);
            int P(float v) => (int)Math.Round(v * s);
            var pad = P(18);
            var cardX = P(12);
            var cardW = w - 2 * cardX;
            var y = P(14);

            var plan = tray.PlanTitle;
            if (plan != null)
            {
                Say(g, plan, title, palette.Text, new Rectangle(pad, y, w - 2 * pad, P(22)));
                y += P(28);
            }

            var note = tray.Note;
            if (note != null)
            {
                var noteBox = new Rectangle(pad, y, w - 2 * pad - (tray.CanRetry ? P(90) : 0), P(36));
                Say(g, note, small, palette.Sub, noteBox, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                if (tray.CanRetry) Link(g, "Read again", new Rectangle(w - pad - P(84), y, P(84), P(18)), () => tray.Retry(), true);
                y += P(42);
            }

            foreach (var wait in tray.Waits.Take(5))
            {
                var box = new Rectangle(cardX, y, cardW, P(54));
                Card(g, box);
                var dot = P(7);
                if (g != null)
                    using (var orange = new SolidBrush(palette.Orange))
                        g.FillEllipse(orange, box.X + P(12), box.Y + P(15), dot, dot);
                var headX = box.X + P(12) + dot + P(6);
                var waited = Spans.Ago(wait.At, now);
                Say(g, wait.Project + " · " + wait.Word, bold, palette.Text, new Rectangle(headX, box.Y + P(8), box.Right - P(12) - headX - P(70), P(20)));
                Say(g, waited, small, palette.Sub, new Rectangle(box.Right - P(82), box.Y + P(9), P(70), P(18)), TextFormatFlags.Right);
                var line = string.Join(" · ", new[] { wait.Began, wait.Detail }.Where(t => !string.IsNullOrEmpty(t)));
                Say(g, line, small, palette.Sub, new Rectangle(box.X + P(12), box.Y + P(30), box.Width - P(24), P(18)));
                y += box.Height + P(8);
            }
            if (tray.Waits.Count > 0) y += P(4);

            var stale = tray.Stale;
            foreach (var limit in tray.Limits)
            {
                var box = new Rectangle(cardX, y, cardW, P(66));
                Card(g, box);
                var colour = palette.Of(Usage.ToneOf(limit), stale);
                var inner = new Rectangle(box.X + P(12), box.Y, box.Width - P(24), box.Height);
                Say(g, limit.Name, body, stale ? palette.Sub : palette.Text, new Rectangle(inner.X, box.Y + P(9), inner.Width - P(60), P(20)));
                Say(g, limit.Percent + "%", bold, Usage.ToneOf(limit) == Tone.Normal && !stale ? palette.Text : colour,
                    new Rectangle(inner.Right - P(60), box.Y + P(9), P(60), P(20)), TextFormatFlags.Right);
                var bar = new Rectangle(inner.X, box.Y + P(34), inner.Width, Math.Max(2, P(4)));
                Bar(g, bar, limit.Percent, colour);
                if (limit.Resets.HasValue)
                {
                    Say(g, Spans.ResetText(limit.Resets.Value, now), small, palette.Sub, new Rectangle(inner.X, box.Y + P(43), inner.Width - P(90), P(18)));
                    Say(g, "in " + Spans.Exact(limit.Resets.Value, now), small, palette.Sub, new Rectangle(inner.Right - P(90), box.Y + P(43), P(90), P(18)), TextFormatFlags.Right);
                }
                y += box.Height + P(6);
            }

            y += P(6);
            Rule(g, y, w);
            y += P(6);

            // the phone: its switch, the live step under it (a click takes that step's way forward), and Pair a phone…
            // once Tailscale serves the relay
            var phoneRow = new Rectangle(0, y, w, P(40));
            var phoneOn = tray.PhoneOn;
            Row(phoneRow, () => tray.SetPhone(!phoneOn));
            Say(g, "Phone", body, palette.Text, new Rectangle(pad, y + P(10), w - 2 * pad - P(56), P(20)));
            Switch(g, new Rectangle(w - pad - P(40), y + P(10), P(40), P(20)), phoneOn);
            y += phoneRow.Height;
            var step = tray.PhoneLine;
            if (step != null)
            {
                Link(g, step, new Rectangle(pad, y, w - 2 * pad, P(28)), tray.PhoneAct, false);
                y += P(28);
            }
            if (tray.CanPair)
            {
                Link(g, "Pair a phone…", new Rectangle(pad, y, w - 2 * pad, P(32)), tray.OpenPair, false);
                y += P(32);
            }
            Rule(g, y + P(4), w);
            y += P(10);

            // Open at login, its switch on the right
            var loginRow = new Rectangle(0, y, w, P(40));
            var canLogin = Install.IsInstalledCopy;
            var isOn = Install.OpenAtLogin;
            if (canLogin) Row(loginRow, () => tray.SetOpenAtLogin(!isOn));
            Say(g, "Open at login", body, canLogin ? palette.Text : palette.Sub, new Rectangle(pad, y + P(10), w - 2 * pad - P(56), P(20)));
            Switch(g, new Rectangle(w - pad - P(40), y + P(10), P(40), P(20)), isOn);
            y += loginRow.Height;

            // the readout on the taskbar, the Mac's two lines
            var taskbarRow = new Rectangle(0, y, w, P(40));
            var onTaskbar = tray.OnTaskbar;
            Row(taskbarRow, () => tray.SetOnTaskbar(!onTaskbar));
            Say(g, "Show on the taskbar", body, palette.Text, new Rectangle(pad, y + P(10), w - 2 * pad - P(56), P(20)));
            Switch(g, new Rectangle(w - pad - P(40), y + P(10), P(40), P(20)), onTaskbar);
            y += taskbarRow.Height;

            Rule(g, y + P(4), w);
            y += P(10);

            var update = tray.UpdateLine ?? (tray.Updater.Newest != null ? "Update to " + tray.Updater.Newest : null);
            if (update != null)
            {
                Link(g, update, new Rectangle(pad, y, w - 2 * pad, P(34)), tray.Updater.IsBusy ? (Action)null : tray.RunUpdate, false);
                y += P(34);
            }
            if (Install.IsInstalledCopy)
            {
                Link(g, "Remove Uselim…", new Rectangle(pad, y, w - 2 * pad, P(34)), tray.ConfirmRemove, false);
                y += P(34);
            }

            var age = tray.Fetched.HasValue ? "Updated " + Spans.Ago(tray.Fetched.Value, now) : "";
            Say(g, age, small, palette.Sub, new Rectangle(pad, y + P(6), P(200), P(18)));
            Say(g, "Uselim " + Usage.Version, small, palette.Sub, new Rectangle(w - pad - P(140), y + P(6), P(140), P(18)), TextFormatFlags.Right);
            y += P(34);
            return y;
        }

        private void Row(Rectangle box, Action act)
        {
            hits.Add(new Hit { Box = box, Act = act });
        }

        private void Say(Graphics g, string text, Font font, Color colour, Rectangle box, TextFormatFlags extra = 0)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | extra;
            if ((extra & TextFormatFlags.WordBreak) == 0) flags |= TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, text, font, box, colour, flags);
        }

        // a row that acts, in the accent colour, with the hover's tint behind it
        private void Link(Graphics g, string text, Rectangle box, Action act, bool right)
        {
            var row = right ? box : new Rectangle(0, box.Y, (int)(Width0 * s), box.Height);
            if (act != null) Row(row, act);
            if (g != null && act != null && hover != null && hover.Box == row)
                using (var tint = new SolidBrush(palette.Hover))
                    g.FillRectangle(tint, row);
            Say(g, text, body, act == null ? palette.Sub : palette.Accent, box, right ? TextFormatFlags.Right : 0);
        }

        private void Card(Graphics g, Rectangle box)
        {
            if (g == null) return;
            using (var path = Rounded(box, 6 * s))
            using (var fill = new SolidBrush(palette.Card))
            using (var line = new Pen(palette.CardLine))
            {
                g.FillPath(fill, path);
                g.DrawPath(line, path);
            }
        }

        private void Bar(Graphics g, Rectangle box, int percent, Color colour)
        {
            if (g == null) return;
            var r = box.Height / 2f;
            using (var track = Rounded(box, r))
            using (var back = new SolidBrush(palette.Track))
                g.FillPath(back, track);
            if (percent <= 0) return;
            var filled = new Rectangle(box.X, box.Y, Math.Max(box.Height, (int)(box.Width * Math.Min(100, percent) / 100f)), box.Height);
            using (var path = Rounded(filled, r))
            using (var fill = new SolidBrush(colour))
                g.FillPath(fill, path);
        }

        private void Switch(Graphics g, Rectangle box, bool on)
        {
            if (g == null) return;
            var r = box.Height / 2f;
            using (var path = Rounded(box, r))
            {
                if (on)
                    using (var fill = new SolidBrush(palette.Accent)) g.FillPath(fill, path);
                else
                    using (var line = new Pen(palette.Sub, Math.Max(1, s))) g.DrawPath(line, path);
            }
            var knob = box.Height * (on ? 0.6f : 0.5f);
            var kx = on ? box.Right - box.Height / 2f - knob / 2 : box.X + box.Height / 2f - knob / 2;
            var ky = box.Y + (box.Height - knob) / 2f;
            using (var k = new SolidBrush(on ? (Palette.AppsLight ? Color.White : Color.Black) : palette.Sub))
                g.FillEllipse(k, kx, ky, knob, knob);
        }

        private void Rule(Graphics g, int y, int w)
        {
            if (g == null) return;
            using (var line = new Pen(palette.Line))
                g.DrawLine(line, 0, y, w, y);
        }

        private static GraphicsPath Rounded(Rectangle box, float r)
        {
            var d = Math.Min(2 * r, Math.Min(box.Width, box.Height));
            var path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(box);
                return path;
            }
            path.AddArc(box.X, box.Y, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose();
                body?.Dispose(); bold?.Dispose(); small?.Dispose(); title?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
