#!/usr/bin/env sh
# Fetches third-party ASN.1 specifications (3GPP, ETSI, OMA, IEEE, CEN) used by the extended corpus tests.
# They are not committed because they are not redistributable under the repository license.
set -e
cd "$(dirname "$0")/.."
mkdir -p .cache
if [ ! -d .cache/asn1tools ]; then
  git clone --depth 1 https://github.com/eerimoq/asn1tools.git .cache/asn1tools
fi
echo "extended corpus available in .cache/asn1tools/tests/files"
