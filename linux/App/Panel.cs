using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Uselim
{
    // The panel, the Windows flyout's rows in the same order: the plan, the note while figures are missing, the chats
    // waiting on an answer, a card per limit with its bar and reset, then Phone with its live step and Pair a phone…,
    // Open at login, the update, Remove Uselim…, Quit, and the figures' age with the version. It opens beside the tray
    // point the host names (on X11; Wayland places it itself) and closes as it loses focus. Light or dark follows the desktop.
    internal sealed class Panel : Window
    {
        private readonly Controller owner;
        private readonly StackPanel rows = new StackPanel { Spacing = 6, Margin = new Thickness(12, 12, 12, 10) };
        private PairWindow pair;
        private const double Width0 = 340;

        public Panel(Controller owner)
        {
            this.owner = owner;
            Title = "Uselim";
            Width = Width0;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            Topmost = true;
            WindowDecorations = WindowDecorations.None;
            Content = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(0x80808080),
                CornerRadius = new CornerRadius(8),
                Child = rows,
            };
            Deactivated += (s, e) => Hide();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Hide(); };
            owner.Phone.Changed = () => Redraw();
        }

        public void ShowNear(int x, int y)
        {
            Build();
            if (x > 0 || y > 0)
            {
                var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
                var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
                var scale = screen?.Scaling ?? 1;
                var w = (int)(Width0 * scale);
                Show();
                var h = (int)(Bounds.Height * scale);
                // above a bottom panel, below a top one, inside the screen's edges
                var left = Math.Max(area.X, Math.Min(x - w / 2, area.Right - w));
                var top = y > area.Y + area.Height / 2 ? area.Bottom - h - 6 : area.Y + 6;
                Position = new PixelPoint(left, top);
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Show();
            }
            Activate();
        }

        public void Redraw()
        {
            if (IsVisible) Build();
        }

        private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));
        private static readonly IBrush Orange = Brush(0xFFF7630C);
        private static readonly IBrush Red = Brush(0xFFD13438);

        private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        private IBrush Sub => Brush(IsDark ? 0xFFA0A0A0u : 0xFF6E6E73u);
        private IBrush CardBack => Brush(IsDark ? 0xFF2C2C2Eu : 0xFFF2F2F5u);

        private IBrush ToneBrush(Tone tone, bool stale) =>
            stale ? Sub : tone == Tone.Critical ? Red : tone == Tone.Warning ? Orange : (IBrush)Brush(IsDark ? 0xFFF2F2F2u : 0xFF1B1B1Bu);

        // a line in the theme's own ink unless a brush is given
        private TextBlock Say(string text, double size = 13, IBrush brush = null, FontWeight weight = FontWeight.Normal)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            if (brush != null) block.Foreground = brush;
            return block;
        }

        private Control Link(string text, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(6, 4), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (s, e) => act();
            return b;
        }

        private Control Switch(string text, bool on, bool enabled, Action<bool> set)
        {
            var toggle = new ToggleSwitch { IsChecked = on, IsEnabled = enabled, OnContent = null, OffContent = null, HorizontalAlignment = HorizontalAlignment.Right };
            toggle.IsCheckedChanged += (s, e) => set(toggle.IsChecked == true);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 0) };
            var label = Say(text);
            label.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(label);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            return grid;
        }

        private Border Card(Control child) =>
            new Border { Background = CardBack, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9), Child = child };

        private void Build()
        {
            rows.Children.Clear();
            var now = DateTime.UtcNow;

            if (owner.PlanTitle is string plan) rows.Children.Add(Say(plan, 15, null, FontWeight.SemiBold));

            if (owner.Note is string note)
            {
                var line = new DockPanel();
                if (owner.CanRetry)
                {
                    var again = Link("Read again", () => owner.Retry());
                    DockPanel.SetDock(again, Dock.Right);
                    line.Children.Add(again);
                }
                line.Children.Add(Say(note, 12, Sub));
                rows.Children.Add(line);
            }

            foreach (var wait in owner.Waits.Take(5))
            {
                var name = owner.Phone.ChatName(wait.Session) ?? wait.Project;
                var head = new DockPanel();
                var age = Say(Spans.Ago(wait.At, now), 12, Sub);
                DockPanel.SetDock(age, Dock.Right);
                head.Children.Add(age);
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                title.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = Orange, VerticalAlignment = VerticalAlignment.Center });
                title.Children.Add(Say(name + " · " + wait.Word, 13, null, FontWeight.SemiBold));
                head.Children.Add(title);
                var detail = string.Join(" · ", new[] { wait.Began, wait.Detail }.Where(t => !string.IsNullOrEmpty(t)));
                var stack = new StackPanel { Spacing = 3 };
                stack.Children.Add(head);
                stack.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Sub, TextTrimming = TextTrimming.CharacterEllipsis });
                rows.Children.Add(Card(stack));
            }

            var stale = owner.Stale;
            foreach (var limit in owner.Limits)
            {
                var tone = Usage.ToneOf(limit);
                var top = new DockPanel();
                var percent = Say(limit.Percent + "%", 13, tone == Tone.Normal && !stale ? null : ToneBrush(tone, stale), FontWeight.SemiBold);
                DockPanel.SetDock(percent, Dock.Right);
                top.Children.Add(percent);
                top.Children.Add(Say(limit.Name, 13, stale ? Sub : null));
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Min(100, limit.Percent), Height = 4, MinHeight = 4, Foreground = ToneBrush(tone, stale), Margin = new Thickness(0, 6) };
                var stack = new StackPanel();
                stack.Children.Add(top);
                stack.Children.Add(bar);
                if (limit.Resets.HasValue)
                {
                    var bottom = new DockPanel();
                    var left = Say("in " + Spans.Exact(limit.Resets.Value, now), 12, Sub);
                    DockPanel.SetDock(left, Dock.Right);
                    bottom.Children.Add(left);
                    bottom.Children.Add(Say(Spans.ResetText(limit.Resets.Value, now), 12, Sub));
                    stack.Children.Add(bottom);
                }
                rows.Children.Add(Card(stack));
            }

            rows.Children.Add(new Separator { Margin = new Thickness(0, 4) });

            // the phone: its switch, the live step under it (a click takes that step's way forward), Pair a phone… once
            // Tailscale serves the relay
            var phone = owner.Phone;
            rows.Children.Add(Switch("Phone", phone.IsOn, true, on => { phone.Set(on); Redraw(); }));
            if (phone.IsOn && phone.Line is string step && phone.Step != PhoneStep.Ready) rows.Children.Add(Link(step, () => phone.Act()));
            if (phone.CanPair) rows.Children.Add(Link("Pair a phone…", OpenPair));

            rows.Children.Add(Switch("Open at login", Install.OpenAtLogin, Install.IsInstalledCopy, on => owner.SetOpenAtLogin(on)));

            var update = owner.UpdateLine ?? (owner.Updater.Newest != null ? "Update to " + owner.Updater.Newest : null);
            if (update != null) rows.Children.Add(Link(update, () => owner.RunUpdate()));
            if (Install.IsInstalledCopy) rows.Children.Add(Link("Remove Uselim…", ConfirmRemove));
            rows.Children.Add(Link("Quit Uselim", () => owner.Quit()));

            var foot = new DockPanel { Margin = new Thickness(6, 2, 6, 0) };
            var version = Say("Uselim " + Usage.Version, 11, Sub);
            DockPanel.SetDock(version, Dock.Right);
            foot.Children.Add(version);
            foot.Children.Add(Say(owner.Fetched.HasValue ? "Updated " + Spans.Ago(owner.Fetched.Value, now) : "", 11, Sub));
            rows.Children.Add(foot);
        }

        private void OpenPair()
        {
            Hide();
            if (pair != null)
            {
                pair.Activate();
                return;
            }
            pair = new PairWindow(owner.Phone, "https://" + owner.Phone.DnsName);
            pair.Closed += (s, e) => pair = null;
            pair.Show();
        }

        // one question first, as the Windows panel's dialog asks it
        private void ConfirmRemove()
        {
            Hide();
            var ask = new Window
            {
                Title = "Remove Uselim?", Width = 380, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            var text = Say("Uselim's hooks and status line leave Claude Code's settings, with a backup kept; the plugin, Open at login, the menu entry and ~/.local/bin/uselim go too.");
            var cancel = new Button { Content = "Cancel" };
            var remove = new Button { Content = "Remove" };
            cancel.Click += (s, e) => ask.Close();
            remove.Click += (s, e) => { ask.Close(); owner.Remove(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(remove);
            var stack = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
            stack.Children.Add(text);
            stack.Children.Add(buttons);
            ask.Content = stack;
            ask.Show();
        }
    }
}
