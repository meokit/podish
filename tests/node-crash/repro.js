'use strict';
// Reproducer for the Node.js crash under Podish.
//
//   node /app/repro.js [rounds] [objects] [bufferSize]
//
// Default: 6 rounds x 3000 objects x 32784-byte Buffers.
//
// The workload keeps allocating Buffer-backed objects and drops the previous
// round, so V8 repeatedly collects and musl's allocator repeatedly releases
// and re-grows its heap. Under Podish this reliably ends in a guest SIGSEGV
// (container exit 139) or a hang; on native Linux the same script completes.

const rounds = parseInt(process.argv[2] || '6', 10);
const objects = parseInt(process.argv[3] || '3000', 10);
const bufferSize = parseInt(process.argv[4] || '32768', 10);

console.log('[repro] rounds=' + rounds + ' objects=' + objects + ' buffer=' + bufferSize);

let kept = null;
for (let round = 0; round < rounds; round++) {
  const batch = new Array(objects);
  for (let i = 0; i < objects; i++) {
    batch[i] = { i: i, b: Buffer.alloc(bufferSize) };
  }
  kept = batch.slice(0, 100);
  console.log('[repro] round=' + round);
}

console.log('[repro] done kept=' + (kept ? kept.length : 0));
