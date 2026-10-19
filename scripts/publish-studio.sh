#!/usr/bin/env bash
# Publishes DotAsn Studio as a static site (GitHub Pages or any static file host).
#
# usage: scripts/publish-studio.sh [base-path] [output-dir]
#   base-path   the URL path the site is served from: "/" (default) or e.g. "/DotAsn/" for a GitHub project site
#   output-dir  default: artifacts/studio; the site is written to <output-dir>/publish/wwwroot
set -euo pipefail

base="${1:-/}"
out="${2:-artifacts/studio}"
[[ "$base" == /* ]] || base="/$base"
[[ "$base" == */ ]] || base="$base/"

cd "$(dirname "$0")/.."
rm -rf "$out"
dotnet publish tools/Asn1.Studio/Asn1.Studio.csproj -c Release -o "$out/publish"

site="$out/publish/wwwroot"
# Blazor resolves every asset and route against <base href>.
sed -i "s|<base href=\"/\" />|<base href=\"$base\" />|" "$site/index.html"
grep -q "<base href=\"$base\" />" "$site/index.html" || { echo "error: could not set <base href>" >&2; exit 1; }
# GitHub Pages serves 404.html for unknown paths; the app's router shows its own not-found view.
cp "$site/index.html" "$site/404.html"
# Without this, GitHub Pages (Jekyll) would not serve the _framework directory.
touch "$site/.nojekyll"
echo "$site"
