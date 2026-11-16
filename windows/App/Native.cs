using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Uselim
{
    // The few Windows calls WinForms leaves out
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetConsoleMode(IntPtr handle, out int mode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WriteConsoleW(IntPtr handle, string text, int length, out int written, IntPtr reserved);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal const int StdOutput = -11;
        internal const int DwmCornerPreference = 33;
        internal const int DwmCornerRound = 2;
    }

    // stdin and stdout for the command forms: a hook's event arrives as UTF-8 on a pipe, and the answer goes back the
    // same way; a person's terminal gets its text through the console, in full Unicode
    internal static class Stdio
    {
        private static bool attached;

        public static string ReadIn()
        {
            try
            {
                using (var input = Console.OpenStandardInput())
                using (var buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        // a terminal that started this exe shows its lines; a pipe gets them as they are
        public static void Attach()
        {
            if (attached) return;
            attached = true;
            var handle = Native.GetStdHandle(Native.StdOutput);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) Native.AttachConsole(-1);
        }

        public static void Write(string text)
        {
            var handle = Native.GetStdHandle(Native.StdOutput);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (Native.GetConsoleMode(handle, out _))
            {
                Native.WriteConsoleW(handle, text, text.Length, out _, IntPtr.Zero);
                return;
            }
            try
            {
                using (var output = Console.OpenStandardOutput())
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
            }
            catch (Exception) { }
        }

        public static void Line(string text) => Write(text + "\n");
    }
}
