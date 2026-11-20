const assert = require('assert');
const fs = require('fs');
const resolve = require('enhanced-resolve').create.sync({
  extensions: ['.js', '.json'],
  mainFields: ['browser', 'module', 'main'],
});

async function main() {
  const entry = resolve(process.cwd(), 'fast-deep-equal');
  const input = fs.readFileSync(entry, 'utf8');
  const { code } = await require('terser').minify(input, { compress: true, mangle: true });
  assert(code && code.length < input.length);
  console.log(`fast-deep-equal: ${input.length} -> ${code.length} bytes`);
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
