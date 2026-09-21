#!/bin/sh
# Install ndx.
# The script is published on GitHub Releases. The binary comes from the
# nuget.org RID package; the blob feed is used when nuget.org is unreachable.
#   curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/install.sh | sh
# Env: NDX_VERSION NDX_PREFIX NDX_ARCHIVE NDX_RID NDX_REPO NDX_SKIP_PATH
#      NDX_NUGET_FLAT NDX_NUGET_REG NDX_BLOB_FLAT
set -eu

REPO="${NDX_REPO:-devlooped/ndx}"
VERSION="${NDX_VERSION:-}"
PREFIX="${NDX_PREFIX:-${HOME}/.local/bin}"
ARCHIVE="${NDX_ARCHIVE:-}"
RID="${NDX_RID:-}"
SKIP_PATH="${NDX_SKIP_PATH:-0}"
NUGET_FLAT="${NDX_NUGET_FLAT:-https://api.nuget.org/v3-flatcontainer}"
NUGET_REG="${NDX_NUGET_REG:-https://api.nuget.org/v3/registration5-gz-semver2}"
BLOB_FLAT="${NDX_BLOB_FLAT:-https://kzu.blob.core.windows.net/nuget/flatcontainer}"

is_musl() {
    # Alpine and other musl hosts. gcompat may also add a glibc loader; the musl
    # loader is still the host libc, so prefer the musl build when it is present.
    if [ -f /etc/alpine-release ]; then
        return 0
    fi
    for loader in /lib/ld-musl-*.so*; do
        if [ -e "$loader" ]; then
            return 0
        fi
    done
    if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
        return 0
    fi
    return 1
}

detect_rid() {
    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m | tr '[:upper:]' '[:lower:]')

    case "$arch" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *)
            echo "ndx: unsupported architecture '$arch'" >&2
            exit 1
            ;;
    esac

    case "$os" in
        linux)
            if is_musl; then
                echo "linux-musl-${arch}"
            else
                echo "linux-${arch}"
            fi
            ;;
        darwin) echo "osx-${arch}" ;;
        mingw*|msys*|cygwin*) echo "win-${arch}" ;;
        *)
            echo "ndx: unsupported OS '$os'" >&2
            exit 1
            ;;
    esac
}

download() {
    url=$1
    dest=$2
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url" -o "$dest"
    else
        wget -qO "$dest" "$url"
    fi
}

# JSON from nuget.org registration and the blob feed is gzip content-encoded.
# curl --compressed unwraps it. A still-gzipped body (wget) is inflated here.
# Do not use this for release archives: those files are themselves gzip.
fetch() {
    url=$1
    dest=$2
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --compressed "$url" -o "$dest" 2>/dev/null || return 1
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$dest" "$url" || return 1
    else
        echo "ndx: need curl or wget" >&2
        exit 1
    fi
    if command -v gzip >/dev/null 2>&1 && gzip -t "$dest" 2>/dev/null; then
        gzip -dc "$dest" > "${dest}.raw" || return 1
        mv "${dest}.raw" "$dest"
    fi
    return 0
}

version_gt() {
    _lhs=$1
    _rhs=$2
    _oa1=0; _oa2=0; _oa3=0; _oa4=0
    _ob1=0; _ob2=0; _ob3=0; _ob4=0
    IFS=. read -r _oa1 _oa2 _oa3 _oa4 <<EOF
${_lhs}
EOF
    IFS=. read -r _ob1 _ob2 _ob3 _ob4 <<EOF
${_rhs}
EOF
    _oa1=$(printf '%s' "${_oa1:-0}" | sed 's/^0*//;s/^$/0/')
    _oa2=$(printf '%s' "${_oa2:-0}" | sed 's/^0*//;s/^$/0/')
    _oa3=$(printf '%s' "${_oa3:-0}" | sed 's/^0*//;s/^$/0/')
    _oa4=$(printf '%s' "${_oa4:-0}" | sed 's/^0*//;s/^$/0/')
    _ob1=$(printf '%s' "${_ob1:-0}" | sed 's/^0*//;s/^$/0/')
    _ob2=$(printf '%s' "${_ob2:-0}" | sed 's/^0*//;s/^$/0/')
    _ob3=$(printf '%s' "${_ob3:-0}" | sed 's/^0*//;s/^$/0/')
    _ob4=$(printf '%s' "${_ob4:-0}" | sed 's/^0*//;s/^$/0/')
    if [ "$_oa1" -gt "$_ob1" ]; then return 0; fi
    if [ "$_oa1" -lt "$_ob1" ]; then return 1; fi
    if [ "$_oa2" -gt "$_ob2" ]; then return 0; fi
    if [ "$_oa2" -lt "$_ob2" ]; then return 1; fi
    if [ "$_oa3" -gt "$_ob3" ]; then return 0; fi
    if [ "$_oa3" -lt "$_ob3" ]; then return 1; fi
    if [ "$_oa4" -gt "$_ob4" ]; then return 0; fi
    return 1
}

latest_stable() {
    base=$1
    id=$(printf '%s' "$2" | tr '[:upper:]' '[:lower:]')
    index="${tmp}/versions.json"
    if ! fetch "${base%/}/${id}/index.json" "$index"; then
        return 1
    fi
    versions=$(grep -oE '"[0-9][^"]*"' "$index" | tr -d '"' || true)
    best=
    set -f
    for v in $versions; do
        case "$v" in
            *[!0-9.]*) continue ;;
        esac
        if [ -z "$best" ] || version_gt "$v" "$best"; then
            best=$v
        fi
    done
    set +f
    if [ -z "$best" ]; then
        return 1
    fi
    printf '%s' "$best"
}

catalog_hash() {
    reg=$1
    id=$2
    ver=$3
    leaf="${tmp}/leaf.json"
    entry="${tmp}/catalog.json"
    if ! fetch "${reg%/}/${id}/${ver}.json" "$leaf"; then
        return 1
    fi
    catalog=$(json_string catalogEntry < "$leaf" || true)
    if [ -z "$catalog" ]; then
        return 1
    fi
    if ! fetch "$catalog" "$entry"; then
        return 1
    fi
    hash=$(json_string packageHash < "$entry" || true)
    algo=$(json_string packageHashAlgorithm < "$entry" || true)
    case "$algo" in
        ""|SHA512|sha512) ;;
        *) return 1 ;;
    esac
    if [ -z "$hash" ]; then
        return 1
    fi
    printf '%s' "$hash"
}

sha512_b64() {
    file=$1
    if command -v openssl >/dev/null 2>&1; then
        digest=$(openssl dgst -sha512 -binary "$file" | openssl base64 | tr -d '\n\r ')
        if [ -n "$digest" ]; then
            printf '%s' "$digest"
            return 0
        fi
    fi
    if command -v python3 >/dev/null 2>&1; then
        python3 -c 'import hashlib,base64,sys; sys.stdout.write(base64.b64encode(hashlib.sha512(open(sys.argv[1],"rb").read()).digest()).decode())' "$file"
        return 0
    fi
    return 1
}

download_package() {
    id=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
    ver=$(printf '%s' "$2" | tr '[:upper:]' '[:lower:]')
    dest=$3
    rel="${id}/${ver}/${id}.${ver}.nupkg"

    if fetch "${NUGET_FLAT%/}/${rel}" "$dest"; then
        if expected=$(catalog_hash "$NUGET_REG" "$id" "$ver"); then
            actual=$(sha512_b64 "$dest") || {
                echo "ndx: no sha512 tool found (openssl or python3)" >&2
                exit 1
            }
            if [ "$actual" != "$expected" ]; then
                echo "ndx: SHA512 mismatch for ${id}.${ver}.nupkg" >&2
                echo "  expected: $expected" >&2
                echo "  actual:   $actual" >&2
                exit 1
            fi
            return 0
        fi
        rm -f "$dest"
    fi

    fetch "${BLOB_FLAT%/}/${rel}" "$dest"
}

extract_nupkg_binary() {
    nupkg=$1
    dest=$2
    entry="tools/any/${RID}/${binary}"
    if command -v unzip >/dev/null 2>&1; then
        if unzip -p "$nupkg" "$entry" > "$dest" 2>/dev/null && [ -s "$dest" ]; then
            return 0
        fi
        rm -f "$dest"
    fi
    if command -v python3 >/dev/null 2>&1; then
        if python3 -c 'import sys,zipfile
z=zipfile.ZipFile(sys.argv[1])
with z.open(sys.argv[2]) as src, open(sys.argv[3],"wb") as dst:
    dst.write(src.read())' "$nupkg" "$entry" "$dest" && [ -s "$dest" ]; then
            return 0
        fi
        rm -f "$dest"
    fi
    echo "ndx: package did not contain ${entry}" >&2
    exit 1
}

json_string() {
    # Extract the first JSON string value for a given key without jq.
    key=$1
    sed -n "s/.*\"${key}\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" | head -n 1
}

verify_sha256() {
    file=$1
    expected=$2
    expected=$(printf '%s' "$expected" | tr '[:upper:]' '[:lower:]' | awk '{print $1}')
    if command -v sha256sum >/dev/null 2>&1; then
        actual=$(sha256sum "$file" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        actual=$(shasum -a 256 "$file" | awk '{print $1}')
    elif command -v openssl >/dev/null 2>&1; then
        actual=$(openssl dgst -sha256 "$file" | awk '{print $NF}')
    else
        echo "ndx: no sha256 tool found (sha256sum, shasum, or openssl)" >&2
        exit 1
    fi
    actual=$(printf '%s' "$actual" | tr '[:upper:]' '[:lower:]')
    if [ "$actual" != "$expected" ]; then
        echo "ndx: SHA256 mismatch for $(basename "$file")" >&2
        echo "  expected: $expected" >&2
        echo "  actual:   $actual" >&2
        exit 1
    fi
}

if [ -z "$RID" ]; then
    RID=$(detect_rid)
fi
RID=$(printf '%s' "$RID" | tr '[:upper:]' '[:lower:]')

case "$RID" in
    win-*) binary=ndx.exe; ext=zip ;;
    linux-*|osx-*) binary=ndx; ext=tar.gz ;;
    *)
        echo "ndx: unsupported RID '$RID'" >&2
        exit 1
        ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

from_package=0
if [ -z "$ARCHIVE" ]; then
    pkg="ndx.${RID}"
    if [ -n "$VERSION" ]; then
        case "$(printf '%s' "$VERSION" | tr '[:upper:]' '[:lower:]')" in
            ci)
                tag=ci
                version=ci
                ;;
            *)
                version=$(printf '%s' "$VERSION" | sed 's/^[vV]//')
                from_package=1
                ;;
        esac
    else
        # GitHub's unauthenticated releases API returns 403 once the hourly
        # quota is spent. The RID package on nuget.org is the same binary.
        version=$(latest_stable "$NUGET_FLAT" "$pkg" || true)
        if [ -z "$version" ]; then
            version=$(latest_stable "$BLOB_FLAT" "$pkg" || true)
        fi
        if [ -z "$version" ]; then
            echo "ndx: could not resolve the latest stable version of ${pkg}" >&2
            exit 1
        fi
        from_package=1
    fi

    if [ "$from_package" = 1 ]; then
        nupkg="${tmp}/package.nupkg"
        if ! download_package "$pkg" "$version" "$nupkg"; then
            echo "ndx: could not download ${pkg} ${version}" >&2
            exit 1
        fi
    else
        name="ndx-${version}-${RID}.${ext}"
        base="https://github.com/${REPO}/releases/download/${tag}"
        ARCHIVE="${tmp}/${name}"
        download "${base}/${name}" "$ARCHIVE"
        download "${base}/${name}.sha256" "${ARCHIVE}.sha256"
        expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
        verify_sha256 "$ARCHIVE" "$expected"
    fi
else
    if [ -f "${ARCHIVE}.sha256" ]; then
        expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
        verify_sha256 "$ARCHIVE" "$expected"
    fi
fi

extract="${tmp}/extract"
mkdir -p "$extract"
if [ "$from_package" = 1 ]; then
    extract_nupkg_binary "$nupkg" "${extract}/${binary}"
else
    case "$ext" in
        zip)
            if command -v unzip >/dev/null 2>&1; then
                unzip -o -q "$ARCHIVE" -d "$extract"
            else
                echo "ndx: unzip is required to extract Windows archives" >&2
                exit 1
            fi
            ;;
        tar.gz)
            tar -xzf "$ARCHIVE" -C "$extract"
            ;;
    esac
fi

if [ ! -f "${extract}/${binary}" ]; then
    echo "ndx: archive did not contain ${binary}" >&2
    exit 1
fi

mkdir -p "$PREFIX"
# Prefer install(1) so the dest is replaced atomically and marked executable.
if command -v install >/dev/null 2>&1; then
    install -m 0755 "${extract}/${binary}" "${PREFIX}/${binary}"
else
    cp "${extract}/${binary}" "${PREFIX}/${binary}"
    chmod 0755 "${PREFIX}/${binary}"
fi

echo "installed ${PREFIX}/${binary}"

write_path_block() {
    file=$1
    kind=$2
    mkdir -p "$(dirname "$file")"
    tmpfile="${file}.ndx.tmp.$$"
    if [ -f "$file" ]; then
        awk '
            BEGIN { skip=0 }
            /# >>> ndx path >>>/ { skip=1; next }
            /# <<< ndx path <<</ { skip=0; next }
            skip==0 { print }
        ' "$file" > "$tmpfile"
    else
        : > "$tmpfile"
    fi
    if [ -s "$tmpfile" ]; then
        printf '\n' >> "$tmpfile"
    fi
    if [ "$kind" = fish ]; then
        cat >> "$tmpfile" <<EOF
# >>> ndx path >>>
fish_add_path ${PREFIX}
# <<< ndx path <<<
EOF
    else
        cat >> "$tmpfile" <<EOF
# >>> ndx path >>>
case ":\$PATH:" in
  *":${PREFIX}:"*) ;;
  *) export PATH="${PREFIX}:\$PATH" ;;
esac
# <<< ndx path <<<
EOF
    fi
    mv "$tmpfile" "$file"
    echo "PATH configured in $file"
}

if [ "$SKIP_PATH" != "1" ]; then
    shell_name=$(basename "${SHELL:-/bin/sh}")
    case "$shell_name" in
        zsh)
            write_path_block "${HOME}/.zshrc" posix
            ;;
        bash)
            write_path_block "${HOME}/.bashrc" posix
            write_path_block "${HOME}/.profile" posix
            ;;
        fish)
            write_path_block "${HOME}/.config/fish/config.fish" fish
            ;;
        *)
            write_path_block "${HOME}/.profile" posix
            ;;
    esac

    case ":${PATH}:" in
        *":${PREFIX}:"*) ;;
        *)
            echo "restart your shell so ndx is on PATH" >&2
            ;;
    esac
fi
