using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Uselim
{
    // Pair a phone, the Windows window's parts: the QR code carrying the address and the six digits, the steps, the
    // digits with their minutes left, the address as text to select beside Copy link, and the paired phones with Remove.
    // The digits pair one phone within ten minutes; a fresh code follows as one runs out.
    internal sealed class PairWindow : Window
    {
        private readonly Phone phone;
        private readonly string address;
        private readonly QrView qr = new QrView { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock digits = new TextBlock { FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock valid = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
        private readonly StackPanel phones = new StackPanel { Spacing = 6 };
        private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private string code;

        public PairWindow(Phone phone, string address)
        {
            this.phone = phone;
            this.address = address;
            Title = "Pair a phone";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var steps = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Text = "On the phone, install Tailscale and sign in with the same account. Scan this code with the Camera, or with "
                    + "Google Lens on Android, and open the link it finds: on an iPhone in Safari, then choose Share and Add to Home "
                    + "Screen; on Android in Chrome, then ⋮ and Add to Home screen. The address works by hand too: type it into the "
                    + "browser, or send it to the phone with Copy link. In the Uselim app, type the six digits below.",
            };
            var where = new SelectableTextBlock { Text = address, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var copy = new Button { Content = "Copy link" };
            copy.Click += async (s, e) =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(address + "/#pair=" + code);
                copy.Content = "Copied";
                DispatcherTimer.RunOnce(() => copy.Content = "Copy link", TimeSpan.FromSeconds(2));
            };
            var addressRow = new DockPanel();
            DockPanel.SetDock(copy, Dock.Right);
            addressRow.Children.Add(copy);
            addressRow.Children.Add(where);

            var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            stack.Children.Add(qr);
            stack.Children.Add(steps);
            stack.Children.Add(digits);
            stack.Children.Add(valid);
            stack.Children.Add(addressRow);
            stack.Children.Add(phones);
            Content = stack;

            phone.Relay.PhonesChanged = () => Dispatcher.UIThread.Post(DrawPhones);
            ShowCode(phone.OpenPairing());
            DrawPhones();
            tick.Tick += (s, e) => Tick();
            tick.Start();
            Closed += (s, e) =>
            {
                tick.Stop();
                phone.Relay.PhonesChanged = null;
                phone.ClosePairing();
            };
        }

        private void ShowCode(string fresh)
        {
            code = fresh;
            digits.Text = fresh.Substring(0, 3) + " " + fresh.Substring(3);
            qr.Code = Qr.Encode(address + "/#pair=" + fresh);
            Tick();
        }

        private void Tick()
        {
            var left = phone.PairSecondsLeft;
            if (left <= 0)
            {
                ShowCode(phone.OpenPairing());
                return;
            }
            valid.Text = "Pairs one phone · " + left / 60 + ":" + (left % 60).ToString("00") + " left";
        }

        private void DrawPhones()
        {
            phones.Children.Clear();
            var list = phone.Phones;
            if (list.Count == 0) return;
            phones.Children.Add(new TextBlock { Text = "Paired phones", FontWeight = FontWeight.SemiBold, Opacity = 0.7 });
            foreach (var p in list)
            {
                var row = new DockPanel();
                var remove = new Button { Content = "Remove" };
                var id = p.Id;
                remove.Click += (s, e) => phone.RemovePhone(id);
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = p.Name + " · paired " + Spans.Ago(p.Paired, DateTime.UtcNow), VerticalAlignment = VerticalAlignment.Center });
                phones.Children.Add(row);
            }
        }

        // the code's squares, black on white with the standard's quiet zone of four
        private sealed class QrView : Control
        {
            private Qr code;
            public Qr Code
            {
                set
                {
                    code = value;
                    InvalidateVisual();
                }
            }

            public override void Render(DrawingContext context)
            {
                var size = Math.Min(Bounds.Width, Bounds.Height);
                context.FillRectangle(Brushes.White, new Rect(0, 0, size, size));
                if (code == null) return;
                var cells = code.Size + 8;
                var unit = Math.Floor(size / cells);
                var offset = (size - unit * code.Size) / 2;
                for (var y = 0; y < code.Size; y++)
                    for (var x = 0; x < code.Size; x++)
                        if (code.Modules[y, x]) context.FillRectangle(Brushes.Black, new Rect(offset + x * unit, offset + y * unit, unit, unit));
            }
        }
    }
}
