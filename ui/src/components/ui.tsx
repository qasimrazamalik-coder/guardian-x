import { animate, motion, useMotionValue, useReducedMotion, useTransform } from 'framer-motion'
import { ReactNode, useEffect, useState } from 'react'

/** A faceplate panel. `hero` gives it the cobalt top rule used for the one most important panel on a page. */
export function Card({ children, className = '', hero, style, title, icon, meta, mark, as = 'section' }: {
  children: ReactNode; className?: string; hero?: boolean; style?: React.CSSProperties
  title?: ReactNode; icon?: ReactNode; meta?: ReactNode; mark?: string; as?: 'section' | 'div'
}) {
  const Tag = as
  return (
    <Tag className={`card ${hero ? 'hero' : ''} ${className}`} style={style}>
      {title != null && (
        <div className="card-head">
          <h3 className="card-title">{mark && <span className="mark" style={{ background: mark }} aria-hidden />}{icon}{title}</h3>
          {meta != null && <div className="row wrap" style={{ gap: 8 }}>{meta}</div>}
        </div>
      )}
      {children}
    </Tag>
  )
}

/** Stand-alone panel heading (sentence case, no tracking). */
export const Eyebrow = ({ children, dot }: { children: ReactNode; dot?: string }) => (
  <h3 className="card-title">{dot && <span className="mark" style={{ background: dot }} aria-hidden />}{children}</h3>
)

/** Number that glides to its new value instead of jumping (instant with reduced motion). */
export function Counter({ value, decimals = 0, suffix = '' }: { value: number | null | undefined; decimals?: number; suffix?: string }) {
  const reduce = useReducedMotion()
  const mv = useMotionValue(value ?? 0)
  const text = useTransform(mv, (v) => v.toFixed(decimals) + suffix)
  useEffect(() => {
    if (value == null) return
    if (reduce) { mv.set(value); return }
    const c = animate(mv, value, { duration: 0.9, ease: [0.2, 0.8, 0.2, 1] })
    return c.stop
  }, [value, reduce])
  if (value == null) return <span>—</span>
  return <motion.span>{text}</motion.span>
}

export function Toggle({ on, onChange, label, hint, disabled }: { on: boolean; onChange: (v: boolean) => void; label: ReactNode; hint?: ReactNode; disabled?: boolean }) {
  return (
    <button type="button" className="toggle" role="switch" aria-checked={on} disabled={disabled} onClick={() => onChange(!on)}>
      <span className="t-text">
        <span className="t-label" style={{ display: 'block' }}>{label}</span>
        {hint && <span className="t-hint" style={{ display: 'block' }}>{hint}</span>}
      </span>
      <span className="track" aria-hidden><span className="knob" /></span>
    </button>
  )
}

export function Seg<T extends string>({ value, options, onChange, layoutId, label }: { value: T; options: { value: T; label: ReactNode }[]; onChange: (v: T) => void; layoutId: string; label?: string }) {
  return (
    <div className="seg" role="group" aria-label={label}>
      {options.map((o) => (
        <button type="button" key={o.value} aria-pressed={o.value === value} onClick={() => onChange(o.value)}>
          {o.value === value && <motion.span layoutId={layoutId} className="seg-pill" transition={{ type: 'spring', stiffness: 520, damping: 40 }} />}
          <span>{o.label}</span>
        </button>
      ))}
    </div>
  )
}

export function Stat({ label, value, unit, color, sub, title }: { label: ReactNode; value: ReactNode; unit?: string; color?: string; sub?: ReactNode; title?: string }) {
  return (
    <div className="stat" title={title}>
      <div className="k">{label}</div>
      <div className="v" style={{ color: color ?? 'var(--ink)' }}>{value}{unit && <span className="unit">{unit}</span>}</div>
      {sub != null && <div className="s">{sub}</div>}
    </div>
  )
}

export function Bar({ value, max = 100, color, label }: { value: number; max?: number; color?: string; label?: string }) {
  const pct = Math.min(100, Math.max(0, (value / Math.max(max, 0.0001)) * 100))
  return (
    <div className="bar" role={label ? 'img' : undefined} aria-label={label}>
      <motion.i initial={false} animate={{ width: `${pct}%` }} transition={{ duration: 0.6, ease: [0.2, 0.8, 0.2, 1] }} style={color ? { background: color } : undefined} />
    </div>
  )
}

export function Skel({ h = 14, w = '100%', r }: { h?: number | string; w?: number | string; r?: number }) {
  return <div className="skel" style={{ height: h, width: w, borderRadius: r }} aria-hidden />
}

/** Placeholder panel with the same footprint as the real one, so nothing jumps when data arrives. */
export function SkelCard({ lines = 3, chart = 0, className = '', style }: { lines?: number; chart?: number; className?: string; style?: React.CSSProperties }) {
  return (
    <div className={`card skel-card ${className}`} style={style} aria-hidden>
      <Skel h={14} w="38%" />
      {chart > 0 && <Skel h={chart} r={6} />}
      {Array.from({ length: lines }, (_, i) => <Skel key={i} h={12} w={`${90 - i * 14}%`} />)}
    </div>
  )
}

/** Polite live region announcing that a page is loading. */
export const Loading = ({ what }: { what: string }) => <div role="status" className="sr-only">Loading {what}…</div>

/** Re-renders on an interval, e.g. to refresh live data. */
export function useTicker(ms: number) {
  const [n, setN] = useState(0)
  useEffect(() => {
    const t = setInterval(() => setN((x) => x + 1), ms)
    return () => clearInterval(t)
  }, [ms])
  return n
}
