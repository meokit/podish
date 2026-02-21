#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="${ROOT_DIR}/build/integration-assets"

echo ">>> Configure integration-assets CMake project"
cmake -S "${ROOT_DIR}/tests/integration" -B "${BUILD_DIR}" -DFIBERISH_PROJECT_ROOT="${ROOT_DIR}"

echo ">>> Build integration test binaries (zig cc)"
cmake --build "${BUILD_DIR}" --target integration-tests-build

echo ">>> Build Fiberish.Cli"
dotnet build "${ROOT_DIR}/Fiberish.Cli/Fiberish.Cli.csproj"

echo ">>> Run integration tests (pytest + pexpect)"
FIBERISH_INTEGRATION_ASSETS_DIR="${BUILD_DIR}/assets" \
pytest -m integration "${ROOT_DIR}/tests/integration"

echo ""
echo ">>> INTEGRATION TESTS PASSED <<<"
