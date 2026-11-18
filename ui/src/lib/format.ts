export const fmt = (n: number | null | undefined, d = 1) => (n == null || Number.isNaN(n) ? '—' : n.toFixed(d))

export function duration(hours: number | null | undefined): string {
  if (hours == null) return '—'
  const m = Math.round(hours * 60)
  return m >= 60 ? `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min` : `${Math.max(1, m)} min`
}

export const minutes = (m: number) => (m >= 60 ? `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min` : `${m} min`)

export const shortDate = (iso: string) => new Date(iso.length === 10 ? iso + 'T12:00:00' : iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })

export const time = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

export const healthColor = (h: number) => (h >= 90 ? 'var(--mint)' : h >= 80 ? 'var(--amber)' : 'var(--red)')

export const levelColor = (l: string) =>
  l === 'Alert' || l === 'High' ? 'var(--red)' : l === 'Warn' || l === 'Medium' ? 'var(--amber)' : l === 'Info' ? 'var(--cyan)' : 'var(--mint)'
