#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

IMAGE_NAME="${IMAGE_NAME:-localhost/podish-wayland-utils:alpine}"
CONTAINERFILE="${CONTAINERFILE:-$PROJECT_ROOT/tests/integration/Containerfile.wayland-utils}"
OCI_ARCHIVE="${OCI_ARCHIVE:-$PROJECT_ROOT/.tmp/podish-wayland-utils.oci.tar}"
IMAGE_ARCH="${IMAGE_ARCH:-386}"
LOG_LEVEL="${LOG_LEVEL:-debug}"
LOG_FILE="${LOG_FILE:-$PROJECT_ROOT/.tmp/weston-stacking.log}"
DESKTOP_SIZE="${DESKTOP_SIZE:-1024x768}"
GUEST_CMD="${GUEST_CMD:-/usr/bin/weston-stacking}"
SKIP_BUILD="${SKIP_BUILD:-0}"
SKIP_LOAD="${SKIP_LOAD:-0}"
BUILD_ONLY="${BUILD_ONLY:-0}"
EXTRA_RUN_ARGS="${EXTRA_RUN_ARGS:-}"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        log_error "missing required command: $1"
        exit 1
    fi
}

build_image() {
    log_info "Building Wayland test image: $IMAGE_NAME"
    podman build --arch "$IMAGE_ARCH" -f "$CONTAINERFILE" -t "$IMAGE_NAME" "$PROJECT_ROOT"
}

export_oci_archive() {
    mkdir -p "$(dirname "$OCI_ARCHIVE")"
    rm -f "$OCI_ARCHIVE"

    log_info "Saving Podman image as OCI archive: $OCI_ARCHIVE"
    podman save --format oci-archive -o "$OCI_ARCHIVE" "$IMAGE_NAME"
}

load_into_podish() {
    log_info "Loading OCI archive into .fiberpod"
    dotnet run --project "$PROJECT_ROOT/Podish.Cli/Podish.Cli.csproj" --no-build -- \
        load -i "$OCI_ARCHIVE"
}

run_debug() {
    mkdir -p "$(dirname "$LOG_FILE")"

    local podish_args=(
        run
        --rm
        --wayland-server
        --wayland-desktop-size "$DESKTOP_SIZE"
        --strace
        --log-level "$LOG_LEVEL"
        --log-file "$LOG_FILE"
    )

    if [[ -n "$EXTRA_RUN_ARGS" ]]; then
        # shellcheck disable=SC2206
        local extra_args=( $EXTRA_RUN_ARGS )
        podish_args+=("${extra_args[@]}")
    fi

    podish_args+=(
        "$IMAGE_NAME"
        --
        $GUEST_CMD
    )

    log_info "Running Wayland debug flow"
    log_info "Engine log: $LOG_FILE"
    dotnet run --project "$PROJECT_ROOT/Podish.Cli/Podish.Cli.csproj" --no-build -- "${podish_args[@]}"
}

main() {
    require_cmd podman
    require_cmd dotnet

    if [[ "$SKIP_BUILD" != "1" ]]; then
        build_image
    else
        log_warn "Skipping image build"
    fi

    export_oci_archive

    if [[ "$SKIP_LOAD" != "1" ]]; then
        load_into_podish
    else
        log_warn "Skipping OCI archive load into .fiberpod"
    fi

    if [[ "$BUILD_ONLY" == "1" ]]; then
        log_info "Build/load only requested; skipping run"
        exit 0
    fi

    run_debug
}

main "$@"
