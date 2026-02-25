#!/bin/bash
# Build FiberPod test images
#
# Workflow:
#   1. Build test binaries with CMake
#   2. Build OCI images with podman
#
# Usage: ./scripts/build-test-images.sh [options]
#
# Options:
#   --skip-cmake    Skip CMake build (if already built)
#   --rebuild       Force rebuild images (even if they exist)
#   static          Build only static test image
#   all             Build all images (default)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Check if podman is available
if ! command -v podman &> /dev/null; then
    log_error "podman is not installed"
    echo "Please install podman first:"
    echo "  macOS: brew install podman && podman machine init && podman machine start"
    exit 1
fi

# Build parameters
ARCH="386"
BUILD_DIR="build/integration-assets"
ASSETS_DIR="$BUILD_DIR/assets"
CONTAINERFILE_DIR="tests/integration"

# Image names
IMAGE_STATIC="localhost/fiberish-tests:static"

# Parse arguments
SKIP_CMAKE=false
FORCE_REBUILD=false
TARGET="all"

for arg in "$@"; do
    case $arg in
        --skip-cmake)
            SKIP_CMAKE=true
            shift
            ;;
        --rebuild)
            FORCE_REBUILD=true
            shift
            ;;
        static|all)
            TARGET="$arg"
            shift
            ;;
    esac
done

# Step 1: CMake build
run_cmake_build() {
    log_info "Building integration test assets with CMake..."

    if [ ! -d "$BUILD_DIR" ]; then
        log_info "Configuring CMake..."
        cmake -S tests/integration \
              -B "$BUILD_DIR" \
              -DFIBERISH_PROJECT_ROOT="$PROJECT_ROOT"
    fi

    log_info "Building test binaries..."
    cmake --build "$BUILD_DIR" --target integration-tests-build

    if [ ! -d "$ASSETS_DIR" ]; then
        log_error "Assets directory not found: $ASSETS_DIR"
        exit 1
    fi

    # Count built binaries
    BINARY_COUNT=$(ls -1 "$ASSETS_DIR" 2>/dev/null | wc -l | tr -d ' ')
    log_info "Built $BINARY_COUNT test binaries in $ASSETS_DIR"
}

# Step 2: Build image
build_static_image() {
    log_info "Building static tests image..."

    # Check if build is needed
    if [ "$FORCE_REBUILD" = false ] && podman image exists "$IMAGE_STATIC" 2>/dev/null; then
        log_warn "Image $IMAGE_STATIC already exists. Use --rebuild to force rebuild."
    else
        # Check assets directory
        if [ ! -d "$ASSETS_DIR" ]; then
            log_error "Assets directory not found: $ASSETS_DIR"
            log_error "Please run without --skip-cmake or run CMake first:"
            log_error "  cmake -S tests/integration -B $BUILD_DIR -DFIBERISH_PROJECT_ROOT=\$(pwd)"
            log_error "  cmake --build $BUILD_DIR --target integration-tests-build"
            exit 1
        fi

        podman build \
            --arch="$ARCH" \
            -f "$CONTAINERFILE_DIR/Containerfile.static-tests" \
            -t "$IMAGE_STATIC" \
            .

        log_info "Image $IMAGE_STATIC built successfully"
    fi

    # Export to FiberPod images directory
    export_to_fiberpod "$IMAGE_STATIC"
}

# Export image to FiberPod images directory
export_to_fiberpod() {
    local image_name="$1"
    local safe_name=$(echo "$image_name" | sed 's/[\/:]/_/g')
    local fiberpod_images_dir="$PROJECT_ROOT/.fiberpod/images"
    local export_dir="$fiberpod_images_dir/$safe_name"

    log_info "Exporting $image_name to $export_dir..."

    # Create directory
    mkdir -p "$export_dir"

    # Create temporary container and export
    local container_id=$(podman create "$image_name" /bin/true 2>/dev/null)
    if [ -z "$container_id" ]; then
        log_error "Failed to create container from $image_name"
        return 1
    fi

    # Export filesystem
    podman export "$container_id" | tar -xf - -C "$export_dir" 2>/dev/null

    # Remove temporary container
    podman rm "$container_id" >/dev/null 2>&1

    log_info "Exported to $export_dir"
}

# Verify images
verify_images() {
    log_info "Verifying images..."

    echo ""
    echo "=== Built Images ==="
    podman images | grep -E "fiberish-tests|REPOSITORY" || true

    echo ""
    echo "=== Image Architectures ==="
    for img in "$IMAGE_STATIC"; do
        if podman image exists "$img" 2>/dev/null; then
            arch=$(podman inspect "$img" --format '{{.Architecture}}' 2>/dev/null || echo "N/A")
            echo "$img: $arch"
        fi
    done
}

# Main flow
main() {
    log_info "Starting FiberPod test image build..."
    log_info "Project root: $PROJECT_ROOT"
    log_info "Target: $TARGET"

    # Step 1: CMake build
    if [ "$SKIP_CMAKE" = false ]; then
        run_cmake_build
    else
        log_warn "Skipping CMake build (--skip-cmake)"
        if [ ! -d "$ASSETS_DIR" ]; then
            log_error "Assets directory not found: $ASSETS_DIR"
            exit 1
        fi
    fi

    # Step 2: Build images
    case "$TARGET" in
        static|all)
            build_static_image
            ;;
    esac

    # Verify
    verify_images

    echo ""
    log_info "Build complete!"
    echo ""
    echo "Next steps:"
    echo "  1. Verify the image: podman run --rm $IMAGE_STATIC /tests/hello_static"
    echo "  2. Run with FiberPod: dotnet run --project FiberPod -- run $IMAGE_STATIC /tests/hello_static"
    echo "  3. Run integration tests: pytest tests/integration/ -v -m integration"
}

main
