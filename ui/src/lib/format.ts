export const fmt = (n: number | null | undefined, d = 1) => (n == null || Number.isNaN(n) ? '—' : n.toFixed(d))

export function duration(hours: number | null | undefined): string {
  if (hours == null) return '—'
  const m = Math.round(hours * 60)
  return m >= 60 ? `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min` : `${Math.max(1, m)} min`
}

export const minutes = (m: number) => (m >= 60 ? `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min` : `${m} min`)

export const shortDate = (iso: string) => new Date(iso.length === 10 ? iso + 'T12:00:00' : iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })

export const time = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

export const healthColor = (h: number) => (h >= 90 ? 'var(--good)' : h >= 80 ? 'var(--warn)' : 'var(--bad)')

export const levelColor = (l: string) =>
  l === 'Alert' || l === 'High' ? 'var(--bad)' : l === 'Warn' || l === 'Medium' ? 'var(--warn)' : l === 'Info' ? 'var(--accent)' : 'var(--good)'

export const monthYear = (iso: string) => new Date(iso.length === 10 ? iso + 'T12:00:00' : iso).toLocaleDateString(undefined, { month: 'long', year: 'numeric' })

/** "HH:mm" (24 h from the engine) shown in the user's locale clock format. */
export const clock = (hhmm: string | null | undefined) => {
  if (!hhmm) return '—'
  const [h, m] = hhmm.split(':').map(Number)
  const d = new Date(); d.setHours(h, m || 0, 0, 0)
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}
export const minutesOfDay = (hhmm: string) => { const [h, m] = hhmm.split(':').map(Number); return h * 60 + (m || 0) }
export const levelTone = (l: string) => (l === 'Alert' || l === 'High' || l === 'hot' ? 'bad' : l === 'Warn' || l === 'Medium' || l === 'warm' ? 'warn' : l === 'Info' ? 'accent' : 'good')
