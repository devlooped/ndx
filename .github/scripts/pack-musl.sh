#!/bin/sh
# Pack one musl Native AOT archive. Run inside mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23-aot
# (or any Alpine SDK image with a compiler). The host job invokes this via docker
# so GitHub's glibc node can still run actions/checkout.
# Usage: pack-musl.sh <rid> <archive-version>
set -eu

rid=${1:?rid}
version=${2:?version}

# Native AOT on Alpine needs clang and zlib headers. The -aot image already has
# them; apk keeps a plain Alpine SDK image working too.
apk add --no-cache clang build-base zlib-dev

# Same RID pack as the glibc jobs. linux-musl-* is in the project's
# RuntimeIdentifiers, so the pointer package depends on this nupkg.
dotnet pack src/ndx/ndx.csproj \
  -c "${Configuration:-Release}" \
  -r "$rid" \
  -bl:"pack-${rid}.binlog"

count=0
nupkg=
for f in bin/ndx."$rid".*.nupkg; do
  [ -f "$f" ] || continue
  case $f in
    *.symbols.nupkg) continue ;;
  esac
  nupkg=$f
  count=$((count + 1))
done

if [ "$count" -ne 1 ]; then
  echo "Expected one RID nupkg for $rid, found $count." >&2
  ls -la bin >&2 || true
  exit 1
fi

mkdir -p artifacts
dotnet run --project src/nativepack --no-launch-profile -c "${Configuration:-Release}" -- "$nupkg" "$rid" artifacts "$version"
