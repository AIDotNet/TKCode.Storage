#!/usr/bin/env bash
# Packs every plugin of the O_O plugin catalog (catalog/plugins/<id>) into <out>/<id>-<version>.zip, checked exactly as
# installing would (O_O --pack-plugin). The O_O service serves these (docs/design/08-updates-and-catalog.md).
#
#   catalog/pack.sh [out-dir]          default: build/out/catalog
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"            # the O_O folder
OUT="${1:-$ROOT/build/out/catalog}"
mkdir -p "$OUT"

echo "==> Building the packer"
dotnet build "$ROOT/src/Island.App" -c Release -o "$ROOT/build/out/packer" >/dev/null
PACKER="$ROOT/build/out/packer/O_O"
[[ -x "$PACKER" ]] || PACKER="$ROOT/build/out/packer/O_O.exe"

for dir in "$ROOT"/catalog/plugins/*/; do
  id="$(basename "$dir")"
  version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$dir/plugin.json")"
  "$PACKER" --pack-plugin "$dir" "$OUT/$id-$version.zip"
done

echo "==> Done: $(ls "$OUT"/*.zip | wc -l | tr -d ' ') packages in $OUT"
