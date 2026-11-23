#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
bench=benchmark/emulator-comparison
host_image=${BENCH_HOST_IMAGE:-podish-benchmark-host:local}
fixture_image=${BENCH_FIXTURE_IMAGE:-podish-benchmark-fixture:local}
ssh_options=(-o BatchMode=yes)
if [[ -n "${BENCH_SSH_CONFIG:-}" ]]; then
    ssh_options+=(-F "$BENCH_SSH_CONFIG")
fi

safe_image_name() {
    if [[ ! "$1" =~ ^[a-zA-Z0-9./:_-]+$ ]]; then
        printf 'Invalid image name %s\n' "$1" >&2
        exit 2
    fi
}

safe_image_name "$host_image"
safe_image_name "$fixture_image"

case "${1:-}" in
    build-host)
        commit=$(git rev-parse HEAD)
        mirror=${BENCH_UBUNTU_MIRROR:-}
        if [[ -n "$mirror" && ! "$mirror" =~ ^https://[a-zA-Z0-9./_-]+$ ]]; then
            printf 'Use an HTTPS Ubuntu package mirror URL.\n' >&2
            exit 2
        fi
        # An explicit member list avoids forwarding ignored build caches or artifacts.
        if [[ $# -eq 2 ]]; then
            git ls-files -z --cached --others --exclude-standard |
                COPYFILE_DISABLE=1 tar --no-xattrs --no-recursion --null -cf - -T - |
                ssh "${ssh_options[@]}" "$2" "
                    set -eu
                    scratch=\$(mktemp -d)
                    trap 'rm -rf \"\$scratch\"' EXIT
                    tar -xf - -C \"\$scratch\"
                    if ! docker buildx version >/dev/null 2>&1; then export DOCKER_BUILDKIT=0; fi
                    docker build --build-arg PODISH_BENCH_COMMIT=$commit --build-arg PODISH_UBUNTU_MIRROR=$mirror \
                        -f \"\$scratch/$bench/Dockerfile.host\" -t $host_image \"\$scratch\"
                "
        elif [[ $# -eq 1 ]]; then
            scratch=$(mktemp -d)
            trap 'rm -rf "$scratch"' EXIT
            git ls-files -z --cached --others --exclude-standard |
                COPYFILE_DISABLE=1 tar --no-xattrs --no-recursion --null -cf - -T - |
                tar -xf - -C "$scratch"
            docker build --build-arg "PODISH_BENCH_COMMIT=$commit" --build-arg "PODISH_UBUNTU_MIRROR=$mirror" \
                -f "$scratch/$bench/Dockerfile.host" -t "$host_image" "$scratch"
        else
            exit 2
        fi
        ;;
    build-fixture)
        if [[ $# -ne 2 ]]; then exit 2; fi
        output=$(mkdir -p "$2" && cd "$2" && pwd)
        if [[ -e "$output/SHA256SUMS" ]]; then
            printf 'Choose an empty fixture destination: %s\n' "$output" >&2
            exit 2
        fi
        if [[ ! "$(docker info --format '{{.Architecture}}')" =~ ^(x86_64|amd64)$ ]]; then
            printf 'Build the i386 fixture on an x86-64 Docker engine.\n' >&2
            exit 2
        fi
        docker build --platform linux/386 -f "$bench/Dockerfile.guest" -t "$fixture_image" "$bench"
        container=$(docker create --platform linux/386 "$fixture_image")
        trap 'docker rm "$container" >/dev/null' EXIT
        docker cp "$container:/bundle/." "$output/"
        (cd "$output" && sha256sum -c SHA256SUMS)
        ;;
    fetch-fixture)
        if [[ $# -ne 4 ]]; then exit 2; fi
        output=$(mkdir -p "$4" && cd "$4" && pwd)
        if [[ -e "$output/SHA256SUMS" ]]; then
            printf 'Choose an empty fixture destination: %s\n' "$output" >&2
            exit 2
        fi
        scp "${ssh_options[@]}" "$2:$3/rootfs.tar.gz" "$2:$3/rootfs.cpio.gz" \
            "$2:$3/kernel" "$2:$3/SHA256SUMS" "$output/"
        if command -v sha256sum >/dev/null; then
            (cd "$output" && sha256sum -c SHA256SUMS)
        else
            (cd "$output" && shasum -a 256 -c SHA256SUMS)
        fi
        ;;
    run-linux)
        if [[ $# -lt 4 ]]; then exit 2; fi
        bundle=$(cd "$2" && pwd)
        output=$(mkdir -p "$3" && cd "$3" && pwd)
        cpu=$4
        shift 4
        docker run --rm --network none --cpuset-cpus "$cpu" \
            --mount "type=bind,src=$bundle,dst=/bundle,readonly" \
            --mount "type=bind,src=$output,dst=/results" \
            "$host_image" --bundle /bundle --state /tmp/bench --output /results \
            "$@"
        ;;
    *)
        printf '%s\n' 'deploy.sh build-host [ssh-host]' 'deploy.sh build-fixture OUTPUT' \
            'deploy.sh fetch-fixture SSH_HOST REMOTE_BUNDLE OUTPUT' \
            'deploy.sh run-linux BUNDLE OUTPUT CPU --machine NAME --backend MODE [...]' >&2
        exit 2
        ;;
esac
