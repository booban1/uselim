using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Uselim
{
    // A small HTTP/1.1 server on the loopback address for the phone relay, one request per connection, the Mac's
    // Server.swift on Windows. The head comes first (16 KiB at most, whole within 10 s of the connection's start) and goes
    // to `admit`, the relay's checks of Host, header, Origin and key: an answer there ends the request before any of its
    // body is read. An admitted body follows by its Content-Length or in chunks, decoded as they arrive: a file from the
    // phone streams to a file in the spool folder (200 MiB at most), any other body stays in memory (1 MiB at most). A
    // connection silent for 30 s while its request arrives closes, as does a body still arriving past its deadline (an
    // upload's ten minutes, 30 s for the rest), and 24 connections stay open at most, each one past them answered 503.
    // The route then answers, at once or later, which is how /relay/wait holds a request open; the connection closes
    // after the answer. Every step runs on the relay's loop.

    public sealed class Request
    {
        public string Method = "";
        public string Path = "";
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public byte[] Body = new byte[0];
        // an upload's body, streamed to a file in the spool folder: the route moves it away or leaves it to be removed
        public string BodyFile;
        // what let a request from the phone in at its head (a phone's id), so its route answers 401 once that key ended
        public string Key;

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string Arg(string name) => Query.TryGetValue(name, out var v) ? v : null;
    }

    public sealed class Response
    {
        public int Status;
        public string Type;
        public byte[] Body;
        // a picture under a name that keeps its bytes: the phone's browser keeps it a week
        public bool IsLasting;

        public static Response Json(object value, int status = 200) =>
            new Response { Status = status, Type = "application/json; charset=utf-8", Body = Encoding.UTF8.GetBytes(Uselim.Json.Write(value)) };

        public static Response Text(string words, int status) =>
            new Response { Status = status, Type = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(words) };
    }

    public sealed class Server
    {
        public const long UploadLimit = 200L * 1_048_576;
        private const int HeadLimit = 16_384;
        private const long BodyLimit = 1_048_576;
        private const double UploadSeconds = 600;

        private readonly int port;
        private readonly string spool;
        private readonly Loop loop;
        private readonly Func<Request, Response> admit;
        private readonly Action<Request, Action<Response>> route;
        private readonly double idleSeconds, bodySeconds, headSeconds;
        private readonly int connectionLimit;
        private TcpListener listener;
        private readonly HashSet<Exchange> open = new HashSet<Exchange>();

        public Server(int port, string spool, Loop loop, Func<Request, Response> admit, Action<Request, Action<Response>> route,
            double idleSeconds = 30, int connectionLimit = 24, double bodySeconds = 30)
        {
            this.port = port;
            this.spool = spool;
            this.loop = loop;
            this.admit = admit;
            this.route = route;
            this.idleSeconds = idleSeconds;
            this.connectionLimit = connectionLimit;
            this.bodySeconds = bodySeconds;
            headSeconds = Math.Min(10, idleSeconds);
        }

        public void Start()
        {
            // an upload cut short by a quit leaves its part behind; the spool starts empty
            try { if (Directory.Exists(spool)) Directory.Delete(spool, true); } catch (Exception) { }
            Directory.CreateDirectory(spool);
            Secure.Folder(spool);
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(64);
                Log.Note("relay: listening on 127.0.0.1:" + port);
                AcceptNext();
            }
            catch (Exception e)
            {
                Log.Note("relay: server refused to start, " + e.Message);
                listener = null;
            }
        }

        // the Phone switch off: the port closes until the next start
        public void Stop()
        {
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;
        }

        private void AcceptNext()
        {
            var l = listener;
            if (l == null) return;
            l.BeginAcceptTcpClient(result =>
            {
                TcpClient client;
                try { client = l.EndAcceptTcpClient(result); }
                catch (Exception) { return; }
                loop.Post(() => Accept(client));
                AcceptNext();
            }, null);
        }

        private void Accept(TcpClient client)
        {
            client.NoDelay = true;
            if (open.Count >= connectionLimit)
            {
                var busy = new Exchange(client, this);
                Send(busy, Response.Text("Busy", 503));
                return;
            }
            var exchange = new Exchange(client, this);
            open.Add(exchange);
            exchange.HeadTimer = loop.After(headSeconds, () => Close(exchange));
            exchange.IdleTimer = loop.After(idleSeconds, () => Close(exchange));
            Receive(exchange);
        }

        private void Receive(Exchange exchange)
        {
            if (exchange.IsClosed) return;
            var buffer = new byte[65536];
            try
            {
                exchange.Stream.BeginRead(buffer, 0, buffer.Length, result =>
                {
                    int read;
                    try { read = exchange.Stream.EndRead(result); }
                    catch (Exception) { read = -1; }
                    loop.Post(() => Received(exchange, buffer, read));
                }, null);
            }
            catch (Exception)
            {
                Close(exchange);
            }
        }

        private void Received(Exchange exchange, byte[] buffer, int read)
        {
            if (exchange.IsClosed || exchange.IsRouted) return;
            if (read <= 0)
            {
                Close(exchange);
                return;
            }
            exchange.IdleTimer?.Cancel();
            exchange.IdleTimer = loop.After(idleSeconds, () => Close(exchange));
            var answer = Take(buffer, read, exchange);
            if (answer != null)
            {
                exchange.IsRouted = true;
                exchange.StopTimers();
                Send(exchange, answer);
                return;
            }
            if (!exchange.IsRouted) Receive(exchange);
        }

        // the bytes as they arrive: the head until it is whole, then the body; an answer ends the request
        private Response Take(byte[] buffer, int count, Exchange exchange)
        {
            var data = new ArraySegment<byte>(buffer, 0, count);
            if (exchange.Head == null)
            {
                exchange.Buffer.Write(buffer, 0, count);
                var all = exchange.Buffer.ToArray();
                var end = IndexOfBlankLine(all, Math.Min(all.Length, HeadLimit + 4));
                if (end < 0) return all.Length >= HeadLimit ? Response.Text("Malformed request", 400) : null;
                var head = ParseHead(Encoding.UTF8.GetString(all, 0, end));
                if (head == null) return Response.Text("Malformed request", 400);
                exchange.HeadTimer?.Cancel();
                exchange.HeadTimer = null;
                data = new ArraySegment<byte>(all, end + 4, all.Length - end - 4);
                exchange.Buffer = new MemoryStream();
                var refusal = admit(head);
                if (refusal != null) return refusal;
                exchange.Head = head;
                refusal = exchange.Frame(head, spool);
                if (refusal != null) return refusal;
                // the whole body within its deadline, so a trickle under the idle time holds a connection a while at most
                exchange.BodyTimer = loop.After(head.Path == "/api/upload" ? UploadSeconds : bodySeconds, () => Close(exchange));
                if (string.Equals(head.Header("expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var go = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    try { exchange.Stream.Write(go, 0, go.Length); } catch (Exception) { }
                }
            }
            switch (exchange.Feed(data))
            {
                case Fed.More: return null;
                case Fed.Malformed: return Response.Text("Malformed request", 400);
                case Fed.TooLarge: return Response.Text("Too large", 413);
                default:
                    var request = exchange.Head;
                    request.Body = exchange.Memory.ToArray();
                    request.BodyFile = exchange.CloseFile();
                    exchange.IsRouted = true;
                    exchange.StopTimers();
                    route(request, response => loop.Post(() => Send(exchange, response)));
                    return null;
            }
        }

        private static int IndexOfBlankLine(byte[] bytes, int limit)
        {
            for (var i = 0; i + 3 < limit; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
            return -1;
        }

        private void Send(Exchange exchange, Response response)
        {
            if (exchange.IsClosed) return;
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
            head.Append("Content-Type: ").Append(response.Type).Append("\r\n");
            head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            head.Append(response.IsLasting ? "Cache-Control: private, max-age=604800, immutable\r\n" : "Cache-Control: no-store\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Referrer-Policy: no-referrer\r\n");
            if (response.Type.StartsWith("text/html", StringComparison.Ordinal))
                head.Append("Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n");
            head.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            var out_ = new byte[bytes.Length + response.Body.Length];
            Buffer.BlockCopy(bytes, 0, out_, 0, bytes.Length);
            Buffer.BlockCopy(response.Body, 0, out_, bytes.Length, response.Body.Length);
            try
            {
                exchange.Stream.BeginWrite(out_, 0, out_.Length, result =>
                {
                    try { exchange.Stream.EndWrite(result); } catch (Exception) { }
                    loop.Post(() => Close(exchange));
                }, null);
            }
            catch (Exception)
            {
                Close(exchange);
            }
        }

        private void Close(Exchange exchange)
        {
            if (exchange.IsClosed) return;
            exchange.IsClosed = true;
            exchange.Finish();
            open.Remove(exchange);
            try { exchange.Client.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { exchange.Client.Close(); } catch (Exception) { }
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Content Too Large";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        // the request line and headers; the query read from the target
        private static Request ParseHead(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3) return null;
            var request = new Request { Method = first[0] };
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
            }
            var target = first[1];
            var mark = target.IndexOf('?');
            request.Path = Uri.UnescapeDataString(mark < 0 ? target : target.Substring(0, mark));
            if (mark >= 0)
                foreach (var pair in target.Substring(mark + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var name = Unescape(eq < 0 ? pair : pair.Substring(0, eq));
                    var value = eq < 0 ? "" : Unescape(pair.Substring(eq + 1));
                    request.Query[name] = value;
                }
            return request;
        }

        private static string Unescape(string text)
        {
            try { return Uri.UnescapeDataString(text.Replace('+', ' ')); }
            catch (Exception) { return text; }
        }

        private enum Fed { More, Done, Malformed, TooLarge }

        // one connection's request as it arrives: the head's bytes until it is whole, then its body, by Content-Length or
        // in chunks (what Tailscale's proxy sends for a body of unstated length), into memory or into a spool file
        private sealed class Exchange
        {
            public readonly TcpClient Client;
            public readonly NetworkStream Stream;
            public MemoryStream Buffer = new MemoryStream();
            public Request Head;
            public MemoryStream Memory = new MemoryStream();
            public bool IsRouted, IsClosed;
            public Loop.Later HeadTimer, IdleTimer, BodyTimer;
            private FileStream file;
            private string filePath;
            private long limit = BodyLimit;
            private long taken;
            private long remaining;
            private bool isChunked;
            private long chunkLeft;
            private readonly MemoryStream line = new MemoryStream();
            private Stage stage = Stage.Size;

            private enum Stage { Size, Data, DataEnd, Trailer }

            public Exchange(TcpClient client, Server server)
            {
                Client = client;
                Stream = client.GetStream();
            }

            // the body's framing and its home: an answer refuses the request as it stands
            public Response Frame(Request head, string spool)
            {
                limit = head.Path == "/api/upload" ? UploadLimit : BodyLimit;
                isChunked = (head.Header("transfer-encoding") ?? "").ToLowerInvariant().Contains("chunked");
                if (!isChunked)
                {
                    if (!long.TryParse(head.Header("content-length") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                        return Response.Text("Malformed request", 400);
                    if (length > limit) return Response.Text("Too large", 413);
                    remaining = length;
                }
                if (head.Path == "/api/upload")
                {
                    try
                    {
                        filePath = System.IO.Path.Combine(spool, Guid.NewGuid().ToString("N"));
                        file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write);
                        Secure.File(filePath);
                    }
                    catch (Exception)
                    {
                        return Response.Text("Saving failed", 500);
                    }
                }
                return null;
            }

            public Fed Feed(ArraySegment<byte> data)
            {
                var bytes = data.Array;
                var index = data.Offset;
                var end = data.Offset + data.Count;
                if (!isChunked)
                {
                    var part = (int)Math.Min(remaining, end - index);
                    Write(bytes, index, part);
                    remaining -= part;
                    return remaining == 0 ? Fed.Done : Fed.More;
                }
                while (index < end || stage == Stage.DataEnd || stage == Stage.Size || stage == Stage.Trailer)
                {
                    if (stage == Stage.Data)
                    {
                        var part = (int)Math.Min(chunkLeft, end - index);
                        Write(bytes, index, part);
                        chunkLeft -= part;
                        index += part;
                        if (chunkLeft == 0) stage = Stage.DataEnd;
                        if (index == end && stage == Stage.Data) return Fed.More;
                        continue;
                    }
                    // a line: a chunk's size, the line break after its data, or a trailer, the empty one ending the body
                    var found = NextLine(bytes, ref index, end);
                    if (found == null) return line.Length > 1_024 ? Fed.Malformed : Fed.More;
                    if (found.Length > 1_024) return Fed.Malformed;
                    switch (stage)
                    {
                        case Stage.Size:
                            var text = Encoding.ASCII.GetString(found).Split(';')[0].Trim();
                            if (!long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                            {
                                // a size past what a long holds is past every limit
                                if (text.Length > 0 && IsHex(text)) return Fed.TooLarge;
                                return Fed.Malformed;
                            }
                            if (size == 0) stage = Stage.Trailer;
                            else
                            {
                                if (size > limit - taken) return Fed.TooLarge;
                                chunkLeft = size;
                                stage = Stage.Data;
                            }
                            break;
                        case Stage.DataEnd:
                            if (found.Length != 0) return Fed.Malformed;
                            stage = Stage.Size;
                            break;
                        default:
                            if (found.Length == 0) return Fed.Done;
                            break;
                    }
                }
                return Fed.More;
            }

            private static bool IsHex(string text)
            {
                foreach (var c in text)
                    if (!Uri.IsHexDigit(c)) return false;
                return true;
            }

            // the next whole line from what has arrived, its break left off; a part waits in `line` for the rest
            private byte[] NextLine(byte[] bytes, ref int index, int end)
            {
                var newline = Array.IndexOf(bytes, (byte)10, index, end - index);
                if (newline < 0)
                {
                    line.Write(bytes, index, end - index);
                    index = end;
                    return null;
                }
                line.Write(bytes, index, newline - index);
                index = newline + 1;
                var found = line.ToArray();
                line.SetLength(0);
                if (found.Length > 0 && found[found.Length - 1] == 13) Array.Resize(ref found, found.Length - 1);
                return found;
            }

            private void Write(byte[] bytes, int offset, int count)
            {
                if (count <= 0) return;
                taken += count;
                if (file != null) file.Write(bytes, offset, count);
                else Memory.Write(bytes, offset, count);
            }

            // the spool file's path once its body is whole, for the route
            public string CloseFile()
            {
                file?.Dispose();
                file = null;
                return filePath;
            }

            public void StopTimers()
            {
                HeadTimer?.Cancel();
                IdleTimer?.Cancel();
                BodyTimer?.Cancel();
                HeadTimer = IdleTimer = BodyTimer = null;
            }

            // the connection's end: its timers stop, and a spool file the route left behind goes
            public void Finish()
            {
                StopTimers();
                file?.Dispose();
                file = null;
                if (filePath != null)
                    try { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); } catch (Exception) { }
            }
        }
    }

    // A file or folder readable by this user alone: on Windows the profile's own folders already are; elsewhere (the
    // Mac-side checks) the mode bits say so
    public static class Secure
    {
        public static void File(string path)
        {
#if !NETFRAMEWORK
            if (!OperatingSystem.IsWindows())
                try { System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
#endif
        }

        public static void Folder(string path)
        {
#if !NETFRAMEWORK
            if (!OperatingSystem.IsWindows())
                try { System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { }
#endif
        }
    }
}
