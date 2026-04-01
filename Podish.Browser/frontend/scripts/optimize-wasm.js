import {execFileSync, execSync} from 'child_process';
import fs from 'fs';
import path from 'path';
import process from 'process';
import {brotliCompressSync, constants as zlibConstants, gzipSync} from 'zlib';
import {fileURLToPath} from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(__dirname, '../../');
const DEFAULT_FRAMEWORK_DIR = path.join(PROJECT_ROOT, 'bin/Release/net10.0/publish/wwwroot/_framework');

function parseArgs(argv) {
    const options = {
        frameworkDir: DEFAULT_FRAMEWORK_DIR,
        inputWasm: null,
        wasmOptPath: process.env.WASM_OPT ?? null,
        safetyLevel: 'safe1',
        stripDebug: true,
        stripProducers: true,
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
        if (arg === '--input-wasm') {
            options.inputWasm = argv[++index];
            continue;
        }
        if (arg === '--wasm-opt') {
            options.wasmOptPath = argv[++index];
            continue;
        }
        if (arg === '--safety-level') {
            options.safetyLevel = argv[++index];
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
  --input-wasm <path>     Process a specific wasm file in place.
  --framework-dir <path>  Override the publish _framework directory.
  --publish-dir <path>    Override the publish wwwroot directory; _framework is appended.
  --wasm-opt <path>       Explicit path to wasm-opt.
  --safety-level <safe1|safe2|safe3>
                          Pass bundle to run. Default: safe1
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

function probeWasmFeatures(wasmOpt, targetPath) {
    const output = execFileSync(wasmOpt, [targetPath, '--print-features', '-o', '/dev/null'], {
        stdio: ['ignore', 'pipe', 'inherit'],
    }).toString();

    return output
        .split(/\r?\n/)
        .map(line => line.trim())
        .filter(line => line.startsWith('--enable-'));
}

function getSafetyPasses(safetyLevel) {
    const safe1 = [
        '--vacuum',
        '--dce',
        '--remove-unused-module-elements',
        '--remove-unused-nonfunction-module-elements',
        '--remove-unused-brs',
        '--merge-blocks',
        '--rse',
        '--remove-unused-names',
    ];
    const safe2Extras = [
        '--optimize-instructions',
        '--simplify-locals',
        '--local-cse',
        '--precompute',
        '--precompute-propagate',
        '--const-hoisting',
        '--pick-load-signs',
        '--untee',
    ];
    const safe3Extras = [
        '--simplify-globals',
        '--simplify-globals-optimizing',
        '--merge-locals',
        '--coalesce-locals',
        '--coalesce-locals-learning',
        '--reorder-locals',
        '--reorder-functions',
        '--reorder-globals',
        '--once-reduction',
        '--signature-pruning',
        '--signature-refining',
        '--global-refining',
        '--local-subtyping',
    ];

    if (safetyLevel === 'safe1')
        return safe1;
    if (safetyLevel === 'safe2')
        return [...safe1, ...safe2Extras];
    if (safetyLevel === 'safe3')
        return [...safe1, ...safe2Extras, ...safe3Extras];

    console.error(`Error: Unknown safety level '${safetyLevel}'. Expected safe1, safe2, or safe3.`);
    process.exit(1);
}

function buildWasmOptArgs(options, outputPath, featureFlags) {
    const args = [
        ...featureFlags,
        ...getSafetyPasses(options.safetyLevel),
    ];
    if (options.stripDebug)
        args.push('--strip-debug');
    if (options.stripProducers)
        args.push('--strip-producers');
    args.push('-o', outputPath);
    return args;
}

const options = parseArgs(process.argv.slice(2));
const wasmOpt = findWasmOpt(options.wasmOptPath);
if (!wasmOpt) {
    console.error('Error: wasm-opt not found. Pass --wasm-opt or install Binaryen/Emscripten.');
    process.exit(1);
}

const frameworkDir = path.resolve(options.frameworkDir);
if (!options.inputWasm && !fs.existsSync(frameworkDir)) {
    console.error(`Error: Framework directory not found at ${frameworkDir}.`);
    process.exit(1);
}

const targetPath = options.inputWasm ? path.resolve(options.inputWasm) : resolveTargetWasm(frameworkDir);
if (!targetPath || !fileExists(targetPath)) {
    console.error(options.inputWasm
        ? `Error: Input wasm not found at ${path.resolve(options.inputWasm)}.`
        : `Error: No dotnet.native.*.wasm file found in ${frameworkDir}.`);
    process.exit(1);
}

const activeName = path.basename(targetPath);
const outputPath = `${targetPath}.opt`;
const oldSize = fs.statSync(targetPath).size;
const featureFlags = probeWasmFeatures(wasmOpt, targetPath);
const wasmOptArgs = buildWasmOptArgs(options, outputPath, featureFlags);

console.log(`Using wasm-opt: ${wasmOpt}`);
console.log(`Target wasm: ${activeName}`);
console.log(`Original size: ${formatMiB(oldSize)}`);
console.log(`Safety level: ${options.safetyLevel}`);
console.log(`Detected features: ${featureFlags.join(' ') || '(none)'}`);
console.log(`wasm-opt args: ${wasmOptArgs.join(' ')}`);

try {
    execFileSync(wasmOpt, [targetPath, ...wasmOptArgs], {stdio: 'inherit'});

    const newSize = fs.statSync(outputPath).size;
    const reduction = (((oldSize - newSize) / oldSize) * 100).toFixed(1);
    fs.renameSync(outputPath, targetPath);

    console.log(`Processed size: ${formatMiB(newSize)} (${reduction}% smaller)`);

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
    console.error(`Wasm post-process failed: ${error.message}`);
    process.exit(1);
}
