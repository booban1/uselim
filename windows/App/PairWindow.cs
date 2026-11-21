using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Uselim
{
    // Pair a phone, the Mac's PairWindow on Windows: the relay's address with fresh pairing digits in its fragment as a
    // QR code, the six digits in large type for typing (two groups of three) with Copy beside them and how long they
    // still pair, the address as text to select or copy as a link that pairs on opening (for an Android phone whose
    // camera leaves QR codes to Google Lens), the phone's steps for an iPhone and for Android, and the phones paired so
    // far, each with Remove. The
    // digits pair one phone within ten minutes while the window stays open; once a phone pairs, or the minutes run out,
    // fresh digits take their place. Closing the window ends the digits on show.

    internal sealed class PairWindow : Form
    {
        private readonly Relay relay;
        private readonly Loop loop;
        private readonly string address;
        private readonly PictureBox picture = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage };
        private readonly Label codeLabel = new Label { AutoSize = true, Font = new Font("Consolas", 26f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        private readonly LinkLabel copy = new LinkLabel { AutoSize = true, Text = "Copy" };
        private readonly Label valid = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly FlowLayoutPanel phonesBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        private readonly LinkLabel copyLink = new LinkLabel { AutoSize = true, Text = "Copy link" };
        private readonly Timer tick = new Timer { Interval = 1000 };
        private string code = "";
        private int ticks;

        public PairWindow(Relay relay, Loop loop, string address)
        {
            this.relay = relay;
            this.loop = loop;
            this.address = address;
            Text = "Pair a phone";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            try { Icon = Icon.ExtractAssociatedIcon(Install.ThisExe); } catch (Exception) { }

            var width = LogicalToDeviceUnits(300);
            picture.Size = new Size(LogicalToDeviceUnits(220), LogicalToDeviceUnits(220));
            picture.AccessibleName = "QR code";
            copy.LinkClicked += (o, e) => CopyText(code, copy, "Copy");
            copy.AccessibleName = "Copy the code";

            var codeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None };
            codeRow.Controls.Add(codeLabel);
            copy.Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(16), 0, 0);
            codeRow.Controls.Add(copy);

            var steps = new Label
            {
                AutoSize = true, MaximumSize = new Size(width, 0),
                Text = "On the phone, install Tailscale and sign in with the same account. Scan this code with the Camera, or with "
                    + "Google Lens on Android, and open the link it finds: on an iPhone in Safari, then choose Share and Add to Home "
                    + "Screen; on Android in Chrome, then ⋮ and Add to Home screen, then Install. The address above opens Uselim "
                    + "too: type it into the browser, or send it to the phone with Copy link. In the Uselim app, type the six "
                    + "digits, or tap the scan icon in the code field and point it here.",
            };
            // the address as text that selects and copies, beside a Copy link that carries the digits too
            var addressBox = new TextBox
            {
                Text = address, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Width = LogicalToDeviceUnits(230), TabStop = false, AccessibleName = "Uselim's address",
            };
            copyLink.LinkClicked += (o, e) => CopyText(address + "/#pair=" + code, copyLink, "Copy link");
            copyLink.AccessibleName = "Copy the pairing link";
            var addressRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            addressBox.Margin = new Padding(0, LogicalToDeviceUnits(3), LogicalToDeviceUnits(8), 0);
            addressRow.Controls.Add(addressBox);
            addressRow.Controls.Add(copyLink);

            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
                Padding = new Padding(LogicalToDeviceUnits(30), LogicalToDeviceUnits(24), LogicalToDeviceUnits(30), LogicalToDeviceUnits(24)),
            };
            foreach (var control in new Control[] { picture, codeRow, valid, addressRow, steps, phonesBox })
            {
                control.Anchor = AnchorStyles.None;
                control.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(control == codeRow ? 4 : 14));
                stack.Controls.Add(control);
            }
            Controls.Add(stack);

            relay.PhonesChanged = () => BeginInvokeSafe(PhonesChanged);
            Show(loop.Invoke(relay.OpenPairing));
            DrawPhones();
            tick.Tick += (o, e) => Tick();
            tick.Start();
        }

        private void BeginInvokeSafe(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (Exception) { }
        }

        // fresh digits: the QR code, the digits in two groups of three, and the time they pair
        private void Show(string fresh)
        {
            code = fresh;
            picture.Image?.Dispose();
            picture.Image = Draw(Qr.Encode(address + "/#pair=" + fresh), picture.Width);
            codeLabel.Text = fresh.Substring(0, 3) + " " + fresh.Substring(3);
            Tick();
        }

        // the code's squares at whole pixels, so they stay sharp, on white with the standard quiet zone
        private static Bitmap Draw(Qr qr, int side)
        {
            var cells = qr.Size + 8;
            var scale = Math.Max(1, side / cells);
            var image = new Bitmap(cells * scale, cells * scale);
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.White);
                for (var y = 0; y < qr.Size; y++)
                    for (var x = 0; x < qr.Size; x++)
                        if (qr.Modules[y, x]) g.FillRectangle(Brushes.Black, (x + 4) * scale, (y + 4) * scale, scale, scale);
            }
            return image;
        }

        // each second, the time the digits still pair; past it, fresh digits; every half minute, the list's last sights
        private void Tick()
        {
            var left = loop.Invoke(() => relay.PairCode == null ? 0 : relay.PairSecondsLeft);
            if (left == 0)
            {
                Show(loop.Invoke(relay.OpenPairing));
                return;
            }
            valid.Text = "Pairs one phone · " + left / 60 + ":" + (left % 60).ToString("00") + " left";
            ticks++;
            if (ticks % 30 == 0) DrawPhones();
        }

        // a phone paired, fresh digits for the next one; or one left the list
        private void PhonesChanged()
        {
            if (loop.Invoke(() => relay.PairCode == null)) Show(loop.Invoke(relay.OpenPairing));
            DrawPhones();
        }

        // the phones paired so far, the section hidden while there are none
        private void DrawPhones()
        {
            phonesBox.SuspendLayout();
            foreach (Control old in phonesBox.Controls.Cast<Control>().ToList()) old.Dispose();
            phonesBox.Controls.Clear();
            var phones = loop.Invoke(() => relay.Phones);
            phonesBox.Visible = phones.Count > 0;
            if (phones.Count > 0)
            {
                phonesBox.Controls.Add(new Label { AutoSize = true, Text = "Paired phones", Font = new Font(Font, FontStyle.Bold), ForeColor = SystemColors.GrayText });
                foreach (var phone in phones) phonesBox.Controls.Add(Row(phone));
            }
            phonesBox.ResumeLayout();
        }

        // a phone's row: its name, when it paired and when it was last seen, and Remove
        private Control Row(Relay.Phone phone)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Width = LogicalToDeviceUnits(300) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var words = new Label
            {
                AutoSize = true,
                Text = phone.Name + "\n" + "Paired " + Day(phone.Paired) + " · seen " + Ago(phone.Seen),
            };
            var remove = new Button { Text = "Remove", AutoSize = true, AccessibleName = "Remove " + phone.Name };
            remove.Click += (o, e) =>
            {
                var answer = MessageBox.Show(this, "It leaves Uselim and its notifications stop. Pairing it again takes a fresh code.",
                    "Remove " + phone.Name + "?", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.OK) loop.Post(() => relay.RemovePhone(phone.Id));
            };
            row.Controls.Add(words, 0, 0);
            row.Controls.Add(remove, 1, 0);
            return row;
        }

        // a day as the list names it: today, yesterday, or the date ("13 Nov")
        private static string Day(DateTime at)
        {
            var local = at.ToLocalTime().Date;
            if (local == DateTime.Today) return "today";
            if (local == DateTime.Today.AddDays(-1)) return "yesterday";
            return local.ToString("d MMM");
        }

        // a last sight: just now within two minutes, then in minutes, then in hours through the day, then its day
        private static string Ago(DateTime at)
        {
            var seconds = (DateTime.UtcNow - at).TotalSeconds;
            if (seconds < 120) return "just now";
            if (seconds < 3600) return (int)(seconds / 60) + " min ago";
            if (at.ToLocalTime().Date == DateTime.Today) return (int)(seconds / 3600) + " h ago";
            return Day(at);
        }

        // the digits, or the link carrying them, on this PC's clipboard, kept out of Windows' clipboard history and its
        // cloud sync, and off the clipboard after a minute unless something else took its place
        private void CopyText(string text, LinkLabel button, string word)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                var zero = new MemoryStream(BitConverter.GetBytes(0));
                data.SetData("CanIncludeInClipboardHistory", zero);
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, true);
                var copied = text;
                var clear = new Timer { Interval = 60_000 };
                clear.Tick += (o, e) =>
                {
                    clear.Stop();
                    clear.Dispose();
                    try { if (Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); } catch (Exception) { }
                };
                clear.Start();
                button.Text = "Copied";
                var back = new Timer { Interval = 1500 };
                back.Tick += (o, e) =>
                {
                    back.Stop();
                    back.Dispose();
                    if (!button.IsDisposed) button.Text = word;
                };
                back.Start();
            }
            catch (Exception e)
            {
                Log.Note("pair: copy failed, " + e.Message);
            }
        }

        // closing: the digits on show pair nothing more
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            tick.Dispose();
            loop.Post(() =>
            {
                relay.ClosePairing();
                relay.PhonesChanged = null;
            });
            picture.Image?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
