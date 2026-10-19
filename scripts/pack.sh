#!/usr/bin/env bash
# Packs the DotAsn NuGet packages (libraries in src/ and the asn1 .NET tool) and smoke-tests them the way a user
# gets them: from an isolated local feed and package cache, outside the repository.
#
# usage: scripts/pack.sh <version> [output-dir]      e.g. scripts/pack.sh 1.2.0 artifacts/packages
set -euo pipefail

version="${1:?usage: scripts/pack.sh <version> [output-dir]}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$(mkdir -p "${2:-$root/artifacts/packages}" && cd "${2:-$root/artifacts/packages}" && pwd)"
rm -f "$out"/*.nupkg

dotnet pack "$root/Asn1Toolchain.sln" -c Release -o "$out" -p:Version="$version"
echo "packages:"; ls -1 "$out"

# ------------------------------------------------------------------ smoke test
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/nuget-cache"          # never reuse packages from the user's global cache
# The app below finds $work/nuget.config by walking up from its directory; <clear /> drops the user's other feeds.
cat > "$work/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$out" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

echo "smoke test: the asn1 .NET tool"
dotnet tool install DotAsn.Cli --version "$version" --tool-path "$work/tools" --configfile "$work/nuget.config" > /dev/null
"$work/tools/asn1" version | grep -q "$version" || { echo "error: asn1 version does not report $version" >&2; exit 1; }
hex="$("$work/tools/asn1" encode "$root/samples/telemetry/telemetry.asn" -t Heartbeat -f uper --value '{"sequenceNumber":7,"uptimeSeconds":4000000000}' | tr -d '\r\n')"
[[ "$hex" == "07EE6B2800" ]] || { echo "error: asn1 encode gave '$hex'" >&2; exit 1; }

echo "smoke test: generated code and runtime packages in a new project"
mkdir -p "$work/app"
"$work/tools/asn1" generate-csharp "$root/samples/telemetry/telemetry.asn" -o "$work/app/Generated" -n Telemetry --quiet
cat > "$work/app/App.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DotAsn.Serialization" Version="$version" />
    <PackageReference Include="DotAsn.Dynamic" Version="$version" />
  </ItemGroup>
</Project>
XML
cat > "$work/app/Program.cs" <<'CS'
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;
using Telemetry;

// Generated types.
var heartbeat = new Heartbeat { SequenceNumber = 7, UptimeSeconds = 4_000_000_000 };
var uper = Asn1Serializer.Encode(heartbeat, Asn1EncodingRules.Uper);
var back = Asn1Serializer.Decode<Heartbeat>(uper, Asn1EncodingRules.Uper);
if (!back.Equals(heartbeat)) throw new Exception("round trip failed");

// Schema-driven (dynamic) values.
var compilation = Asn1Compilation.FromText(File.ReadAllText(args[0]));
var type = new DynamicTypeFactory().Create(compilation.FindType("Heartbeat")!);
var der = Asn1EncodingRules.Der.Encode(type, DynamicJson.Parse(type, """{"sequenceNumber":7,"uptimeSeconds":4000000000}"""));
Console.WriteLine(Convert.ToHexString(uper) + " " + Convert.ToHexString(der));
CS
if ! output="$(cd "$work/app" && dotnet run -- "$root/samples/telemetry/telemetry.asn" 2>&1)"; then
  echo "error: the package smoke test app failed:" >&2; echo "$output" >&2; exit 1
fi
result="$(echo "$output" | tail -1)"
[[ "$result" == "07EE6B2800 300A800107810500EE6B2800" ]] || { echo "error: package smoke test printed '$result'" >&2; exit 1; }
echo "smoke tests passed"
