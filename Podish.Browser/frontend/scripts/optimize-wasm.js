import {execFileSync, execSync} from 'child_process';
import fs from 'fs';
import path from 'path';
import process from 'process';
import {brotliCompressSync, constants as zlibConstants, gzipSync} from 'zlib';
import {fileURLToPath} from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(__dirname, '../../');
const DEFAULT_FRAMEWORK_DIR = path.join(PROJECT_ROOT, 'bin/Release/net10.0/publish/wwwroot/_framework');
const DEFAULT_PROFILE = 'Oz';

function parseArgs(argv) {
    const options = {
        frameworkDir: DEFAULT_FRAMEWORK_DIR,
        profile: DEFAULT_PROFILE,
        wasmOptPath: process.env.WASM_OPT ?? null,
        stripDebug: true,
        stripProducers: false,
        recompress: true,
    };

    for (let index = 0; index < argv.length; index += 1) {
        const arg = argv[index];
        if (arg === '--framework-dir') {
            options.frameworkDir = argv[++index];
            continue;
        }
        if (arg === '--publish-dir') {
            options.frameworkDir = path.join(argv[++index], '_framework');
            continue;
        }
        if (arg === '--profile') {
            options.profile = argv[++index];
            continue;
        }
        if (arg === '--wasm-opt') {
            options.wasmOptPath = argv[++index];
            continue;
        }
        if (arg === '--strip-producers') {
            options.stripProducers = true;
            continue;
        }
        if (arg === '--no-strip-debug') {
            options.stripDebug = false;
            continue;
        }
        if (arg === '--no-recompress') {
            options.recompress = false;
            continue;
        }
        if (arg === '--help' || arg === '-h') {
            printUsage(0);
        }

        console.error(`Unknown argument: ${arg}`);
        printUsage(1);
    }

    return options;
}

function printUsage(exitCode) {
    console.log(`Usage: node scripts/optimize-wasm.js [options]

Options:
  --framework-dir <path>  Override the publish _framework directory.
  --publish-dir <path>    Override the publish wwwroot directory; _framework is appended.
  --profile <O2|O3|O4|Os|Oz>
                          wasm-opt optimization profile. Default: ${DEFAULT_PROFILE}
  --wasm-opt <path>       Explicit path to wasm-opt.
  --strip-producers       Strip the producers section in addition to debug data.
  --no-strip-debug        Keep debug/names sections.
  --no-recompress         Do not regenerate .br and .gz assets.
  -h, --help              Show this help.
`);
    process.exit(exitCode);
}

function fileExists(filePath) {
    try {
        fs.accessSync(filePath, fs.constants.R_OK);
        return true;
    } catch {
        return false;
    }
}

function findWasmOpt(explicitPath) {
    const candidates = [];
    if (explicitPath)
        candidates.push(explicitPath);

    try {
        const pathResult = execSync('command -v wasm-opt', {stdio: ['ignore', 'pipe', 'ignore']}).toString().trim();
        if (pathResult)
            candidates.push(pathResult);
    } catch {
    }

    candidates.push('/opt/homebrew/Cellar/emscripten/5.0.4/libexec/binaryen/bin/wasm-opt');

    for (const packsDir of ['/usr/local/share/dotnet/packs', '/opt/homebrew/share/dotnet/packs', path.join(process.env.HOME ?? '', '.dotnet/packs')]) {
        if (!packsDir || !fs.existsSync(packsDir))
            continue;
        try {
            const match = execSync(`find "${packsDir}" -name wasm-opt -type f 2>/dev/null | head -n 1`, {
                stdio: ['ignore', 'pipe', 'ignore'],
            }).toString().trim();
            if (match)
                candidates.push(match);
        } catch {
        }
    }

    for (const candidate of candidates) {
        if (candidate && fileExists(candidate))
            return candidate;
    }

    return null;
}

function resolveTargetWasm(frameworkDir) {
    const dotnetJsPath = path.join(frameworkDir, 'dotnet.js');
    if (fileExists(dotnetJsPath)) {
        const dotnetJs = fs.readFileSync(dotnetJsPath, 'utf8');
        const match = dotnetJs.match(/"name":\s*"(dotnet\.native\.[^"]+\.wasm)"/);
        if (match)
            return path.join(frameworkDir, match[1]);
    }

    const wasmFiles = fs.readdirSync(frameworkDir)
        .filter(name => name.startsWith('dotnet.native.') && name.endsWith('.wasm'))
        .map(name => ({
            name,
            mtimeMs: fs.statSync(path.join(frameworkDir, name)).mtimeMs,
        }))
        .sort((left, right) => right.mtimeMs - left.mtimeMs);
    if (wasmFiles.length === 0)
        return null;
    return path.join(frameworkDir, wasmFiles[0].name);
}

function formatMiB(byteCount) {
    return `${(byteCount / 1024 / 1024).toFixed(2)} MiB`;
}

function compressAsset(filePath) {
    const contents = fs.readFileSync(filePath);
    const gzipPath = `${filePath}.gz`;
    const brotliPath = `${filePath}.br`;
    fs.writeFileSync(gzipPath, gzipSync(contents, {level: 9}));
    fs.writeFileSync(brotliPath, brotliCompressSync(contents, {
        params: {
            [zlibConstants.BROTLI_PARAM_QUALITY]: 11,
        },
    }));

    return {
        gzipPath,
        brotliPath,
        gzipSize: fs.statSync(gzipPath).size,
        brotliSize: fs.statSync(brotliPath).size,
    };
}

function buildWasmOptArgs(profile, options) {
    const normalizedProfile = profile.startsWith('-') ? profile : `-${profile}`;
    const args = [normalizedProfile];
    if (options.stripDebug)
        args.push('--strip-debug');
    if (options.stripProducers)
        args.push('--strip-producers');
    return args;
}

const options = parseArgs(process.argv.slice(2));
const wasmOpt = findWasmOpt(options.wasmOptPath);
if (!wasmOpt) {
    console.error('Error: wasm-opt not found. Pass --wasm-opt or install Binaryen/Emscripten/wasm-tools.');
    process.exit(1);
}

const frameworkDir = path.resolve(options.frameworkDir);
if (!fs.existsSync(frameworkDir)) {
    console.error(`Error: Framework directory not found at ${frameworkDir}.`);
    process.exit(1);
}

const targetPath = resolveTargetWasm(frameworkDir);
if (!targetPath) {
    console.error(`Error: No dotnet.native.*.wasm file found in ${frameworkDir}.`);
    process.exit(1);
}

const activeName = path.basename(targetPath);
const outputPath = `${targetPath}.opt`;
const oldSize = fs.statSync(targetPath).size;
const wasmOptArgs = buildWasmOptArgs(options.profile, options);

console.log(`Using wasm-opt: ${wasmOpt}`);
console.log(`Target wasm: ${activeName}`);
console.log(`Original size: ${formatMiB(oldSize)}`);
console.log(`wasm-opt args: ${wasmOptArgs.join(' ')}`);

try {
    execFileSync(wasmOpt, [targetPath, ...wasmOptArgs, '-o', outputPath], {stdio: 'inherit'});

    const newSize = fs.statSync(outputPath).size;
    const reduction = (((oldSize - newSize) / oldSize) * 100).toFixed(1);
    fs.renameSync(outputPath, targetPath);

    console.log(`Optimized size: ${formatMiB(newSize)} (${reduction}% smaller)`);

    if (options.recompress) {
        const {brotliSize, gzipSize} = compressAsset(targetPath);
        console.log(`Recompressed: ${path.basename(targetPath)}.br = ${formatMiB(brotliSize)}, ${path.basename(targetPath)}.gz = ${formatMiB(gzipSize)}`);
    } else {
        console.log('Skipped .br/.gz regeneration. Existing compressed assets may now be stale.');
    }
} catch (error) {
    try {
        if (fileExists(outputPath))
            fs.unlinkSync(outputPath);
    } catch {
    }
    console.error(`Optimization failed: ${error.message}`);
    process.exit(1);
}
