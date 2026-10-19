#!/usr/bin/env bash
# Publishes the asn1 CLI as a self-contained single-file executable (no .NET installation needed), smoke-tests it
# when it can run on this machine, and archives it with README and LICENSE.
#
# usage: scripts/publish-cli.sh <runtime-id> <version> [output-dir]
#   runtime-id  linux-x64, linux-arm64, win-x64, win-arm64, osx-x64 or osx-arm64
#   output      <output-dir>/asn1-<version>-<runtime-id>.tar.gz (.zip for Windows); default output-dir: artifacts/cli
set -euo pipefail

rid="${1:?usage: scripts/publish-cli.sh <runtime-id> <version> [output-dir]}"
version="${2:?usage: scripts/publish-cli.sh <runtime-id> <version> [output-dir]}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$(mkdir -p "${3:-$root/artifacts/cli}" && cd "${3:-$root/artifacts/cli}" && pwd)"
name="asn1-$version-$rid"
stage="$out/$name"
rm -rf "$stage"

dotnet publish "$root/src/Asn1.Cli/Asn1.Cli.csproj" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=embedded -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false \
  -p:Version="$version" -o "$stage"
cp "$root/README.md" "$root/LICENSE" "$stage/"
exe="$stage/asn1"; [[ "$rid" == win-* ]] && exe="$stage/asn1.exe"

# ------------------------------------------------------------------ smoke test (only for this machine's platform)
host_os="$(uname -s)"; host_arch="$(uname -m)"
case "$host_os" in Linux) host=linux ;; Darwin) host=osx ;; MINGW*|MSYS*|CYGWIN*) host=win ;; *) host=unknown ;; esac
case "$host_arch" in x86_64|amd64) host="$host-x64" ;; aarch64|arm64) host="$host-arm64" ;; esac
if [[ "$host" == "$rid" ]]; then
  echo "smoke test: $rid"
  "$exe" version | grep -q "$version" || { echo "error: asn1 version does not report $version" >&2; exit 1; }
  hex="$("$exe" encode "$root/samples/telemetry/telemetry.asn" -t Heartbeat -f uper --value '{"sequenceNumber":7,"uptimeSeconds":4000000000}' | tr -d '\r\n')"
  [[ "$hex" == "07EE6B2800" ]] || { echo "error: asn1 encode gave '$hex'" >&2; exit 1; }
  gen="$(mktemp -d)"
  "$exe" generate-csharp "$root/samples/telemetry/telemetry.asn" -o "$gen" --quiet
  grep -q "public sealed partial class Heartbeat" "$gen"/*.g.cs || { echo "error: generate-csharp produced no Heartbeat class" >&2; exit 1; }
  rm -rf "$gen"
  "$exe" lint "$root/samples" --encoding-rules uper > /dev/null
  echo "smoke test passed"
else
  echo "smoke test skipped: $rid cannot run on $host"
fi

# ------------------------------------------------------------------ archive
cd "$out"
if [[ "$rid" == win-* ]]; then
  rm -f "$name.zip"
  if command -v 7z > /dev/null; then 7z a -bso0 "$name.zip" "$name" > /dev/null
  else python3 -m zipfile -c "$name.zip" "$name"; fi
  echo "$out/$name.zip"
else
  tar -czf "$name.tar.gz" "$name"
  echo "$out/$name.tar.gz"
fi
