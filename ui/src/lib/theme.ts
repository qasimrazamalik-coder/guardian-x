import { useSyncExternalStore } from 'react'

// Light / dark / system theme. The resolved theme is written to <html data-theme>, and the choice is cached in
// localStorage so the next start paints in the right colours before the host answers settingsGet.
export type ThemeChoice = 'system' | 'light' | 'dark'
const KEY = 'gx-theme'

const media = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null
let choice: ThemeChoice = read()
const subs = new Set<() => void>()

function read(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    if (v === 'light' || v === 'dark' || v === 'system') return v
  } catch { /* storage can be unavailable in WebView2 with some profiles */ }
  return 'system'
}

export const resolve = (c: ThemeChoice): 'light' | 'dark' => (c === 'system' ? (media?.matches ? 'dark' : 'light') : c)

function paint() {
  const root = document.documentElement
  root.dataset.theme = resolve(choice)
  root.dataset.themeChoice = choice
}

export function setTheme(c: unknown) {
  if (c !== 'light' && c !== 'dark' && c !== 'system') return
  choice = c
  try { localStorage.setItem(KEY, c) } catch { /* ignore */ }
  paint()
  subs.forEach((s) => s())
}

media?.addEventListener('change', () => { if (choice === 'system') { paint(); subs.forEach((s) => s()) } })
paint()

const subscribe = (cb: () => void) => { subs.add(cb); return () => { subs.delete(cb) } }
const snap = () => `${choice}|${resolve(choice)}`

/** Current choice and the theme it resolves to; re-renders on change (including the OS switching while on "system"). */
export function useTheme() {
  const s = useSyncExternalStore(subscribe, snap)
  const [c, r] = s.split('|') as [ThemeChoice, 'light' | 'dark']
  return { choice: c, resolved: r }
}
