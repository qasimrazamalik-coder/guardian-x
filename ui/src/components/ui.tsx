import { animate, motion, useMotionValue, useTransform } from 'framer-motion'
import { ReactNode, useEffect, useState } from 'react'

export function Card({ children, className = '', glow, style, delay = 0 }: { children: ReactNode; className?: string; glow?: boolean; style?: React.CSSProperties; delay?: number }) {
  return (
    <motion.div
      className={`card ${glow ? 'glow' : ''} ${className}`}
      style={style}
      initial={{ opacity: 0, y: 14, scale: 0.99 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.5, delay, ease: [0.2, 0.8, 0.2, 1] }}
    >
      {children}
    </motion.div>
  )
}

export const Eyebrow = ({ children, dot }: { children: ReactNode; dot?: string }) => (
  <div className="eyebrow">{dot && <span className="dot" style={{ background: dot, boxShadow: `0 0 10px ${dot}` }} />}{children}</div>
)

/** Number that glides to its new value instead of jumping. */
export function Counter({ value, decimals = 0, suffix = '' }: { value: number | null | undefined; decimals?: number; suffix?: string }) {
  const mv = useMotionValue(0)
  const text = useTransform(mv, (v) => v.toFixed(decimals) + suffix)
  useEffect(() => {
    if (value == null) return
    const c = animate(mv, value, { duration: 1.1, ease: [0.2, 0.8, 0.2, 1] })
    return c.stop
  }, [value])
  if (value == null) return <span>—</span>
  return <motion.span>{text}</motion.span>
}

export function Toggle({ on, onChange, label, hint }: { on: boolean; onChange: (v: boolean) => void; label: ReactNode; hint?: ReactNode }) {
  return (
    <div className={`toggle ${on ? 'on' : ''}`} onClick={() => onChange(!on)} role="switch" aria-checked={on} tabIndex={0}
      onKeyDown={(e) => (e.key === ' ' || e.key === 'Enter') && onChange(!on)}>
      <div>
        <div>{label}</div>
        {hint && <div className="body" style={{ fontSize: 12.5, marginTop: 2 }}>{hint}</div>}
      </div>
      <div className="track"><div className="knob" /></div>
    </div>
  )
}

export function Seg<T extends string>({ value, options, onChange, layoutId }: { value: T; options: { value: T; label: string }[]; onChange: (v: T) => void; layoutId: string }) {
  return (
    <div className="seg">
      {options.map((o) => (
        <button key={o.value} className={o.value === value ? 'on' : ''} onClick={() => onChange(o.value)}>
          {o.value === value && <motion.span layoutId={layoutId} className="seg-pill" transition={{ type: 'spring', stiffness: 500, damping: 38 }} />}
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Stat({ label, value, unit, color }: { label: string; value: ReactNode; unit?: string; color?: string }) {
  return (
    <div>
      <div className="eyebrow" style={{ marginBottom: 6 }}>{label}</div>
      <div className="num" style={{ fontSize: 20, color: color ?? 'var(--ink)' }}>{value}{unit && <span className="muted" style={{ fontSize: 12, marginLeft: 4 }}>{unit}</span>}</div>
    </div>
  )
}

export function Bar({ value, max = 100, color }: { value: number; max?: number; color?: string }) {
  return (
    <div className="bar">
      <motion.i initial={{ width: 0 }} animate={{ width: `${Math.min(100, (value / Math.max(max, 0.0001)) * 100)}%` }}
        transition={{ duration: 0.9, ease: [0.2, 0.8, 0.2, 1] }} style={color ? { background: color } : undefined} />
    </div>
  )
}

/** Re-renders on an interval, e.g. to refresh live data. */
export function useTicker(ms: number) {
  const [n, setN] = useState(0)
  useEffect(() => {
    const t = setInterval(() => setN((x) => x + 1), ms)
    return () => clearInterval(t)
  }, [ms])
  return n
}
