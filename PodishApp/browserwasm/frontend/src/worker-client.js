const encoder = new TextEncoder()
const decoder = new TextDecoder()

function createPodishWorkerClient() {
  const worker = new Worker('/podish-worker.mjs', { type: 'module' })
  let nextRequestId = 1
  const pending = new Map()

  const ready = new Promise((resolve, reject) => {
    const onMessage = event => {
      const message = event.data
      if (message?.type === 'ready') {
        worker.removeEventListener('message', onMessage)
        resolve()
      } else if (message?.type === 'ready-error') {
        worker.removeEventListener('message', onMessage)
        reject(new Error(message.error ?? 'Worker failed to initialize.'))
      }
    }
    worker.addEventListener('message', onMessage)
    worker.addEventListener('error', event => reject(event.error ?? new Error(event.message)))
  })

  worker.addEventListener('message', event => {
    const message = event.data
    if (message?.type !== 'response') return
    const entry = pending.get(message.id)
    if (!entry) return
    pending.delete(message.id)
    if (message.ok) entry.resolve(message.result)
    else entry.reject(new Error(message.error ?? 'Worker invocation failed.'))
  })

  function invoke(method, args = [], transfer = []) {
    return ready.then(() => new Promise((resolve, reject) => {
      const id = nextRequestId++
      pending.set(id, { resolve, reject })
      worker.postMessage({ type: 'invoke', id, method, args }, transfer)
    }))
  }

  return { ready, invoke, terminate() { worker.terminate() } }
}

const podishWorker = createPodishWorkerClient()

export async function callWorker(method, args = [], transfer = []) {
  return podishWorker.invoke(method, args, transfer)
}

export { podishWorker, encoder, decoder }
