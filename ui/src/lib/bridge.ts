import { mock } from './mock'

// JSON-RPC over WebView2's message channel. In a normal browser (development, screenshots) a mock answers instead.
type Listener = (data: any) => void

interface WebView { postMessage(msg: unknown): void; addEventListener(type: 'message', cb: (e: { data: any }) => void): void }
const host: WebView | undefined = (window as any).chrome?.webview
export const inHost = !!host

let nextId = 1
const pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>()
const listeners = new Map<string, Set<Listener>>()

host?.addEventListener('message', (e) => {
  const msg = e.data
  if (msg && typeof msg.id === 'number' && pending.has(msg.id)) {
    const p = pending.get(msg.id)!
    pending.delete(msg.id)
    if (msg.error) p.reject(new Error(msg.error))
    else p.resolve(msg.result)
  } else if (msg && msg.event) {
    listeners.get(msg.event)?.forEach((l) => l(msg.data))
  }
})

export function call<T = any>(method: string, params?: unknown): Promise<T> {
  if (!host) return mock(method, params, emit) as Promise<T>
  const id = nextId++
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve, reject })
    host.postMessage({ id, method, params: params ?? null })
  })
}

export function on(event: string, cb: Listener): () => void {
  if (!listeners.has(event)) listeners.set(event, new Set())
  listeners.get(event)!.add(cb)
  return () => listeners.get(event)!.delete(cb)
}

function emit(event: string, data: any) {
  listeners.get(event)?.forEach((l) => l(data))
}
