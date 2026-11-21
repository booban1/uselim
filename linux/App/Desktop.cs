using System;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Uselim
{
    // The desktop's session bus: notices through org.freedesktop.Notifications, the idle time and the screen lock for the
    // phone's away reading. Each call fails quietly where the desktop lacks the service.

    internal static class Desktop
    {
        private static DBusConnection bus;
        private static readonly object gate = new object();

        public static async Task<DBusConnection> Bus()
        {
            lock (gate)
                if (bus != null) return bus;
            var fresh = new DBusConnection(DBusAddress.Session);
            await fresh.ConnectAsync();
            // the reason the bus gives when it closes, into the log
            _ = fresh.DisconnectedAsync().ContinueWith(t => { if (t.Result != null && !ReferenceEquals(bus, null) && ReferenceEquals(bus, fresh)) Log.Note("bus closed: " + t.Result.Message); });
            lock (gate)
            {
                if (bus != null)
                {
                    fresh.Dispose();
                    return bus;
                }
                bus = fresh;
                return bus;
            }
        }

        // a notice with Uselim's icon; a notice with the same replaces id takes the earlier one's place
        public static async Task<uint> Notify(string title, string body, uint replaces = 0)
        {
            try
            {
                var connection = await Bus();
                MessageBuffer message;
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader("org.freedesktop.Notifications", "/org/freedesktop/Notifications",
                        "org.freedesktop.Notifications", "Notify", "susssasa{sv}i", MessageFlags.None);
                    writer.WriteString("Uselim");
                    writer.WriteUInt32(replaces);
                    writer.WriteString("uselim");
                    writer.WriteString(title ?? "");
                    writer.WriteString(body ?? "");
                    writer.WriteArray(new string[0]);
                    writer.WriteDictionary(new[] { new System.Collections.Generic.KeyValuePair<string, VariantValue>("desktop-entry", VariantValue.String("uselim")) });
                    writer.WriteInt32(-1);
                    message = writer.CreateMessage();
                }
                return await connection.CallMethodAsync(message, (Message m, object s) => m.GetBodyReader().ReadUInt32(), null);
            }
            catch (Exception e)
            {
                Log.Note("notice failed: " + e.Message);
                return 0;
            }
        }

        public static async Task CloseNotice(uint id)
        {
            if (id == 0) return;
            try
            {
                var connection = await Bus();
                MessageBuffer message;
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader("org.freedesktop.Notifications", "/org/freedesktop/Notifications",
                        "org.freedesktop.Notifications", "CloseNotification", "u", MessageFlags.None);
                    writer.WriteUInt32(id);
                    message = writer.CreateMessage();
                }
                await connection.CallMethodAsync(message);
            }
            catch (Exception) { }
        }

        // seconds since the last input, from the screensaver services desktops carry: Cinnamon's and GNOME's report it
        // through Mutter's idle monitor or freedesktop's ScreenSaver; null where none answers
        public static async Task<double?> IdleSeconds()
        {
            var tries = new[]
            {
                new[] { "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor", "GetIdletime", "ms64" },
                new[] { "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "GetSessionIdleTime", "s32" },
                new[] { "org.cinnamon.ScreenSaver", "/org/cinnamon/ScreenSaver", "org.cinnamon.ScreenSaver", "GetActiveTime", "s32" },
            };
            foreach (var t in tries)
            {
                try
                {
                    var connection = await Bus();
                    MessageBuffer message;
                    using (var writer = connection.GetMessageWriter())
                    {
                        writer.WriteMethodCallHeader(t[0], t[1], t[2], t[3], null, MessageFlags.None);
                        message = writer.CreateMessage();
                    }
                    var kind = t[4];
                    var value = await connection.CallMethodAsync(message, (Message m, object s) =>
                    {
                        var reader = m.GetBodyReader();
                        return kind == "ms64" ? reader.ReadUInt64() / 1000.0 : reader.ReadUInt32();
                    }, null);
                    // Cinnamon's GetActiveTime counts the screensaver's time, zero while it is off
                    if (t[0] == "org.cinnamon.ScreenSaver" && value == 0) continue;
                    return value;
                }
                catch (Exception) { }
            }
            return null;
        }

        // the screen locked: Cinnamon's screensaver, else freedesktop's (GNOME answers there too)
        public static async Task<bool> IsLocked()
        {
            foreach (var t in new[]
            {
                new[] { "org.cinnamon.ScreenSaver", "/org/cinnamon/ScreenSaver", "org.cinnamon.ScreenSaver" },
                new[] { "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver" },
                new[] { "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver" },
            })
            {
                try
                {
                    var connection = await Bus();
                    MessageBuffer message;
                    using (var writer = connection.GetMessageWriter())
                    {
                        writer.WriteMethodCallHeader(t[0], t[1], t[2], "GetActive", null, MessageFlags.None);
                        message = writer.CreateMessage();
                    }
                    return await connection.CallMethodAsync(message, (Message m, object s) => m.GetBodyReader().ReadBool(), null);
                }
                catch (Exception) { }
            }
            return false;
        }
    }
}
