using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Uselim
{
    // Files from the phone, saved in relay\files\<session>\ under their own names and handed to the chat by path, the
    // Mac's Files.swift on Windows. A picture is fitted by the host (the app draws it with Windows' own codecs); every
    // other file lands as it came.

    public static class Uploads
    {
        public static string MimeType(string ext)
        {
            switch (ext)
            {
                case "jpg": return "image/jpeg";
                case "png": return "image/png";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        // the phone's file name kept to letters, digits, dots, dashes and underscores: a bare name, visible, 80
        // characters at most
        public static string SafeName(string raw)
        {
            var last = Words.LastName(raw ?? "");
            var kept = new StringBuilder();
            foreach (var c in last) kept.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '-');
            var name = Regex.Replace(kept.ToString(), "-{2,}", "-").Trim('.', '-');
            if (name.Length == 0) name = "file";
            if (name.Length <= 80) return name;
            var ext = Path.GetExtension(name).TrimStart('.');
            var stem = Path.GetFileNameWithoutExtension(name);
            return ext.Length == 0 || ext.Length > 10 ? name.Substring(0, 80) : Words.Prefix(stem, 79 - ext.Length) + "." + ext;
        }

        // saves the bytes under the file's own name, a number added where the name is taken, and returns the name it got
        public static string Save(byte[] data, string raw, string ext, string folder)
        {
            var saved = FreeName(raw, ext, folder);
            File.WriteAllBytes(Path.Combine(folder, saved), data);
            return saved;
        }

        // the same for a file the server streamed to the spool, moved into place
        public static string Move(string file, string raw, string ext, string folder)
        {
            var saved = FreeName(raw, ext, folder);
            File.Move(file, Path.Combine(folder, saved));
            return saved;
        }

        private static string FreeName(string raw, string ext, string folder)
        {
            var name = SafeName(raw);
            if (ext != null) name = Path.GetFileNameWithoutExtension(name) + "." + ext;
            Directory.CreateDirectory(folder);
            var stem = Path.GetFileNameWithoutExtension(name);
            var suffix = Path.GetExtension(name).TrimStart('.');
            var saved = name;
            for (var count = 2; File.Exists(Path.Combine(folder, saved)); count++)
                saved = suffix.Length == 0 ? stem + "-" + count : stem + "-" + count + "." + suffix;
            return saved;
        }
    }

    // A picture fitted for the chat or the phone: its bytes, or null where the file goes as it is, and its extension
    public sealed class Fitted
    {
        public byte[] Data;
        public string Ext;
    }
}
