import { execSync } from 'child_process';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(__dirname, '../../');
const PUBLISH_DIR = path.join(PROJECT_ROOT, 'bin/Release/net10.0/publish/wwwroot/_framework');

// Try to find wasm-opt in common .NET pack locations if not in PATH
function findWasmOpt() {
  try {
    return execSync('which wasm-opt').toString().trim();
  } catch {
    const packsDir = '/usr/local/share/dotnet/packs';
    if (fs.existsSync(packsDir)) {
      const matches = execSync(`find ${packsDir} -name wasm-opt -type f 2>/dev/null | head -n 1`).toString().trim();
      if (matches) return matches;
    }
    return null;
  }
}

const WASM_OPT = findWasmOpt();

if (!WASM_OPT) {
  console.error('Error: wasm-opt not found. Please install emscripten or ensure wasm-tools workload is installed.');
  process.exit(1);
}

console.log(`Using wasm-opt: ${WASM_OPT}`);

if (!fs.existsSync(PUBLISH_DIR)) {
  console.error(`Error: Publish directory not found at ${PUBLISH_DIR}. Run 'dotnet publish -c Release' first.`);
  process.exit(1);
}

const wasmFiles = fs.readdirSync(PUBLISH_DIR).filter(f => f.startsWith('dotnet.native.') && f.endsWith('.wasm'));

if (wasmFiles.length === 0) {
  console.error('Error: No dotnet.native.*.wasm files found in publish directory.');
  process.exit(1);
}

// Sort by mtime to pick the latest one if there are multiple
const targetFile = wasmFiles
  .map(f => ({ name: f, time: fs.statSync(path.join(PUBLISH_DIR, f)).mtime }))
  .sort((a, b) => b.time - a.time)[0].name;

const targetPath = path.join(PUBLISH_DIR, targetFile);
const outputPath = targetPath + '.opt';

console.log(`Optimizing ${targetFile} (Size: ${(fs.statSync(targetPath).size / 1024 / 1024).toFixed(2)} MB)...`);

try {
  // -O4 is the highest optimization level in wasm-opt
  // --strip-debug removes extra metadata
  execSync(`"${WASM_OPT}" -O4 --strip-debug "${targetPath}" -o "${outputPath}"`, { stdio: 'inherit' });
  
  const oldSize = fs.statSync(targetPath).size;
  const newSize = fs.statSync(outputPath).size;
  const reduction = ((oldSize - newSize) / oldSize * 100).toFixed(1);
  
  console.log(`Success! New size: ${(newSize / 1024 / 1024).toFixed(2)} MB (${reduction}% reduction)`);
  
  // Replace the original file
  fs.renameSync(outputPath, targetPath);
  
  // Also clean up stale compressed versions if they exist, as they are now invalid
  const brFile = targetPath + '.br';
  const gzFile = targetPath + '.gz';
  if (fs.existsSync(brFile)) fs.unlinkSync(brFile);
  if (fs.existsSync(gzFile)) fs.unlinkSync(gzFile);
  
  console.log('Original file replaced. Note: .br and .gz files removed; they should be re-generated or omitted.');
} catch (err) {
  console.error('Optimization failed:', err.message);
  process.exit(1);
}
