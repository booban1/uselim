#!/bin/zsh
# Pair a phone's QR codes, read back on the Mac: windows/Core/Phone/Qr.cs makes codes of several lengths (versions 1
# to 10), each drawn to a PNG and read with the Mac's own QR reader (CoreImage's detector, qr-read.swift); every text
# must come back whole. ./check-qr.sh prints a line per code and exits 1 at any miss.
set -e
cd "${0:A:h}"
export PATH="/usr/local/share/dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
work=$(mktemp -d -t uselim-qr)
trap 'rm -rf $work' EXIT
dotnet build Tests.csproj -c Release -f net11.0 -nologo -v quiet > /dev/null
swiftc -O qr-read.swift -o $work/read
bin=../build.noindex/bin/Tests/Release/net11.0/Tests
missed=0
for text in "hi" "https://desktop-8k2l.tail9b3c.ts.net/#pair=123456" \
    "https://a-rather-long-machine-name-here.tail1234ab.ts.net/#pair=987654" "${(l:100::x:)}" \
    "https://example.com/${(l:150::y:)}" "${(l:210::z:)}"; do
  version=$($bin --qr "$text" $work/m.txt)
  python3 - $work/m.txt $work/m.png <<'PY'
import sys
from PIL import Image
rows = open(sys.argv[1]).read().split()
n, quiet, scale = len(rows), 4, 8
image = Image.new('L', ((n + 2 * quiet) * scale,) * 2, 255)
pixels = image.load()
for y, row in enumerate(rows):
    for x, c in enumerate(row):
        if c == '1':
            for dy in range(scale):
                for dx in range(scale):
                    pixels[(x + quiet) * scale + dx, (y + quiet) * scale + dy] = 0
image.save(sys.argv[2])
PY
  read=$($work/read $work/m.png)
  if [[ $read == $text ]]; then echo "ok    $version, ${#text} characters"; else echo "MISS  $version, ${#text} characters: read '$read'"; missed=1; fi
done
exit $missed
