#!/bin/sh
# Install ndx from GitHub Releases.
#   curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/install.sh | sh
set -eu

REPO="${NDX_REPO:-devlooped/ndx}"
VERSION="${NDX_VERSION:-}"
PREFIX="${NDX_PREFIX:-${HOME}/.local/bin}"
ARCHIVE="${NDX_ARCHIVE:-}"
RID="${NDX_RID:-}"
SKIP_PATH="${NDX_SKIP_PATH:-0}"

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

github_json() {
    url=$1
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -H "Accept: application/vnd.github+json" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO- --header="Accept: application/vnd.github+json" "$url"
    else
        echo "ndx: need curl or wget" >&2
        exit 1
    fi
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

if [ -z "$ARCHIVE" ]; then
    if [ -n "$VERSION" ]; then
        case "$(printf '%s' "$VERSION" | tr '[:upper:]' '[:lower:]')" in
            ci)
                tag=ci
                version=ci
                ;;
            v*)
                tag=$VERSION
                version=${tag#v}
                ;;
            *)
                tag="v${VERSION}"
                version=$VERSION
                ;;
        esac
    else
        json=$(github_json "https://api.github.com/repos/${REPO}/releases/latest")
        tag=$(printf '%s' "$json" | json_string tag_name)
        if [ -z "$tag" ]; then
            echo "ndx: could not resolve latest release of ${REPO}" >&2
            exit 1
        fi
        version=${tag#v}
    fi

    name="ndx-${version}-${RID}.${ext}"
    base="https://github.com/${REPO}/releases/download/${tag}"
    ARCHIVE="${tmp}/${name}"
    download "${base}/${name}" "$ARCHIVE"
    download "${base}/${name}.sha256" "${ARCHIVE}.sha256"
    expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
    verify_sha256 "$ARCHIVE" "$expected"
else
    if [ -f "${ARCHIVE}.sha256" ]; then
        expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
        verify_sha256 "$ARCHIVE" "$expected"
    fi
fi

extract="${tmp}/extract"
mkdir -p "$extract"
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
