using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Uselim
{
    // Web push to the phone's app (RFC 8030), each message encrypted for its subscription (RFC 8291, aes128gcm) and
    // signed with Uselim's VAPID key (RFC 8292), the Mac's Push.swift on Windows. The key and the subscriptions live in
    // the relay folder: vapid.json (the signing key) and push.json. A subscription names an endpoint on a phone's own
    // push service, Apple's for an iPhone, Google's for Chrome on Android, Mozilla's for Firefox, and carries its phone's
    // tag: Remove leaves it behind. A 404 or 410 from the service retires it.

    public sealed class Push
    {
        private readonly string keyPath, listPath;
        private readonly Loop loop;
        private readonly ECParameters key;
        private List<JObject> subscriptions = new List<JObject>();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        public Push(string relayDir, Loop loop)
        {
            this.loop = loop;
            keyPath = Path.Combine(relayDir, "vapid.json");
            listPath = Path.Combine(relayDir, "push.json");
            var stored = Json.ParseObject(Paths.Read(keyPath));
            var d = Base64Url.Decode(stored?.Str("private"));
            var x = Base64Url.Decode(stored?.Str("x"));
            var y = Base64Url.Decode(stored?.Str("y"));
            if (d != null && x != null && y != null && d.Length == 32 && x.Length == 32 && y.Length == 32)
            {
                key = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = new ECPoint { X = x, Y = y } };
            }
            else
            {
                using (var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    key = fresh.ExportParameters(true);
                Paths.WriteAtomic(keyPath, Json.Write(new JObject
                {
                    ["private"] = Base64Url.Encode(key.D), ["x"] = Base64Url.Encode(key.Q.X), ["y"] = Base64Url.Encode(key.Q.Y),
                }));
                Secure.File(keyPath);
            }
            subscriptions = (Json.Parse(Paths.Read(listPath)) as List<object> ?? new List<object>()).OfType<JObject>().ToList();
        }

        // the applicationServerKey the phone subscribes with: the public key as an uncompressed point
        public string PublicKey => Base64Url.Encode(Point(key.Q));
        public int Count => subscriptions.Count;

        private static byte[] Point(ECPoint q)
        {
            var point = new byte[65];
            point[0] = 4;
            Buffer.BlockCopy(q.X, 0, point, 1, 32);
            Buffer.BlockCopy(q.Y, 0, point, 33, 32);
            return point;
        }

        // a phone's own push service over HTTPS: Apple's (web.push.apple.com, any name under push.apple.com), Google's
        // (fcm.googleapis.com, Chrome on Android), or Mozilla's (Firefox)
        public static bool IsKnownService(string endpoint)
        {
            if (!Uri.TryCreate(endpoint ?? "", UriKind.Absolute, out var url) || url.Scheme != "https") return false;
            var host = url.Host.ToLowerInvariant();
            return host == "push.apple.com" || host.EndsWith(".push.apple.com", StringComparison.Ordinal)
                || host == "fcm.googleapis.com"
                || host == "updates.push.services.mozilla.com";
        }

        public bool Add(JObject subscription, string origin, string tag)
        {
            var endpoint = subscription?.Str("endpoint");
            var keys = subscription?.Obj("keys");
            if (!IsKnownService(endpoint) || keys?.Str("p256dh") == null || keys.Str("auth") == null) return false;
            subscriptions.RemoveAll(s => s.Str("endpoint") == endpoint);
            subscriptions.Add(new JObject { ["endpoint"] = endpoint, ["keys"] = keys, ["origin"] = origin, ["tag"] = tag });
            Save();
            Log.Note("relay: phone subscribed for notifications (" + subscriptions.Count + " in all)");
            return true;
        }

        // the subscriptions the phones in use stand behind: one under another tag, or on another service, leaves the list
        public void Keep(ICollection<string> tags)
        {
            var before = subscriptions.Count;
            subscriptions = subscriptions.Where(s => IsKnownService(s.Str("endpoint")) && s.Str("tag") != null && tags.Contains(s.Str("tag"))).ToList();
            if (subscriptions.Count == before) return;
            Save();
            Log.Note("relay: notification subscriptions kept: " + subscriptions.Count + " of " + before);
        }

        public void Send(string title, string body, string url, string tag)
        {
            var payload = Encoding.UTF8.GetBytes(Json.Write(new JObject
            {
                ["title"] = title, ["body"] = body.Length > 600 ? body.Substring(0, 600) : body, ["url"] = url, ["tag"] = tag,
            }));
            foreach (var subscription in subscriptions.ToList()) Deliver(payload, subscription);
        }

        private void Save()
        {
            Paths.WriteAtomic(listPath, Json.Write(subscriptions.Cast<object>().ToList()));
            Secure.File(listPath);
        }

        private void Deliver(byte[] payload, JObject subscription)
        {
            var endpoint = subscription.Str("endpoint");
            var keys = subscription.Obj("keys");
            var p256dh = Base64Url.Decode(keys?.Str("p256dh"));
            var auth = Base64Url.Decode(keys?.Str("auth"));
            if (!IsKnownService(endpoint) || p256dh == null || auth == null) return;
            byte[] body;
            try { body = Encrypt(payload, p256dh, auth); }
            catch (Exception e)
            {
                Log.Note("relay: a notification failed to encrypt, " + e.Message);
                return;
            }
            var url = new Uri(endpoint);
            var subject = subscription.Str("origin") ?? "https://localhost";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Headers.TryAddWithoutValidation("Urgency", "high");
            request.Headers.TryAddWithoutValidation("Authorization", Vapid(url.Scheme + "://" + url.Host, subject));
            client.SendAsync(request).ContinueWith(task =>
            {
                var code = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? (int)task.Result.StatusCode : 0;
                var why = task.Exception?.GetBaseException().Message ?? "";
                loop.Post(() =>
                {
                    if (code == 404 || code == 410)
                    {
                        subscriptions.RemoveAll(s => s.Str("endpoint") == endpoint);
                        Save();
                        Log.Note("relay: a retired notification subscription left the list (HTTP " + code + ")");
                    }
                    else if (code < 200 || code > 299)
                    {
                        Log.Note("relay: notification refused, HTTP " + code + " " + why);
                    }
                });
            });
        }

        // "vapid t=<JWT>, k=<public key>": the JWT names the push service as audience, lasts twelve hours and carries the
        // relay's own address as its contact
        private string Vapid(string audience, string subject)
        {
            var header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
            var exp = (long)Spans.ToEpoch(DateTime.UtcNow) + 12 * 3600;
            var claims = Base64Url.Encode(Encoding.UTF8.GetBytes(Json.Write(new JObject { ["aud"] = audience, ["exp"] = exp, ["sub"] = subject })));
            var input = header + "." + claims;
            byte[] signature;
            using (var signer = ECDsa.Create(key))
                signature = signer.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256);
            return "vapid t=" + input + "." + Base64Url.Encode(signature) + ", k=" + PublicKey;
        }

        // RFC 8291: a fresh key pair per message, the shared secret mixed with the subscription's auth secret, then one
        // aes128gcm record (RFC 8188) whose header carries the salt, the record size and the fresh public key
        public static byte[] Encrypt(byte[] plain, byte[] p256dh, byte[] auth, byte[] saltGiven = null, ECParameters? localGiven = null)
        {
            if (p256dh.Length != 65 || p256dh[0] != 4) throw new CryptographicException("the phone's key is malformed");
            var phone = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = p256dh.Skip(1).Take(32).ToArray(), Y = p256dh.Skip(33).Take(32).ToArray() },
            };
            using (var local = localGiven.HasValue ? ECDiffieHellman.Create(localGiven.Value) : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            using (var other = ECDiffieHellman.Create(phone))
            {
                var localPublic = Point(local.ExportParameters(false).Q);
                // HKDF-Extract(auth, shared secret) is HMAC-SHA256 keyed with auth over the secret: what DeriveKeyFromHmac gives
                var prk = local.DeriveKeyFromHmac(other.PublicKey, HashAlgorithmName.SHA256, auth);
                var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info"), new byte[] { 0 }, p256dh, localPublic);
                var ikm = Hkdf.Expand(prk, keyInfo, 32);
                var salt = saltGiven ?? Random(16);
                var prk2 = Hkdf.Extract(salt, ikm);
                var cek = Hkdf.Expand(prk2, Concat(Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm"), new byte[] { 0 }), 16);
                var nonce = Hkdf.Expand(prk2, Concat(Encoding.ASCII.GetBytes("Content-Encoding: nonce"), new byte[] { 0 }), 12);
                var record = Concat(plain, new byte[] { 2 });
                var sealed_ = Gcm.Seal(cek, nonce, record);
                return Concat(salt, new byte[] { 0, 0, 0x10, 0, (byte)localPublic.Length }, localPublic, sealed_);
            }
        }

        public static byte[] Random(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            var all = new byte[parts.Sum(p => p.Length)];
            var at = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, all, at, p.Length);
                at += p.Length;
            }
            return all;
        }
    }

    public static class Hkdf
    {
        public static byte[] Extract(byte[] salt, byte[] ikm)
        {
            using (var hmac = new HMACSHA256(salt)) return hmac.ComputeHash(ikm);
        }

        // the first block of HKDF-Expand, enough for every length here (32 bytes at most)
        public static byte[] Expand(byte[] prk, byte[] info, int length)
        {
            using (var hmac = new HMACSHA256(prk))
            {
                var block = hmac.ComputeHash(Push.Concat(info, new byte[] { 1 }));
                return block.Take(length).ToArray();
            }
        }
    }

    public static class Base64Url
    {
        public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public static byte[] Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var plain = text.Replace('-', '+').Replace('_', '/');
            while (plain.Length % 4 != 0) plain += "=";
            try { return Convert.FromBase64String(plain); }
            catch (FormatException) { return null; }
        }
    }

    // AES-128-GCM: the ciphertext with its 16-byte tag after it. Modern .NET carries it; .NET Framework reaches Windows'
    // own CNG for it, the same code Windows' TLS uses
    public static class Gcm
    {
        public static byte[] Seal(byte[] key, byte[] nonce, byte[] plain)
        {
#if NETFRAMEWORK
            return Cng.Seal(key, nonce, plain);
#else
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            return Push.Concat(cipher, tag);
#endif
        }

#if NETFRAMEWORK
        private static class Cng
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct AuthInfo
            {
                public int cbSize, dwInfoVersion;
                public IntPtr pbNonce;
                public int cbNonce;
                public IntPtr pbAuthData;
                public int cbAuthData;
                public IntPtr pbTag;
                public int cbTag;
                public IntPtr pbMacContext;
                public int cbMacContext, cbAAD;
                public long cbData;
                public int dwFlags;
            }

            [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
            [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptSetProperty(IntPtr obj, string prop, byte[] input, int size, int flags);
            [DllImport("bcrypt.dll")] private static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int secretSize, int flags);
            [DllImport("bcrypt.dll")] private static extern int BCryptEncrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
            [DllImport("bcrypt.dll")] private static extern int BCryptDestroyKey(IntPtr key);
            [DllImport("bcrypt.dll")] private static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

            public static byte[] Seal(byte[] key, byte[] nonce, byte[] plain)
            {
                Check(BCryptOpenAlgorithmProvider(out var alg, "AES", null, 0), "open");
                var handle = IntPtr.Zero;
                var pins = new List<GCHandle>();
                try
                {
                    var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                    Check(BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0), "mode");
                    Check(BCryptGenerateSymmetricKey(alg, out handle, IntPtr.Zero, 0, key, key.Length, 0), "key");
                    var tag = new byte[16];
                    var nonceCopy = (byte[])nonce.Clone();
                    var pinNonce = GCHandle.Alloc(nonceCopy, GCHandleType.Pinned);
                    var pinTag = GCHandle.Alloc(tag, GCHandleType.Pinned);
                    pins.Add(pinNonce);
                    pins.Add(pinTag);
                    var info = new AuthInfo
                    {
                        cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1,
                        pbNonce = pinNonce.AddrOfPinnedObject(), cbNonce = nonceCopy.Length,
                        pbTag = pinTag.AddrOfPinnedObject(), cbTag = tag.Length,
                    };
                    var cipher = new byte[plain.Length];
                    Check(BCryptEncrypt(handle, plain, plain.Length, ref info, IntPtr.Zero, 0, cipher, cipher.Length, out _, 0), "encrypt");
                    return Push.Concat(cipher, tag);
                }
                finally
                {
                    foreach (var pin in pins) pin.Free();
                    if (handle != IntPtr.Zero) BCryptDestroyKey(handle);
                    BCryptCloseAlgorithmProvider(alg, 0);
                }
            }

            private static void Check(int status, string step)
            {
                if (status != 0) throw new CryptographicException("AES-GCM " + step + " failed, 0x" + status.ToString("x8"));
            }
        }
#endif
    }
}
