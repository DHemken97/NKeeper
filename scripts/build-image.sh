#!/usr/bin/env bash
# Builds NKeeper as a single container image (nginx, API, PostgreSQL and guacd together; see
# container/Dockerfile), for Proxmox VE OCI containers, a registry, or plain `docker run`.
#
#   scripts/build-image.sh                          dist/nkeeper-<version>-amd64.tar (OCI archive)
#   scripts/build-image.sh --push ghcr.io/me/nkeeper    also push <version> and latest to a registry
#   scripts/build-image.sh --load                   also load it into the local Docker as nkeeper:<version>
#   scripts/build-image.sh --platform linux/amd64,linux/arm64
#
# The archive is what Proxmox VE uploads as a container template (an OCI archive, not `docker save`
# output). Needs Docker with buildx (Docker Desktop on Windows/macOS, or docker-ce on Linux).
set -euo pipefail

# In Git Bash on Windows, a C:/... path: docker.exe can't use /c/... when path conversion is off.
REPO=$(cd "$(dirname "$0")/.." && { pwd -W 2>/dev/null || pwd; })
BUILDER=nkeeper-builder

name=nkeeper version="" platforms=linux/amd64 push="" load=false archive=true out="$REPO/dist"

usage() {
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    cat <<EOF

Options:
  --version V        Image version (default: git describe, e.g. 1.2.0 or 327c499)
  --name NAME        Local image and archive name (default: nkeeper)
  --platform LIST    Comma-separated platforms (default: linux/amd64); one archive each
  --push REPOSITORY  Push REPOSITORY:<version> and REPOSITORY:latest (run docker login first)
  --load             Load the image into the local Docker engine (one platform only)
  --no-archive       Don't write the OCI archive
  --output DIR       Where archives go (default: dist/)
EOF
}

die() { echo "build-image: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=${2:?--version needs a value}; shift ;;
        --name) name=${2:?--name needs a value}; shift ;;
        --platform) platforms=${2:?--platform needs a value}; shift ;;
        --push) push=${2:?--push needs a repository}; shift ;;
        --load) load=true ;;
        --no-archive) archive=false ;;
        --output) out=${2:?--output needs a directory}; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option $1 (see --help)" ;;
    esac
    shift
done

command -v docker >/dev/null || die "Docker isn't installed."
docker buildx version >/dev/null 2>&1 || die "Docker buildx isn't available. Install Docker Desktop or the docker-buildx plugin."
docker info >/dev/null 2>&1 || die "Docker isn't running, or you can't use it."

if [ -z "$version" ]; then
    version=$(git -C "$REPO" describe --tags --always --dirty 2>/dev/null || echo dev)
    version=${version#v}
fi
case "$version" in *[!A-Za-z0-9_.-]*) die "the version may only contain letters, digits, '.', '_' and '-'." ;; esac
if $load && [[ "$platforms" == *,* ]]; then
    die "--load takes one platform; drop it or pass a single --platform."
fi
$archive || [ -n "$push" ] || $load || die "nothing to do: --no-archive needs --push or --load."

# A BuildKit builder of its own: it can write OCI archives and cross-build other platforms.
if ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
    echo "==> Creating the buildx builder $BUILDER"
    docker buildx create --name "$BUILDER" --driver docker-container >/dev/null
fi

build() {
    # No provenance attestation: it adds an "unknown/unknown" platform entry some importers choke on.
    docker buildx build --builder "$BUILDER" -f "$REPO/container/Dockerfile" --provenance=false \
        --build-arg "NKEEPER_VERSION=$version" "$@" "$REPO"
}

if $archive; then
    mkdir -p "$out"
    IFS=',' read -ra list <<<"$platforms"
    for platform in "${list[@]}"; do
        arch=${platform#*/}
        arch=${arch//\//}
        file="$out/$name-$version-$arch.tar"
        echo "==> Building $name:$version for $platform -> $file"
        build --platform "$platform" --output "type=oci,dest=$file,name=$name:$version"
    done
fi

if $load; then
    echo "==> Loading $name:$version into Docker"
    build --platform "$platforms" -t "$name:$version" -t "$name:latest" --load
fi

if [ -n "$push" ]; then
    echo "==> Pushing $push:$version and $push:latest"
    build --platform "$platforms" -t "$push:$version" -t "$push:latest" --push
fi

echo
echo "Done: NKeeper $version."
if $archive; then
    echo "  Archives: $out/$name-$version-*.tar"
    echo "  Proxmox VE: upload it under a storage's CT Templates (Upload), then Create CT from it."
fi
[ -z "$push" ] || echo "  Pushed: $push:$version (and latest)"
$load && echo "  Run: docker run -d --name nkeeper -p 443:443 -p 80:80 -v nkeeper-data:/var/lib/nkeeper $name:$version"
echo "  See docs/installation.md#single-container-image-proxmox-ve for the rest."
