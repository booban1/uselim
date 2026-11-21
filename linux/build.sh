#!/bin/zsh
# Builds Uselim for Linux on the Mac: build.noindex/<x64|arm64>/uselim, one self-contained file per processor, from the
# .NET SDK. "./build.sh test" runs the shared core's checks first; "./build.sh release" also packs each file as
# Uselim-<version>-linux-<x64|arm64>.tar.gz with its .sha256, the names the Linux updater looks for in a GitHub release.
set -e
cd "${0:A:h}"
export PATH="/usr/local/share/dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ $1 == test || $1 == release ]]; then
  dotnet run --project ../windows/Tests/Tests.csproj -c Release -f net11.0
fi

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Uselim.csproj)
for arch in x64 arm64; do
  dotnet publish Uselim.csproj -c Release -r linux-$arch -o build.noindex/$arch -nologo -v quiet
  echo "Built linux/build.noindex/$arch/uselim"
  if [[ $1 == release ]]; then
    name="Uselim-$version-linux-$arch.tar.gz"
    (cd build.noindex/$arch && tar -czf "../$name" uselim && cd .. && shasum -a 256 "$name" > "$name.sha256")
    echo "Packed linux/build.noindex/$name"
  fi
done
