using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Uselim
{
    // Uselim's place in the tray: a StatusNotifierItem on the session bus, the standard KDE, GNOME's AppIndicator
    // extension and Cinnamon's xapp-sn-watcher all host. It carries the asterisk icon in pixels, the label beside it
    // ("9% · 79%", Ayatana's label, which GNOME and Cinnamon show and KDE leaves out) and the tooltip. A click on the
    // icon calls Activate, a right click ContextMenu; both open the panel, which holds every action, so the item carries
    // zero menu of its own.

    internal sealed class TrayItem : IPathMethodHandler
    {
        private const string ItemInterface = "org.kde.StatusNotifierItem";
        private const string ItemPath = "/StatusNotifierItem";
        private DBusConnection connection;
        private string busName;
        private readonly object gate = new object();

        private List<Pixmap> icon = new List<Pixmap>();
        private string label = "", tipTitle = "Uselim", tipBody = "";

        // a click, at the screen point the host names (zero on Wayland)
        public Action<int, int> Clicked;

        public string Path => ItemPath;
        public bool HandlesChildPaths => false;

        public sealed class Pixmap
        {
            public int Width, Height;
            public byte[] Argb;
        }

        public async Task Start()
        {
            try
            {
                connection = await Desktop.Bus();
                connection.AddMethodHandler(this);
                busName = "org.kde.StatusNotifierItem-" + Environment.ProcessId + "-1";
                await connection.RequestNameAsync(busName, RequestNameOptions.Default);
                _ = RegisterUntilHosted();
            }
            catch (Exception e)
            {
                Log.Note("tray: " + e.Message);
            }
        }

        // the watcher may start after Uselim (a login's order): register, retried every five seconds until one answers
        private async Task RegisterUntilHosted()
        {
            for (var tries = 0; tries < 120; tries++)
            {
                try
                {
                    MessageBuffer message;
                    using (var writer = connection.GetMessageWriter())
                    {
                        writer.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                            "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s", MessageFlags.None);
                        writer.WriteString(busName);
                        message = writer.CreateMessage();
                    }
                    await connection.CallMethodAsync(message);
                    Log.Note("tray: registered");
                    return;
                }
                catch (Exception e)
                {
                    if (tries == 0) Log.Note("tray: no watcher yet, " + e.Message);
                }
                await Task.Delay(5000);
            }
        }

        // the icon, the label and the tooltip; hosts redraw on each signal
        public void Update(List<Pixmap> pixmaps, string newLabel, string title, string body)
        {
            bool iconChanged, labelChanged, tipChanged;
            lock (gate)
            {
                iconChanged = !SameIcon(icon, pixmaps);
                labelChanged = newLabel != label;
                tipChanged = title != tipTitle || body != tipBody;
                icon = pixmaps;
                label = newLabel;
                tipTitle = title;
                tipBody = body;
            }
            if (connection == null) return;
            if (iconChanged) Signal("NewIcon", null);
            if (labelChanged) Signal("XAyatanaNewLabel", w => { w.WriteString(newLabel); w.WriteString(Guide); });
            if (tipChanged) Signal("NewToolTip", null);
        }

        // the label's widest form, so hosts that size by it keep the clock still
        private const string Guide = "100% · 100%";

        private static bool SameIcon(List<Pixmap> a, List<Pixmap> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].Width != b[i].Width || !a[i].Argb.AsSpan().SequenceEqual(b[i].Argb)) return false;
            return true;
        }

        private void Signal(string member, Action<MessageWriter> body)
        {
            try
            {
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteSignalHeader(null, ItemPath, ItemInterface, member, body == null ? null : "ss");
                    body?.Invoke(writer);
                    connection.TrySendMessage(writer.CreateMessage());
                }
            }
            catch (Exception e)
            {
                Log.Note("tray signal " + member + ": " + e.Message);
            }
        }

        // Calls from the host

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            var iface = request.InterfaceAsString;
            var member = request.MemberAsString;
            try
            {
                if (context.IsDBusIntrospectRequest)
                {
                    context.ReplyIntrospectXml(new[] { new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(Introspection)) }, Array.Empty<string>());
                    return default;
                }
                if (iface == "org.freedesktop.DBus.Properties")
                {
                    if (member == "GetAll")
                    {
                        using (var writer = context.CreateReplyWriter("a{sv}"))
                        {
                            writer.WriteDictionary(Names.Select(n => new KeyValuePair<string, VariantValue>(n, Value(n))));
                            context.Reply(writer.CreateMessage());
                        }
                        return default;
                    }
                    if (member == "Get")
                    {
                        var reader = request.GetBodyReader();
                        reader.ReadString();
                        var name = reader.ReadString();
                        if (Array.IndexOf(Names, name) < 0)
                        {
                            context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                            return default;
                        }
                        using (var writer = context.CreateReplyWriter("v"))
                        {
                            writer.WriteVariant(Value(name));
                            context.Reply(writer.CreateMessage());
                        }
                        return default;
                    }
                    context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", member);
                    return default;
                }
                if (iface == ItemInterface)
                {
                    if (member == "Activate" || member == "SecondaryActivate" || member == "ContextMenu")
                    {
                        var reader = request.GetBodyReader();
                        var x = reader.ReadInt32();
                        var y = reader.ReadInt32();
                        Clicked?.Invoke(x, y);
                    }
                    if (!context.NoReplyExpected)
                        using (var writer = context.CreateReplyWriter(null))
                            context.Reply(writer.CreateMessage());
                    return default;
                }
                context.ReplyUnknownMethodError();
            }
            catch (Exception e)
            {
                Log.Note("tray call " + member + ": " + e.Message);
                if (!context.ReplySent) context.ReplyError("org.freedesktop.DBus.Error.Failed", e.Message);
            }
            return default;
        }

        private static readonly string[] Names =
        {
            "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName",
            "AttentionIconName", "ToolTip", "ItemIsMenu", "Menu", "XAyatanaLabel", "XAyatanaLabelGuide",
        };

        // one property's value, built from the library's own types: its single-value variant writers produced
        // messages the session bus refused, and it closed the connection on each (Mint 22.3, Tmds.DBus.Protocol 0.95.2)
        private VariantValue Value(string name)
        {
            List<Pixmap> pixmaps;
            string text, title, body;
            lock (gate)
            {
                pixmaps = icon;
                text = label;
                title = tipTitle;
                body = tipBody;
            }
            switch (name)
            {
                case "Category": return VariantValue.String("ApplicationStatus");
                case "Id": return VariantValue.String("uselim");
                case "Title": return VariantValue.String("Uselim");
                case "Status": return VariantValue.String("Active");
                case "WindowId": return VariantValue.Int32(0);
                case "IconPixmap": return Pixmaps(pixmaps);
                case "ToolTip": return VariantValue.Struct(VariantValue.String(""), Pixmaps(new List<Pixmap>()), VariantValue.String(title), VariantValue.String(body));
                case "ItemIsMenu": return VariantValue.Bool(false);
                case "Menu": return VariantValue.ObjectPath(new ObjectPath("/NO_DBUSMENU"));
                case "XAyatanaLabel": return VariantValue.String(text);
                case "XAyatanaLabelGuide": return VariantValue.String(Guide);
                default: return VariantValue.String("");
            }
        }

        // a(iiay): width, height and ARGB bytes per size
        private static VariantValue Pixmaps(List<Pixmap> pixmaps)
        {
            var array = new Array<Struct<int, int, Array<byte>>>();
            foreach (var p in pixmaps) array.Add(Struct.Create(p.Width, p.Height, new Array<byte>(p.Argb)));
            return array;
        }

        private const string Introspection = @"<interface name=""org.kde.StatusNotifierItem"">
  <property name=""Category"" type=""s"" access=""read""/>
  <property name=""Id"" type=""s"" access=""read""/>
  <property name=""Title"" type=""s"" access=""read""/>
  <property name=""Status"" type=""s"" access=""read""/>
  <property name=""WindowId"" type=""i"" access=""read""/>
  <property name=""IconName"" type=""s"" access=""read""/>
  <property name=""IconPixmap"" type=""a(iiay)"" access=""read""/>
  <property name=""OverlayIconName"" type=""s"" access=""read""/>
  <property name=""AttentionIconName"" type=""s"" access=""read""/>
  <property name=""ToolTip"" type=""(sa(iiay)ss)"" access=""read""/>
  <property name=""ItemIsMenu"" type=""b"" access=""read""/>
  <property name=""Menu"" type=""o"" access=""read""/>
  <property name=""XAyatanaLabel"" type=""s"" access=""read""/>
  <property name=""XAyatanaLabelGuide"" type=""s"" access=""read""/>
  <method name=""Activate""><arg name=""x"" type=""i"" direction=""in""/><arg name=""y"" type=""i"" direction=""in""/></method>
  <method name=""SecondaryActivate""><arg name=""x"" type=""i"" direction=""in""/><arg name=""y"" type=""i"" direction=""in""/></method>
  <method name=""ContextMenu""><arg name=""x"" type=""i"" direction=""in""/><arg name=""y"" type=""i"" direction=""in""/></method>
  <method name=""Scroll""><arg name=""delta"" type=""i"" direction=""in""/><arg name=""orientation"" type=""s"" direction=""in""/></method>
  <signal name=""NewIcon""/>
  <signal name=""NewToolTip""/>
  <signal name=""NewStatus""><arg type=""s""/></signal>
  <signal name=""XAyatanaNewLabel""><arg type=""s""/><arg type=""s""/></signal>
</interface>
";
    }
}
