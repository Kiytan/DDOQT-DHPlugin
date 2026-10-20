#!/usr/bin/env bash
# Writes the signed Dungeon Helper 5 update feed for a release, ready to publish on GitHub Pages:
#
#   dist/feed/V2/plugin.metadata
#   dist/feed/V2/plugin.metadata.sig
#
# Usage: DH_AUTHOR_PFX=/path/to/author.pfx scripts/update-feed.sh <version>
#
# Run it after `dotnet publish -c Release` has built dist/QuestTracker_<version>.zip, and after that zip
# has been attached to a GitHub release tagged <version>. The live feed is fetched and this version
# added to it, so older versions stay listed. Then copy dist/feed/V2/ to the V2/ folder of the gh-pages
# branch and push.
#
# Every install polls the feed URL baked into its plugin.metadata, and Dungeon Helper refuses a feed
# whose .sig is missing or does not verify, so both files must always be published together.
set -euo pipefail

PluginName="QuestTracker"
FeedUrl="https://kiytan.github.io/DDOQT-DHPlugin/V2/plugin.metadata"
ProductInfoUrl="https://github.com/Kiytan/DDOQT-DHPlugin"
ReleaseBaseUrl="https://github.com/Kiytan/DDOQT-DHPlugin/releases/download"

RepoRoot="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:?usage: scripts/update-feed.sh <version>}"
pfx="${DH_AUTHOR_PFX:?DH_AUTHOR_PFX must name the author .pfx}"
zipfile="$RepoRoot/dist/${PluginName}_${version}.zip"
outdir="$RepoRoot/dist/feed/V2"
meta="$outdir/plugin.metadata"

[ -f "$zipfile" ] || { echo "zip not found: $zipfile (run dotnet publish -c Release first)" >&2; exit 1; }
[ -f "$pfx" ] || { echo "DH_AUTHOR_PFX does not exist: $pfx" >&2; exit 1; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

mkdir -p "$outdir"
if curl -fsSL "$FeedUrl" -o "$meta" 2>/dev/null; then
    echo "== fetched existing feed from $FeedUrl =="
else
    echo "== no existing feed at $FeedUrl, starting a fresh one =="
    printf '{ "PluginName": "%s", "CurrentVersion": "", "AllowedVersions": [] }\n' "$PluginName" > "$meta"
fi

url="$ReleaseBaseUrl/$version/${PluginName}_${version}.zip"
checksum="$(sha512sum "$zipfile" | awk '{print toupper($1)}')"

jq --arg name "$PluginName" --arg feed "$FeedUrl" --arg page "$ProductInfoUrl" \
   --arg v "$version" --arg url "$url" --arg sum "$checksum" '
    .PluginName = $name
  | .UpdateSource = $feed
  | .ProductInfoUrl = $page
  | .CurrentVersion = $v
  | .AllowedVersions = ((.AllowedVersions // []) | map(select(.Version != $v)))
        + [ { Version: $v, Url: $url, Checksum: $sum } ]
' "$meta" > "$meta.tmp" && mv "$meta.tmp" "$meta"

# Signed after the last edit, as the signature covers the exact bytes served. Verified against the
# public key inside the release zip, which is the one installs will check it with.
openssl dgst -sha256 -sign "$pfx" -passin pass: -out "$meta.sig" "$meta"
bundlepem="$(mktemp)"
trap 'rm -f "$bundlepem"' EXIT
unzip -p "$zipfile" "$PluginName.pem" > "$bundlepem" || { echo "no $PluginName.pem inside $zipfile" >&2; exit 1; }
openssl dgst -sha256 -verify "$bundlepem" -signature "$meta.sig" "$meta" >/dev/null \
    || { echo "feed signature does not verify against the bundle's $PluginName.pem" >&2; exit 1; }

echo "== $meta updated for $version and signed =="
cat "$meta"
