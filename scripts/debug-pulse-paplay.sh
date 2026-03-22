#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

IMAGE_NAME="${IMAGE_NAME:-localhost/podish-paplay:alpine}"
CONTAINERFILE="${CONTAINERFILE:-$PROJECT_ROOT/tests/integration/Containerfile.pulse-paplay}"
OCI_ARCHIVE="${OCI_ARCHIVE:-$PROJECT_ROOT/.tmp/podish-paplay.oci.tar}"
IMAGE_ARCH="${IMAGE_ARCH:-386}"
LOG_LEVEL="${LOG_LEVEL:-debug}"
LOG_FILE="${LOG_FILE:-$PROJECT_ROOT/.tmp/pulse-paplay.log}"
PCM_FILE="${PCM_FILE:-$PROJECT_ROOT/.tmp/pulse-silence.s16le}"
SAMPLE_RATE="${SAMPLE_RATE:-48000}"
CHANNELS="${CHANNELS:-2}"
AUDIO_SECONDS="${AUDIO_SECONDS:-1}"
BUILD_ONLY="${BUILD_ONLY:-0}"
SKIP_BUILD="${SKIP_BUILD:-0}"
SKIP_LOAD="${SKIP_LOAD:-0}"
EXTRA_RUN_ARGS="${EXTRA_RUN_ARGS:-}"
DISABLE_PULSE_SHM="${DISABLE_PULSE_SHM:-1}"

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
    log_info "Building PulseAudio paplay debug image: $IMAGE_NAME"
    podman build --arch "$IMAGE_ARCH" -f "$CONTAINERFILE" -t "$IMAGE_NAME" "$PROJECT_ROOT"
}

export_rootfs() {
    :
}

export_oci_archive() {
    mkdir -p "$(dirname "$OCI_ARCHIVE")"
    rm -f "$OCI_ARCHIVE"

    log_info "Saving Podman image as OCI archive: $OCI_ARCHIVE"
    podman save --format oci-archive -o "$OCI_ARCHIVE" "$IMAGE_NAME"
}

load_into_fiberpod() {
    log_info "Loading OCI archive into .fiberpod"
    dotnet run --project "$PROJECT_ROOT/Podish.Cli/Podish.Cli.csproj" -- \
        load -i "$OCI_ARCHIVE"
}

generate_pcm() {
    mkdir -p "$(dirname "$PCM_FILE")"
    local bytes_per_second=$(( SAMPLE_RATE * CHANNELS * 2 ))
    local total_bytes=$(( bytes_per_second * AUDIO_SECONDS ))

    log_info "Generating raw PCM file: $PCM_FILE (${AUDIO_SECONDS}s, ${SAMPLE_RATE} Hz, ${CHANNELS} ch)"
    python3 - <<PY
from pathlib import Path
path = Path(r"$PCM_FILE")
path.write_bytes(b"\x00" * $total_bytes)
PY
}

run_debug() {
    mkdir -p "$(dirname "$LOG_FILE")"

    local podish_args=(
        run
        --rm
        --pulse-server
        --log-level "$LOG_LEVEL"
        --log-file "$LOG_FILE"
        --volume "$PCM_FILE:/tmp/input.s16le"
        --
        "$IMAGE_NAME"
        /bin/sh
        -lc
        "paplay --version && paplay --raw --format=s16le --rate=$SAMPLE_RATE --channels=$CHANNELS /tmp/input.s16le"
    )

    if [[ "$DISABLE_PULSE_SHM" == "1" ]]; then
        podish_args=(
            run
            --rm
            --pulse-server
            --log-level "$LOG_LEVEL"
            --log-file "$LOG_FILE"
            --volume "$PCM_FILE:/tmp/input.s16le"
            --env "PULSE_SHM=0"
            --env "PULSE_MEMFD=0"
            --
            "$IMAGE_NAME"
            /bin/sh
            -lc
            "paplay --version && paplay --raw --format=s16le --rate=$SAMPLE_RATE --channels=$CHANNELS /tmp/input.s16le"
        )
    fi

    if [[ -n "$EXTRA_RUN_ARGS" ]]; then
        # shellcheck disable=SC2206
        local extra_args=( $EXTRA_RUN_ARGS )
        podish_args=(run "${extra_args[@]}" "${podish_args[@]:1}")
    fi

    log_info "Running Podish pulse debug flow"
    log_info "Engine log: $LOG_FILE"
    dotnet run --project "$PROJECT_ROOT/Podish.Cli/Podish.Cli.csproj" -- "${podish_args[@]}"
}

main() {
    require_cmd podman
    require_cmd dotnet
    require_cmd python3

    if [[ "$SKIP_BUILD" != "1" ]]; then
        build_image
    else
        log_warn "Skipping image build"
    fi

    export_oci_archive

    if [[ "$SKIP_LOAD" != "1" ]]; then
        load_into_fiberpod
    else
        log_warn "Skipping OCI archive load into .fiberpod"
    fi

    if [[ "$BUILD_ONLY" == "1" ]]; then
        log_info "Build-only mode complete"
        exit 0
    fi

    generate_pcm
    run_debug

    log_info "Finished. Inspect the engine log at $LOG_FILE"
}

main "$@"
