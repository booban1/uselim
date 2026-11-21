using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace Uselim
{
    // Uselim's place in Cinnamon's tray, Linux Mint's own: a native XApp status icon. Cinnamon's applet shows its Label
    // beside the icon, the "9% · 79%" the tray standard's bridge leaves out. The icon owns org.x.StatusIcon.uselim,
    // lists itself through an object manager at /org/x/StatusIcon, and serves org.x.StatusIcon on /org/x/StatusIcon/Icon:
    // the icon as a PNG file path, the label, the tooltip; changes go out as PropertiesChanged. A release of the first
    // or third button opens the panel at the point the applet names.

    internal sealed class XAppIcon
    {
        private const string Iface = "org.x.StatusIcon";
        private const string ManagerPath = "/org/x/StatusIcon";
        private const string IconPath = "/org/x/StatusIcon/Icon";
        private DBusConnection connection;
        private readonly object gate = new object();
        private string iconFile = "", label = "", tooltip = "Uselim";
        private int iconSize = 24;
        public Action<int, int> Clicked;

        // the applet's presence: XApp icons export natively while a name with this prefix sits on the bus
        public static async Task<bool> IsHosted()
        {
            try
            {
                var bus = await Desktop.Bus();
                return (await bus.ListServicesAsync()).Any(n => n.StartsWith("org.x.StatusIconMonitor", StringComparison.Ordinal));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task Start()
        {
            try
            {
                connection = await Desktop.Bus();
                connection.AddMethodHandler(new Manager(this));
                connection.AddMethodHandler(new Icon(this));
                await connection.RequestNameAsync("org.x.StatusIcon.uselim", RequestNameOptions.Default);
                Log.Note("tray: XApp icon");
            }
            catch (Exception e)
            {
                Log.Note("tray: " + e.Message);
            }
        }

        private static string Folder
        {
            get
            {
                var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                return Path.Combine(!string.IsNullOrEmpty(cache) && cache.StartsWith("/") ? cache : Path.Combine(Paths.Home, ".cache"), "uselim");
            }
        }

        // the icon as a PNG named by its look, so a new look is a new path the applet loads fresh
        private static string WriteIcon(TrayItem.Pixmap p, string look)
        {
            var path = Path.Combine(Folder, "icon-" + look + ".png");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Folder);
            using (var bitmap = new SKBitmap(new SKImageInfo(p.Width, p.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
            {
                var rgba = new byte[p.Argb.Length];
                for (var i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = p.Argb[i + 1];
                    rgba[i + 1] = p.Argb[i + 2];
                    rgba[i + 2] = p.Argb[i + 3];
                    rgba[i + 3] = p.Argb[i];
                }
                System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
                using (var image = SKImage.FromBitmap(bitmap))
                using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var file = File.Create(path))
                    png.SaveTo(file);
            }
            return path;
        }

        public void Update(TrayItem.Pixmap icon, string look, string newLabel, string newTooltip)
        {
            string file;
            try { file = WriteIcon(icon, look); }
            catch (Exception e)
            {
                Log.Note("tray icon file: " + e.Message);
                return;
            }
            var changed = new List<KeyValuePair<string, VariantValue>>();
            lock (gate)
            {
                if (file != iconFile) changed.Add(new KeyValuePair<string, VariantValue>("IconName", VariantValue.String(file)));
                if (newLabel != label) changed.Add(new KeyValuePair<string, VariantValue>("Label", VariantValue.String(newLabel)));
                if (newTooltip != tooltip) changed.Add(new KeyValuePair<string, VariantValue>("TooltipText", VariantValue.String(newTooltip)));
                iconFile = file;
                label = newLabel;
                tooltip = newTooltip;
            }
            if (connection == null || changed.Count == 0) return;
            try
            {
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteSignalHeader(null, IconPath, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as");
                    writer.WriteString(Iface);
                    writer.WriteDictionary(changed);
                    writer.WriteArray(new string[0]);
                    connection.TrySendMessage(writer.CreateMessage());
                }
            }
            catch (Exception e)
            {
                Log.Note("tray signal: " + e.Message);
            }
        }

        private List<KeyValuePair<string, VariantValue>> Properties()
        {
            lock (gate)
                return new List<KeyValuePair<string, VariantValue>>
                {
                    new KeyValuePair<string, VariantValue>("Name", VariantValue.String("uselim")),
                    new KeyValuePair<string, VariantValue>("IconName", VariantValue.String(iconFile)),
                    new KeyValuePair<string, VariantValue>("TooltipText", VariantValue.String(tooltip)),
                    new KeyValuePair<string, VariantValue>("Label", VariantValue.String(label)),
                    new KeyValuePair<string, VariantValue>("Visible", VariantValue.Bool(true)),
                    new KeyValuePair<string, VariantValue>("IconSize", VariantValue.Int32(iconSize)),
                    new KeyValuePair<string, VariantValue>("PrimaryMenuIsOpen", VariantValue.Bool(false)),
                    new KeyValuePair<string, VariantValue>("SecondaryMenuIsOpen", VariantValue.Bool(false)),
                    new KeyValuePair<string, VariantValue>("Metadata", VariantValue.String("")),
                };
        }

        // /org/x/StatusIcon: the object manager listing the one icon
        private sealed class Manager : IPathMethodHandler
        {
            private readonly XAppIcon owner;
            public Manager(XAppIcon owner) => this.owner = owner;
            public string Path => ManagerPath;
            public bool HandlesChildPaths => false;

            public ValueTask HandleMethodAsync(MethodContext context)
            {
                try
                {
                    if (context.IsDBusIntrospectRequest)
                    {
                        context.ReplyIntrospectXml(new[] { new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(
                            "<interface name=\"org.freedesktop.DBus.ObjectManager\"><method name=\"GetManagedObjects\"><arg type=\"a{oa{sa{sv}}}\" direction=\"out\"/></method>"
                            + "<signal name=\"InterfacesAdded\"><arg type=\"o\"/><arg type=\"a{sa{sv}}\"/></signal><signal name=\"InterfacesRemoved\"><arg type=\"o\"/><arg type=\"as\"/></signal></interface>")) },
                            new[] { "Icon" });
                        return default;
                    }
                    if (context.Request.InterfaceAsString == "org.freedesktop.DBus.ObjectManager" && context.Request.MemberAsString == "GetManagedObjects")
                    {
                        using (var writer = context.CreateReplyWriter("a{oa{sa{sv}}}"))
                        {
                            var objects = writer.WriteDictionaryStart();
                            writer.WriteDictionaryEntryStart();
                            writer.WriteObjectPath(IconPath);
                            var interfaces = writer.WriteDictionaryStart();
                            writer.WriteDictionaryEntryStart();
                            writer.WriteString(Iface);
                            writer.WriteDictionary(owner.Properties());
                            writer.WriteDictionaryEnd(interfaces);
                            writer.WriteDictionaryEnd(objects);
                            context.Reply(writer.CreateMessage());
                        }
                        return default;
                    }
                    context.ReplyUnknownMethodError();
                }
                catch (Exception e)
                {
                    Log.Note("tray manager: " + e.Message);
                    if (!context.ReplySent) context.ReplyError("org.freedesktop.DBus.Error.Failed", e.Message);
                }
                return default;
            }
        }

        // /org/x/StatusIcon/Icon: org.x.StatusIcon and its properties
        private sealed class Icon : IPathMethodHandler
        {
            private readonly XAppIcon owner;
            public Icon(XAppIcon owner) => this.owner = owner;
            public string Path => IconPath;
            public bool HandlesChildPaths => false;

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
                                writer.WriteDictionary(owner.Properties());
                                context.Reply(writer.CreateMessage());
                            }
                            return default;
                        }
                        if (member == "Get")
                        {
                            var reader = request.GetBodyReader();
                            reader.ReadString();
                            var name = reader.ReadString();
                            var found = owner.Properties().FirstOrDefault(p => p.Key == name);
                            if (found.Key == null)
                            {
                                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                                return default;
                            }
                            using (var writer = context.CreateReplyWriter("v"))
                            {
                                writer.WriteVariant(found.Value);
                                context.Reply(writer.CreateMessage());
                            }
                            return default;
                        }
                        if (member == "Set")
                        {
                            // the applet sets IconSize; the PNG carries 48 px and the applet scales it
                            var reader = request.GetBodyReader();
                            reader.ReadString();
                            if (reader.ReadString() == "IconSize")
                            {
                                var value = reader.ReadVariantValue();
                                try { lock (owner.gate) owner.iconSize = value.GetInt32(); } catch (Exception) { }
                            }
                            using (var writer = context.CreateReplyWriter(null))
                                context.Reply(writer.CreateMessage());
                            return default;
                        }
                    }
                    if (iface == Iface)
                    {
                        if (member == "ButtonRelease")
                        {
                            var reader = request.GetBodyReader();
                            var x = reader.ReadInt32();
                            var y = reader.ReadInt32();
                            var button = reader.ReadUInt32();
                            if (button == 1 || button == 3) owner.Clicked?.Invoke(x, y);
                        }
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

            private const string Introspection = @"<interface name=""org.x.StatusIcon"">
  <method name=""ButtonPress""><arg type=""i"" direction=""in""/><arg type=""i"" direction=""in""/><arg type=""u"" direction=""in""/><arg type=""u"" direction=""in""/><arg type=""i"" direction=""in""/></method>
  <method name=""ButtonRelease""><arg type=""i"" direction=""in""/><arg type=""i"" direction=""in""/><arg type=""u"" direction=""in""/><arg type=""u"" direction=""in""/><arg type=""i"" direction=""in""/></method>
  <method name=""Scroll""><arg type=""i"" direction=""in""/><arg type=""i"" direction=""in""/><arg type=""u"" direction=""in""/></method>
  <property name=""Name"" type=""s"" access=""read""/>
  <property name=""IconName"" type=""s"" access=""read""/>
  <property name=""TooltipText"" type=""s"" access=""read""/>
  <property name=""Label"" type=""s"" access=""read""/>
  <property name=""Visible"" type=""b"" access=""read""/>
  <property name=""IconSize"" type=""i"" access=""readwrite""/>
  <property name=""PrimaryMenuIsOpen"" type=""b"" access=""read""/>
  <property name=""SecondaryMenuIsOpen"" type=""b"" access=""read""/>
  <property name=""Metadata"" type=""s"" access=""read""/>
</interface>
";
        }
    }
}
