import { dotnet } from './_framework/dotnet.js';

let browserExportsPromise = null;

async function getBrowserExports() {
  if (browserExportsPromise)
    return browserExportsPromise;

  browserExportsPromise = (async () => {
    const runtime = await dotnet.create();
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
