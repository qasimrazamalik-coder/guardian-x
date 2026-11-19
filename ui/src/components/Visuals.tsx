import { animate, motion, useReducedMotion } from 'framer-motion'
import { useEffect, useRef, useState } from 'react'
import { useWidth } from './Charts'

const rad = (deg: number) => (deg * Math.PI) / 180

/**
 * The health meter: a moving-coil style dial. 240° engraved scale (zoomed to 50–100% where batteries actually live),
 * zone bands for replace / watch / good, the 14-day spread of readings as a cobalt band, and a needle.
 */
export function HealthDial({ health, low, high, maxSize = 248 }: { health: number; low?: number; high?: number; maxSize?: number }) {
  const [ref, w] = useWidth<HTMLDivElement>()
  const reduce = useReducedMotion()
  const needleRef = useRef(158)
  const S = Math.max(170, Math.min(maxSize, w || maxSize))
  const c = S / 2, r = S / 2 - 16
  const min = health < 52 ? Math.floor((health - 5) / 10) * 10 : 50, max = 100
  const start = 158, sweep = 224
  const ang = (v: number) => start + (sweep * (Math.min(max, Math.max(min, v)) - min)) / (max - min)
  const pt = (a: number, rr: number) => [c + rr * Math.cos(rad(a)), c + rr * Math.sin(rad(a))] as const
  const arc = (v0: number, v1: number, rr: number) => {
    const a0 = ang(v0), a1 = ang(v1)
    const [x0, y0] = pt(a0, rr), [x1, y1] = pt(a1, rr)
    return `M ${x0} ${y0} A ${rr} ${rr} 0 ${a1 - a0 > 180 ? 1 : 0} 1 ${x1} ${y1}`
  }
  const ticks: number[] = []
  for (let v = min; v <= max; v += 2) ticks.push(v)
  const h = Math.max(0, Math.min(100, health))
  const tone = h >= 90 ? 'var(--good)' : h >= 80 ? 'var(--warn)' : 'var(--bad)'
  const needleLen = r - 42 // ends inside the scale numbers (r - 28 ± 7) so it never crosses them
  const target = ang(h)
  const [needle, setNeedle] = useState(reduce ? target : start)
  useEffect(() => {
    if (reduce) { setNeedle(target); return }
    const ctl = animate(needleRef.current, target, { type: 'spring', stiffness: 60, damping: 13, onUpdate: (v) => { needleRef.current = v; setNeedle(v) } })
    return () => ctl.stop()
  }, [target, reduce])
  return (
    <div ref={ref} style={{ width: '100%', maxWidth: maxSize, minWidth: 170 }}>
      <svg width={S} height={S * 0.93} viewBox={`0 0 ${S} ${S * 0.93}`} role="img" aria-label={`Battery health ${h.toFixed(1)} percent${low != null && high != null ? `, readings between ${low.toFixed(1)} and ${high.toFixed(1)} percent over 14 days` : ''}`} style={{ display: 'block', margin: '0 auto' }}>
        {/* zones */}
        <path d={arc(min, 80, r)} style={{ stroke: 'var(--bad)', strokeOpacity: 0.55 }} strokeWidth={4} fill="none" />
        <path d={arc(80, 90, r)} style={{ stroke: 'var(--warn)', strokeOpacity: 0.6 }} strokeWidth={4} fill="none" />
        <path d={arc(90, 100, r)} style={{ stroke: 'var(--good)', strokeOpacity: 0.6 }} strokeWidth={4} fill="none" />
        {/* engraved graduations */}
        {ticks.map((v) => {
          const major = v % 10 === 0
          const [x0, y0] = pt(ang(v), r - 6), [x1, y1] = pt(ang(v), r - (major ? 16 : 11))
          return <line key={v} x1={x0} y1={y0} x2={x1} y2={y1} style={{ stroke: major ? 'var(--ink-2)' : 'var(--line-2)' }} strokeWidth={major ? 1.6 : 1} />
        })}
        {ticks.filter((v) => v % 10 === 0).map((v) => {
          const [x, y] = pt(ang(v), r - 28)
          return <text key={v} x={x} y={y + 4} textAnchor="middle" style={{ fontFamily: 'var(--mono)', fontSize: 11, fill: 'var(--ink-3)' }}>{v}</text>
        })}
        {/* spread of readings */}
        {low != null && high != null && high > low && (
          <path d={arc(low, high, r + 7)} style={{ stroke: 'var(--accent)' }} strokeWidth={5} strokeLinecap="round" fill="none" />
        )}
        {/* needle */}
        <g transform={`rotate(${needle} ${c} ${c})`}>
          <line x1={c - 10} y1={c} x2={c + needleLen} y2={c} style={{ stroke: tone }} strokeWidth={2.4} strokeLinecap="round" />
          <line x1={c + r - 17} y1={c} x2={c + r - 4} y2={c} style={{ stroke: tone }} strokeWidth={3} strokeLinecap="round" />
        </g>
        <circle cx={c} cy={c} r={6} style={{ fill: 'var(--panel)', stroke: 'var(--ink)' }} strokeWidth={2} />
        <text x={c} y={c + r * 0.66} textAnchor="middle" style={{ fontFamily: 'var(--mono)', fontSize: S * 0.13, fontWeight: 500, fill: 'var(--ink)', letterSpacing: '-0.03em' }}>
          {h.toFixed(h >= 100 ? 0 : low != null ? 2 : 1)}<tspan style={{ fontSize: S * 0.065, fill: 'var(--ink-2)', fontFamily: 'var(--sans)' }} dx={3}>%</tspan>
        </text>
        <text x={c} y={c + r * 0.66 + S * 0.08} textAnchor="middle" style={{ fontSize: 12, fill: 'var(--ink-2)', fontFamily: 'var(--sans)' }}>health</text>
      </svg>
    </div>
  )
}

/** A battery cell glyph with its fill level; status colour from the theme. */
export function BatteryCell({ percent, charging, onAc, width = 76, height = 140 }: { percent: number; charging: boolean; onAc: boolean; width?: number; height?: number }) {
  const reduce = useReducedMotion()
  const pad = 6, capH = 10
  const innerH = height - capH - pad * 2 - 2
  const level = Math.max(0, Math.min(100, percent))
  const top = capH + 2 + pad + innerH * (1 - level / 100)
  const color = level <= 20 ? 'var(--bad)' : level >= 95 && onAc ? 'var(--warn)' : charging ? 'var(--accent)' : 'var(--good)'
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`Charge ${Math.round(level)} percent${charging ? ', charging' : ''}`} style={{ flex: 'none', display: 'block' }}>
      <rect x={width * 0.33} y={1} width={width * 0.34} height={capH} rx={2} style={{ fill: 'var(--ink-3)' }} />
      <rect x={1} y={capH + 1} width={width - 2} height={height - capH - 2} rx={8} style={{ fill: 'var(--well)', stroke: 'var(--ink-3)' }} strokeWidth={1.5} />
      {[25, 50, 75].map((p) => <line key={p} x1={pad + 2} x2={width - pad - 2} y1={capH + 2 + pad + innerH * (1 - p / 100)} y2={capH + 2 + pad + innerH * (1 - p / 100)} style={{ stroke: 'var(--line-2)' }} strokeDasharray="2 3" />)}
      <motion.rect x={pad} width={width - pad * 2} rx={4} style={{ fill: color }}
        initial={reduce ? false : { y: height - pad, height: 0 }} animate={{ y: top, height: Math.max(0, height - pad - top) }} transition={{ duration: 0.9, ease: [0.2, 0.8, 0.2, 1] }} />
      {charging && <path d={`M ${width / 2 + 4} ${height * 0.38} l -10 14 h 8 l -4 13 l 11 -16 h -8 z`} style={{ fill: 'var(--panel)' }} />}
    </svg>
  )
}

/** A linear 0–100 scale with engraved ticks and a fill, for scores such as stress and heat load. */
export function ScaleBar({ value, color, label }: { value: number; color: string; label: string }) {
  const v = Math.max(0, Math.min(100, value))
  return (
    <div role="img" aria-label={label} style={{ position: 'relative', height: 26 }}>
      <div style={{ position: 'absolute', top: 4, left: 0, right: 0, height: 8, borderRadius: 3, background: 'var(--well)', boxShadow: 'var(--well-shadow)', border: '1px solid var(--line)' }} />
      <motion.div style={{ position: 'absolute', top: 4, left: 0, height: 8, borderRadius: 3, background: color }} initial={false} animate={{ width: `${v}%` }} transition={{ duration: 0.7 }} />
      {[0, 25, 50, 75, 100].map((t) => (
        <div key={t} style={{ position: 'absolute', top: 14, left: `${t}%`, width: 1, height: 5, background: 'var(--line-2)' }} />
      ))}
      <div className="num xs faint" style={{ position: 'absolute', top: 14, left: 0, right: 0, display: 'flex', justifyContent: 'space-between', paddingTop: 1, pointerEvents: 'none', fontSize: 10 }}>
        <span style={{ marginTop: 3 }}>0</span><span style={{ marginTop: 3 }}>100</span>
      </div>
    </div>
  )
}

export function Logo({ size = 32 }: { size?: number }) {
  return (
    <svg className="brand-mark" width={size} height={size} viewBox="0 0 64 64" aria-hidden>
      <rect x="4" y="4" width="56" height="56" rx="12" style={{ fill: 'var(--accent)' }} />
      <rect x="22" y="15" width="20" height="36" rx="4" fill="none" style={{ stroke: 'var(--accent-ink)' }} strokeWidth="3.4" />
      <rect x="27" y="10.5" width="10" height="4.5" rx="1.5" style={{ fill: 'var(--accent-ink)' }} />
      <rect x="26.5" y="32" width="11" height="14.5" rx="1.5" style={{ fill: 'var(--accent-ink)' }} />
    </svg>
  )
}
