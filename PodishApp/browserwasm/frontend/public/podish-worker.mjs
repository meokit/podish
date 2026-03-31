import {
  attachQueue,
} from './sab-ring.mjs';
import {
  attachInterruptController,
  IRQ_INPUT_READY,
  IRQ_LOG_READY,
  IRQ_OUTPUT_DRAINED,
  IRQ_OUTPUT_READY,
  IRQ_TIMER,
  IRQ_TIMER_CONTROL,
} from './interrupt-controller.mjs';
import { dotnet } from './_framework/dotnet.js';

let browserExportsPromise = null;
let runtimeInstance = null;
let sidecarQueues = null;
let interruptController = null;
let timerControl = null;

function attachTimerControl(buffer) {
  const i32 = new Int32Array(buffer, 0, 3);
  return {
    arm(delayMs) {
      Atomics.store(i32, 1, delayMs | 0);
      Atomics.store(i32, 0, 1);
    },
    cancel() {
      Atomics.store(i32, 0, 2);
    },
    buffer,
  };
}

function writePacketIntoMemory(packet, ptr, maxBytes = 0) {
  if (!runtimeInstance || !packet)
    return 0;

  if (!packet)
    return 0;

  const payload = packet.payload ?? new Uint8Array(0);
  const payloadLength = maxBytes > 0 ? Math.min(payload.length, maxBytes) : payload.length;
  const totalLength = 8 + payloadLength;
  const base = ptr >>> 0;
  const view = new DataView(runtimeInstance.Module.HEAPU8.buffer, base, totalLength);
  view.setUint32(0, totalLength, true);
  view.setUint32(4, packet.eventType >>> 0, true);
  if (payloadLength > 0)
    runtimeInstance.Module.HEAPU8.set(payload.subarray(0, payloadLength), base + 8);
  return totalLength;
}

export function signalInterrupt(bits) {
  if (interruptController)
    interruptController.signal(bits);
}

export function requestTimer(delayMs) {
  if (!timerControl || !interruptController)
    return;
  timerControl.arm(delayMs);
  interruptController.signal(IRQ_TIMER_CONTROL);
}

export function cancelTimer() {
  if (!timerControl || !interruptController)
    return;
  timerControl.cancel();
  interruptController.signal(IRQ_TIMER_CONTROL);
}

export function pollInterrupt(mask = 0xFFFFFFFF) {
  if (!interruptController)
    return 0;
  return interruptController.take(mask >>> 0);
}

export async function waitForInterrupt(mask = 0xFFFFFFFF, timeoutMs = -1) {
  if (!interruptController)
    return 0;
  return await interruptController.wait(mask >>> 0, timeoutMs);
}

export function waitForInterruptSync(mask = 0xFFFFFFFF, timeoutMs = -1) {
  if (!interruptController)
    return 0;

  const pending = interruptController.take(mask >>> 0);
  if (pending)
    return pending;

  const i32 = new Int32Array(interruptController.buffer, 0, 2);
  const seq = Atomics.load(i32, 0);
  Atomics.wait(i32, 0, seq, timeoutMs < 0 ? undefined : timeoutMs);
  return interruptController.take(mask >>> 0);
}

export function pollInputPacketInto(ptr, maxBytes = 65536) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  return writePacketIntoMemory(sidecarQueues.input.tryReadPacket(), ptr, maxBytes | 0);
}

export function pollOutputPacketInto(ptr, maxBytes = 65536) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  return writePacketIntoMemory(sidecarQueues.output.tryReadPacket(), ptr, maxBytes | 0);
}

export function pollLogPacketInto(ptr, maxBytes = 65536) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  return writePacketIntoMemory(sidecarQueues.log.tryReadPacket(), ptr, maxBytes | 0);
}

export function writeInputPacketFromMemory(eventType, ptr, len) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  const payload = len > 0
    ? runtimeInstance.Module.HEAPU8.subarray(ptr >>> 0, (ptr >>> 0) + (len >>> 0))
    : new Uint8Array(0);
  const written = sidecarQueues.input.tryWritePacketPartsLossy(eventType >>> 0, [payload], len >>> 0);
  if (written > 0)
    signalInterrupt(IRQ_INPUT_READY);
  return written;
}

export function writeOutputPacketFromMemory(eventType, ptr, len) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  const payload = len > 0
    ? runtimeInstance.Module.HEAPU8.subarray(ptr >>> 0, (ptr >>> 0) + (len >>> 0))
    : new Uint8Array(0);
  const written = sidecarQueues.output.tryWritePacketPartsLossy(eventType >>> 0, [payload], len >>> 0);
  if (written > 0)
    signalInterrupt(IRQ_OUTPUT_READY);
  return written;
}

export function writeLogPacketFromMemory(eventType, ptr, len, flags = 0) {
  if (!runtimeInstance || !sidecarQueues)
    return 0;
  const payload = len > 0
    ? runtimeInstance.Module.HEAPU8.subarray(ptr >>> 0, (ptr >>> 0) + (len >>> 0))
    : new Uint8Array(0);
  const written = sidecarQueues.log.tryWritePacketLeadingByteLossy(eventType >>> 0, flags, payload, len >>> 0);
  if (written > 0)
    signalInterrupt(IRQ_LOG_READY);
  return written;
}

async function getBrowserExports() {
  if (browserExportsPromise)
    return browserExportsPromise;

  browserExportsPromise = (async () => {
    const runtime = await dotnet.create();
    runtimeInstance = runtime;

    runtime.setModuleImports('podish-worker.mjs', {
      signalInterrupt,
      requestTimer,
      cancelTimer,
      pollInterrupt,
      waitForInterrupt,
      waitForInterruptSync,
      pollInputPacketInto,
      pollOutputPacketInto,
      pollLogPacketInto,
      writeInputPacketFromMemory,
      writeOutputPacketFromMemory,
      writeLogPacketFromMemory,
    });

    // Start the .NET Main (which now stays alive via Task.Delay(-1))
    void runtime.runMain();

    const exports = await runtime.getAssemblyExports('PodishApp.BrowserWasm');
    return exports.PodishApp.BrowserWasm.BrowserExports;
  })();

  return browserExportsPromise;
}

async function invoke(method, args) {
  const browserExports = await getBrowserExports();
  const fn = browserExports[method];
  if (typeof fn !== 'function')
    throw new Error(`Unknown BrowserExports method: ${method}`);
  return await fn(...args);
}

function collectTransferables(result) {
  if (result instanceof Uint8Array)
    return [result.buffer];
  if (result instanceof ArrayBuffer)
    return [result];
  return [];
}

self.onmessage = async event => {
  const message = event.data;
  if (message?.type === 'init-sab') {
    sidecarQueues = {
      input: attachQueue(message.inputBuffer, message.inputCapacity),
      output: attachQueue(message.outputBuffer, message.outputCapacity),
      log: attachQueue(message.logBuffer, message.logCapacity),
    }
    timerControl = attachTimerControl(message.timerControlBuffer)
    interruptController = attachInterruptController(message.irqBuffer)
    return
  }

  if (message?.type === 'run-session') {
    const browserExports = await getBrowserExports();
    await browserExports.RunCurrentSession();
    return;
  }

  if (message?.type !== 'invoke')
    return;

  try {
    const result = await invoke(message.method, message.args ?? []);
    self.postMessage(
      { type: 'response', id: message.id, ok: true, result },
      collectTransferables(result)
    );
  } catch (error) {
    self.postMessage({
      type: 'response',
      id: message.id,
      ok: false,
      error: error instanceof Error ? `${error.message}\n${error.stack ?? ''}` : String(error)
    });
  }
};

getBrowserExports()
  .then(() => {
    self.postMessage({ type: 'ready' });
  })
  .catch(error => {
    self.postMessage({
      type: 'ready-error',
      error: error instanceof Error ? `${error.message}\n${error.stack ?? ''}` : String(error)
    });
  });
