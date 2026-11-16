using System;
using System.Collections.Generic;
using System.Text;

namespace Uselim
{
    // A QR code for Pair a phone, the Mac's CIQRCodeGenerator on Windows: byte mode, error correction level M (the Mac's
    // pick), versions 1 to 10, so up to 213 bytes, ample for "https://<name>.<tailnet>.ts.net/#pair=123456". Built as the
    // QR standard (ISO/IEC 18004) lays it out: the data with its mode and length, padded and split into blocks, each with
    // its Reed-Solomon codewords, interleaved, placed around the finder, timing and alignment patterns, then the mask with
    // the lowest penalty and its format and version bits. Modules[y, x] true is a dark square.

    public sealed class Qr
    {
        public readonly int Version;
        public readonly int Size;
        public readonly bool[,] Modules;
        private readonly bool[,] isFunction;

        // error correction codewords per block and the number of blocks, level M, by version
        private static readonly int[] eccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] blockCount = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        private Qr(int version)
        {
            Version = version;
            Size = version * 4 + 17;
            Modules = new bool[Size, Size];
            isFunction = new bool[Size, Size];
        }

        public static Qr Encode(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            for (var version = 1; version <= 10; version++)
            {
                var countBits = version <= 9 ? 8 : 16;
                var capacityBits = DataCodewords(version) * 8;
                if (4 + countBits + data.Length * 8 > capacityBits) continue;
                var qr = new Qr(version);
                qr.Build(data, countBits, capacityBits);
                return qr;
            }
            throw new ArgumentException("the text runs past a version 10 code");
        }

        private static int RawModules(int version)
        {
            var result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                var align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int version) => RawModules(version) / 8 - eccPerBlock[version] * blockCount[version];

        private void Build(byte[] data, int countBits, int capacityBits)
        {
            // the bits: byte mode (0100), the length, the bytes, up to four zeros, then whole bytes padded 0xEC, 0x11
            var bits = new List<bool>();
            void Append(int value, int length)
            {
                for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
            }
            Append(4, 4);
            Append(data.Length, countBits);
            foreach (var b in data) Append(b, 8);
            Append(0, Math.Min(4, capacityBits - bits.Count));
            Append(0, (8 - bits.Count % 8) % 8);
            for (var pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));

            DrawFunctionPatterns();
            DrawCodewords(WithErrorCorrection(codewords));
            var best = 0;
            var lowest = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                var penalty = Penalty();
                if (penalty < lowest)
                {
                    lowest = penalty;
                    best = mask;
                }
                ApplyMask(mask);
            }
            ApplyMask(best);
            DrawFormatBits(best);
        }

        private void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        private void DrawFunctionPatterns()
        {
            for (var i = 0; i < Size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            var positions = AlignmentPositions();
            var n = positions.Length;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                            Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            // the format bits' places, drawn for real with the mask
            DrawFormatBits(0);
            if (Version >= 7)
            {
                var rem = Version;
                for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                var bits = (Version << 12) | rem;
                for (var i = 0; i < 18; i++)
                {
                    var dark = ((bits >> i) & 1) != 0;
                    var a = Size - 11 + i % 3;
                    var b = i / 3;
                    Set(a, b, dark);
                    Set(b, a, dark);
                }
            }
        }

        private void DrawFinder(int x, int y)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    var xx = x + dx;
                    var yy = y + dy;
                    if (xx < 0 || xx >= Size || yy < 0 || yy >= Size) continue;
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(xx, yy, distance != 2 && distance != 4);
                }
        }

        private int[] AlignmentPositions()
        {
            if (Version == 1) return new int[0];
            var count = Version / 7 + 2;
            var step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            for (int i = count - 1, position = Size - 7; i >= 1; i--, position -= step) result[i] = position;
            return result;
        }

        // level M's two bits (00), the mask's three, ten BCH bits, then the fixed XOR mask
        private void DrawFormatBits(int mask)
        {
            var data = (0 << 3) | mask;
            var rem = data;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            var bits = ((data << 10) | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;
            for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
            for (var i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
            for (var i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
            Set(8, Size - 8, true);
        }

        // the data split into blocks, each with its Reed-Solomon codewords, then interleaved: every block's first
        // codeword, every block's second, and so on, the error correction last
        private byte[] WithErrorCorrection(byte[] data)
        {
            var blocks = blockCount[Version];
            var eccLength = eccPerBlock[Version];
            var raw = RawModules(Version) / 8;
            var shortBlocks = blocks - raw % blocks;
            var shortLength = raw / blocks;
            var divisor = Divisor(eccLength);
            var all = new byte[blocks][];
            for (int i = 0, k = 0; i < blocks; i++)
            {
                var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
                var part = new byte[length];
                Array.Copy(data, k, part, 0, length);
                k += length;
                var block = new byte[shortLength + 1];
                Array.Copy(part, block, length);
                var ecc = Remainder(part, divisor);
                Array.Copy(ecc, 0, block, block.Length - eccLength, eccLength);
                all[i] = block;
            }
            var result = new byte[raw];
            var at = 0;
            for (var i = 0; i < all[0].Length; i++)
                for (var j = 0; j < blocks; j++)
                    if (i != shortLength - eccLength || j >= shortBlocks) result[at++] = all[j][i];
            return result;
        }

        private static byte Multiply(int x, int y)
        {
            var z = 0;
            for (var i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private static byte[] Divisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            var root = 1;
            for (var i = 0; i < degree; i++)
            {
                for (var j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 2);
            }
            return result;
        }

        private static byte[] Remainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                var factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        // the codewords' bits in the zigzag, two columns at a time from the right, up then down, around the patterns
        private void DrawCodewords(byte[] data)
        {
            var i = 0;
            for (var right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (var vertical = 0; vertical < Size; vertical++)
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? Size - 1 - vertical : vertical;
                        if (isFunction[y, x] || i >= data.Length * 8) continue;
                        Modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
            }
        }

        private void ApplyMask(int mask)
        {
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    if (isFunction[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) Modules[y, x] = !Modules[y, x];
                }
        }

        // the standard's four penalties: runs of five or more alike, 2x2 blocks alike, finder-like stretches, and the
        // share of dark squares away from half
        private int Penalty()
        {
            var score = 0;
            for (var pass = 0; pass < 2; pass++)
                for (var a = 0; a < Size; a++)
                {
                    var run = 1;
                    var line = new bool[Size];
                    for (var b = 0; b < Size; b++) line[b] = pass == 0 ? Modules[a, b] : Modules[b, a];
                    for (var b = 1; b < Size; b++)
                    {
                        if (line[b] == line[b - 1])
                        {
                            run++;
                            if (run == 5) score += 3;
                            else if (run > 5) score++;
                        }
                        else run = 1;
                    }
                    for (var b = 0; b + 10 < Size; b++)
                    {
                        if (Matches(line, b, true) || Matches(line, b, false)) score += 40;
                    }
                }
            for (var y = 0; y + 1 < Size; y++)
                for (var x = 0; x + 1 < Size; x++)
                {
                    var c = Modules[y, x];
                    if (c == Modules[y, x + 1] && c == Modules[y + 1, x] && c == Modules[y + 1, x + 1]) score += 3;
                }
            var dark = 0;
            foreach (var m in Modules)
                if (m) dark++;
            var total = Size * Size;
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return score + Math.Max(0, k) * 10;
        }

        // dark-light-dark-dark-dark-light-dark with four light ones on one side: 10111010000 or 00001011101
        private static bool Matches(bool[] line, int at, bool lightAfter)
        {
            var pattern = lightAfter
                ? new[] { true, false, true, true, true, false, true, false, false, false, false }
                : new[] { false, false, false, false, true, false, true, true, true, false, true };
            for (var i = 0; i < 11; i++)
                if (line[at + i] != pattern[i]) return false;
            return true;
        }
    }
}
