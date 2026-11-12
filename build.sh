#!/bin/zsh
# Builds Uselim.app; "./build.sh install" puts it in Applications and opens it; "./build.sh release" builds it for Intel
# and Apple silicon and zips it with its checksum for a GitHub release, signed by the one Uselim Signer certificate
# Culpmit pins
set -e
cd "${0:A:h}"

name="Uselim"
app="build.noindex/$name.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Intel for the Mac it is built on; a release carries Apple silicon too, in one universal binary
if [[ $1 == release ]]; then
  swiftc -O -target x86_64-apple-macos13.0 Sources/*.swift -o "build.noindex/$name-x86_64"
  swiftc -O -target arm64-apple-macos13.0 Sources/*.swift -o "build.noindex/$name-arm64"
  lipo -create "build.noindex/$name-x86_64" "build.noindex/$name-arm64" -output "$app/Contents/MacOS/$name"
else
  swiftc -O -target x86_64-apple-macos13.0 Sources/*.swift -o "$app/Contents/MacOS/$name"
fi
cp Resources/Info.plist "$app/Contents/Info.plist"
# the phone's web app, served by the relay from the bundle
ditto Resources/Web "$app/Contents/Resources/Web"
# the icon, drawn by Tools/make-icon.swift
cp Resources/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
# the Claude Code plugin with its one-entry marketplace, which "Uselim --setup" installs from the app itself, so the
# plugin always matches the app; its tests stay out
plugin="$app/Contents/Resources/Plugin"
rm -rf "$plugin"
mkdir -p "$plugin/.claude-plugin" "$plugin/mod"
cp .claude-plugin/marketplace.json "$plugin/.claude-plugin/marketplace.json"
ditto mod/.claude-plugin "$plugin/mod/.claude-plugin"
ditto mod/hooks "$plugin/mod/hooks"
count=$(git rev-list --count HEAD 2>/dev/null || echo 0)
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $count" "$app/Contents/Info.plist"
# one lasting signature keeps the keychain's Always Allow across builds; ad hoc where the certificate is absent.
# Tools/make-signer.sh makes the certificate and its keychain once.
keychain="$HOME/Library/Keychains/uselim-signing.keychain-db"
sign=(--sign -)
if [[ -f $keychain ]] && security unlock-keychain -p "" "$keychain" 2>/dev/null; then
  identity=$(security find-identity -p codesigning "$keychain" | awk '/"Uselim Signer"/ { print $2; exit }')
  [[ -n $identity ]] && sign=(--keychain "$keychain" --sign "$identity")
fi
codesign --force $sign "$app"
# the build copy leaves macOS's app records, so notifications and login items stay with the copy in Applications
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
$lsregister -u "$app" >/dev/null 2>&1 || true
echo "Built $app"

if [[ $1 == install ]]; then
  pkill -a -x "$name" || true
  for i in {1..50}; do pgrep -a -qx "$name" || break; sleep 0.1; done
  ditto "$app" "/Applications/$name.app"
  touch "/Applications/$name.app"
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "/Applications/$name.app"
  open "/Applications/$name.app" || { sleep 1; open "/Applications/$name.app"; }
  echo "Installed /Applications/$name.app"
fi

if [[ $1 == release ]]; then
  # Culpmit installs only a Uselim whose signature names this leaf: a release signed otherwise stops here
  leaf='certificate leaf = H"140952e822dd4f3065dc6fed4ec81e013bb11ff0"'
  if ! codesign --verify --strict -R="identifier \"com.simonbooban.uselim\" and $leaf" "$app" 2>/dev/null; then
    echo "The app is signed by another certificate than the Uselim Signer Culpmit pins; the release stops here."
    exit 1
  fi
  version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$app/Contents/Info.plist")
  zip="build.noindex/$name-$version.zip"
  rm -f "$zip"
  ditto -c -k --keepParent --norsrc --noextattr --noacl "$app" "$zip"
  (cd build.noindex && shasum -a 256 "${zip:t}" > "${zip:t}.sha256")
  echo "Zipped $zip"
fi
