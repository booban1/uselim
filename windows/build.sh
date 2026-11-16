#!/bin/zsh
# Builds Uselim for Windows on the Mac: build.noindex/Uselim.exe from the .NET SDK and .NET Framework 4.8's reference
# assemblies. "./build.sh test" runs the core's checks on the Mac first; "./build.sh release" also zips the exe as
# Uselim-<version>-windows.zip with its .sha256, the names the Windows updater looks for in a GitHub release.
set -e
cd "${0:A:h}"
export PATH="/usr/local/share/dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ $1 == test || $1 == release ]]; then
  dotnet run --project Tests/Tests.csproj -c Release
fi

dotnet build Uselim.csproj -c Release -nologo -v quiet
cp build.noindex/bin/Uselim/Release/Uselim.exe build.noindex/Uselim.exe
echo "Built windows/build.noindex/Uselim.exe"

if [[ $1 == release ]]; then
  version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Uselim.csproj)
  zip="build.noindex/Uselim-$version-windows.zip"
  rm -f "$zip" "$zip.sha256"
  (cd build.noindex && zip -q -X "Uselim-$version-windows.zip" Uselim.exe)
  (cd build.noindex && shasum -a 256 "Uselim-$version-windows.zip" > "Uselim-$version-windows.zip.sha256")
  echo "Zipped $zip"
fi
