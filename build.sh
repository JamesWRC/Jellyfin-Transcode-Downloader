#!/bin/bash
# Builds the plugin with the .NET SDK in a container (no .NET needed on the host).
# Runs the unit tests first and REFUSES to publish when any of them fails.
#
#   ./build.sh [VERSION] [OUT_DIR]
#       VERSION   assembly/plugin version, e.g. 1.0.27.3   (default 0.0.0)
#       OUT_DIR   where the published plugin goes (default ./out); the files land in OUT_DIR/pub
#
#   SKIP_TESTS=1 ./build.sh ...      publish without running the tests. Loud on purpose: only for a
#                                    build you cannot otherwise get, never for something you ship.
#   JELLYFIN_VERSION=12.4.0 ./build.sh ...   build and test against another Jellyfin package version
#   SDK_IMAGE=...  NUGET_CACHE=...   override the SDK image / the package cache directory
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:-0.0.0}"
OUT="${2:-$HERE/out}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
NUGET_CACHE="${NUGET_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/transcode-downloader-nuget}"
TESTS=tests/Jellyfin.Plugin.TranscodeDownloader.Tests/Jellyfin.Plugin.TranscodeDownloader.Tests.csproj

mkdir -p "$OUT" "$NUGET_CACHE"
OUT="$(cd "$OUT" && pwd)"

if [ -n "${SKIP_TESTS:-}" ]; then
    echo "################################################################" >&2
    echo "# WARNING: SKIP_TESTS is set. The unit tests will NOT run, and   #" >&2
    echo "# this build has not been checked. Do not release it.            #" >&2
    echo "################################################################" >&2
fi

docker run --rm --user "$(id -u):$(id -g)" \
    -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/nuget \
    -e SKIP_TESTS="${SKIP_TESTS:-}" -e VERSION="$VERSION" -e TESTS="$TESTS" -e JELLYFIN_VERSION="${JELLYFIN_VERSION:-}" \
    -v "$HERE":/src:ro -v "$NUGET_CACHE":/nuget -v "$OUT":/out \
    "$SDK_IMAGE" sh -c '
        set -e
        # Work on a copy so the build never writes bin/ or obj/ into the repository.
        rm -rf /tmp/work && cp -r /src /tmp/work && cd /tmp/work
        find . -name bin -o -name obj | xargs rm -rf
        jf=""
        [ -n "$JELLYFIN_VERSION" ] && jf="-p:JellyfinVersion=$JELLYFIN_VERSION"
        if [ -z "$SKIP_TESTS" ]; then
            echo "== unit tests"
            if ! dotnet test "$TESTS" -c Release $jf; then
                echo "== unit tests FAILED: nothing was published" >&2
                exit 1
            fi
        fi
        echo "== publish $VERSION"
        rm -rf /out/pub
        dotnet publish src/JellyfinTranscodeDownloader.csproj -c Release -o /out/pub -p:Version="$VERSION" $jf
    '

echo "Published to $OUT/pub"
