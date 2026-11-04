#!/bin/bash
# Builds the Xcode project that the RunPod builder exported (Xcode-<tag>.tar.gz in the
# GitHub Release of the tag), signs it and uploads it to App Store Connect, where it shows
# up in TestFlight. Runs on the Mac runner.
#
# Env: GITHUB_TOKEN, GITHUB_REPOSITORY, RELEASE_TAG, TEAM_ID,
#      ASC_KEY_ID, ASC_ISSUER_ID, ASC_KEY_P8 (the AuthKey .p8, as is or base64-encoded),
#      IOS_DIST_P12 (the Apple Distribution identity, base64 .p12), IOS_DIST_P12_PASSWORD.
# The API key needs the Admin role so Xcode can create the distribution certificate
# and profile itself (-allowProvisioningUpdates).
set -euo pipefail
# The runner service starts without a UTF-8 locale, which CocoaPods needs.
export LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8

work="${RUNNER_TEMP:-$(mktemp -d)}/ios"
rm -rf "$work"
mkdir -p "$work"
cd "$work"

api="https://api.github.com/repos/$GITHUB_REPOSITORY"
asset="Xcode-$RELEASE_TAG.tar.gz"
asset_url=$(curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" "$api/releases/tags/$RELEASE_TAG" |
  ASSET="$asset" python3 -c 'import json, os, sys
assets = [a for a in json.load(sys.stdin)["assets"] if a["name"] == os.environ["ASSET"]]
print(assets[0]["url"] if assets else "")')
if [ -z "$asset_url" ]; then
  echo "$asset not found in release $RELEASE_TAG" >&2
  exit 1
fi
echo "Downloading $asset"
curl -fSL -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/octet-stream" "$asset_url" -o xcode.tar.gz
tar -xzf xcode.tar.gz

key="$work/AuthKey_$ASC_KEY_ID.p8"
keychain="$work/ci.keychain-db"
saved_keychains=()
cleanup() {
  rm -f "$key" "$work/dist.p12"
  if [ ${#saved_keychains[@]} -gt 0 ]; then
    security list-keychains -d user -s "${saved_keychains[@]}" || true
  fi
  [ -f "$keychain" ] && security delete-keychain "$keychain" || true
  # The project, DerivedData and archive take several GB; nothing is reused between runs.
  cd /
  rm -rf "$work"
}
trap cleanup EXIT

# The runner service cannot use private keys in the login keychain (codesign fails with
# errSecInternalComponent), so the distribution identity goes into a throwaway keychain
# that this script creates, unlocks and allows codesign to use without a prompt.
if [ -n "${IOS_DIST_P12:-}" ]; then
  keychain_pass=$(uuidgen)
  security create-keychain -p "$keychain_pass" "$keychain"
  security set-keychain-settings -lut 7200 "$keychain"
  security unlock-keychain -p "$keychain_pass" "$keychain"
  printf '%s' "$IOS_DIST_P12" | base64 --decode > "$work/dist.p12"
  security import "$work/dist.p12" -k "$keychain" -P "$IOS_DIST_P12_PASSWORD" \
    -T /usr/bin/codesign -T /usr/bin/security
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keychain_pass" "$keychain" > /dev/null
  while IFS= read -r line; do
    saved_keychains+=("$(echo "$line" | sed -E 's/^[[:space:]]*"(.*)"$/\1/')")
  done < <(security list-keychains -d user)
  # First in the search list, so codesign finds the identity here before the login keychain.
  security list-keychains -d user -s "$keychain" "${saved_keychains[@]}"
  security find-identity -v -p codesigning "$keychain"
else
  echo "::warning::IOS_DIST_P12 is not set; signing relies on the login keychain"
fi

if [[ "$ASC_KEY_P8" == -----* ]]; then
  printf '%s\n' "$ASC_KEY_P8" > "$key"
else
  printf '%s' "$ASC_KEY_P8" | base64 --decode > "$key"
fi
auth=(-allowProvisioningUpdates -authenticationKeyPath "$key"
      -authenticationKeyID "$ASC_KEY_ID" -authenticationKeyIssuerID "$ASC_ISSUER_ID")

# Firebase and Appodeal come as CocoaPods, which the Linux export cannot install.
cd Xcode
# cdn.cocoapods.org now redirects to jsDelivr, where CocoaPods' HTTP/2 downloads fail
# with "Error in the HTTP2 framing layer"; the git trunk avoids the CDN. The first run
# clones it into ~/.cocoapods/repos, later runs only fetch.
if [ -f Podfile ]; then
  sed -i '' -E 's#https://cdn\.cocoapods\.org/?#https://github.com/CocoaPods/Specs.git#g' Podfile
  grep -E "^source" Podfile || true
  for attempt in 1 2 3; do
    pod install --repo-update && break
    if [ "$attempt" = 3 ]; then
      exit 1
    fi
    echo "pod install failed, retrying ($attempt/3)"
    sleep 15
  done
fi
if [ -d Unity-iPhone.xcworkspace ]; then
  project=(-workspace Unity-iPhone.xcworkspace)
else
  project=(-project Unity-iPhone.xcodeproj)
fi

# The archive stays unsigned; the export below signs it for App Store distribution.
echo "Archiving"
xcodebuild "${project[@]}" -scheme Unity-iPhone -configuration Release \
  -destination generic/platform=iOS -archivePath "$work/app.xcarchive" \
  -derivedDataPath "$work/DerivedData" \
  DEVELOPMENT_TEAM="$TEAM_ID" CODE_SIGNING_ALLOWED=NO -quiet archive

cat > "$work/ExportOptions.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key><string>app-store-connect</string>
  <key>destination</key><string>upload</string>
  <key>teamID</key><string>$TEAM_ID</string>
  <key>signingStyle</key><string>automatic</string>
  <key>uploadSymbols</key><true/>
</dict>
</plist>
EOF

echo "Uploading to App Store Connect"
if ! xcodebuild -exportArchive -archivePath "$work/app.xcarchive" \
  -exportOptionsPlist "$work/ExportOptions.plist" -exportPath "$work/export" "${auth[@]}"; then
  logs=$(ls -dt "${TMPDIR:-/tmp}"/*.xcdistributionlogs 2>/dev/null | head -1 || true)
  [ -n "$logs" ] && tail -n 60 "$logs/IDEDistribution.standard.log" || true
  exit 1
fi

# The Xcode project is only an intermediate; keep the release to the store builds.
# The build is already uploaded, so a failed delete must not fail the job: a rerun would
# upload the same build number again.
curl -fsS -X DELETE -H "Authorization: Bearer $GITHUB_TOKEN" "$asset_url" ||
  echo "::warning::Could not delete $asset from the release"
echo "Uploaded; the build appears in TestFlight once App Store Connect processes it"
