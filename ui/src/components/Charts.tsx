import { motion } from 'framer-motion'
import { Fragment, useEffect, useMemo, useRef, useState } from 'react'

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  const [w, setW] = useState(600)
  useEffect(() => {
    if (!ref.current) return
    const ro = new ResizeObserver(([e]) => setW(e.contentRect.width))
    ro.observe(ref.current)
    return () => ro.disconnect()
  }, [])
  return [ref, w] as const
}

export interface Pt { t: number; v: number; flag?: boolean }

/**
 * Time series with a gradient area, segments coloured by a flag (e.g. on battery), an optional shaded band,
 * gaps where data is missing, and a crosshair tooltip.
 */
export function TimeChart({ points, from, to, min = 0, max = 100, height = 220, band, bandLabel, maxGap, unit = '%', color = '#3de0ff', flagColor = '#ffb547',
  dots, tickMode = 'hours', label }: {
  points: Pt[]; from: number; to: number; min?: number; max?: number; height?: number; band?: number; bandLabel?: string; maxGap: number
  unit?: string; color?: string; flagColor?: string; dots?: boolean; tickMode?: 'hours' | 'days'; label?: (p: Pt) => string
}) {
  const [ref, w] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<Pt | null>(null)
  const L = 44, R = 10, T = 10, B = 26
  const pw = Math.max(10, w - L - R), ph = height - T - B
  const X = (t: number) => L + (pw * (t - from)) / Math.max(1, to - from)
  const Y = (v: number) => T + ph * (1 - (Math.min(max, Math.max(min, v)) - min) / Math.max(0.0001, max - min))

  const segments = useMemo(() => {
    const segs: Pt[][] = []
    let cur: Pt[] = []
    points.forEach((p, i) => {
      if (i > 0 && (p.t - points[i - 1].t > maxGap || !!p.flag !== !!points[i - 1].flag)) {
        if (cur.length) segs.push(cur)
        cur = i > 0 && p.t - points[i - 1].t <= maxGap ? [points[i - 1]] : []
      }
      cur.push(p)
    })
    if (cur.length) segs.push(cur)
    return segs
  }, [points, maxGap])

  const ticks = useMemo(() => {
    const out: { t: number; s: string }[] = []
    if (tickMode === 'hours') {
      const d = new Date(from); d.setMinutes(0, 0, 0); d.setHours(Math.ceil(d.getHours() / 6) * 6)
      for (let t = d.getTime(); t <= to; t += 6 * 3.6e6) out.push({ t, s: new Date(t).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) })
    } else {
      const days = (to - from) / 864e5
      const step = Math.max(1, Math.ceil(days / 7)) * 864e5
      for (let t = from; t <= to; t += step) out.push({ t, s: new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) })
    }
    return out
  }, [from, to, tickMode])

  const onMove = (e: React.MouseEvent) => {
    if (!points.length) return
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    const t = from + ((e.clientX - rect.left - L) / pw) * (to - from)
    let best = points[0]
    for (const p of points) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p
    setHover(best)
  }

  return (
    <div ref={ref} style={{ position: 'relative', height, width: '100%', overflow: 'hidden' }} onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
      <svg width={w} height={height} style={{ position: 'absolute', inset: 0, display: 'block' }}>
        <defs>
          <linearGradient id={`fill-${color}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor={color} stopOpacity={0.32} /><stop offset="1" stopColor={color} stopOpacity={0} /></linearGradient>
          <linearGradient id={`fill-${flagColor}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor={flagColor} stopOpacity={0.3} /><stop offset="1" stopColor={flagColor} stopOpacity={0} /></linearGradient>
        </defs>
        {band != null && (
          <g>
            <rect x={L} y={Y(max)} width={pw} height={Y(band) - Y(max)} fill="rgba(255,181,71,0.09)" />
            <line x1={L} x2={L + pw} y1={Y(band)} y2={Y(band)} stroke="rgba(255,181,71,0.35)" strokeDasharray="4 4" />
            {bandLabel && <text x={L + pw - 6} y={Y(band) - 5} textAnchor="end" fontSize={10} fill="rgba(255,181,71,0.8)" fontFamily="var(--mono)">{bandLabel}</text>}
          </g>
        )}
        {[0, 1, 2, 3, 4].map((i) => {
          const v = min + ((max - min) * i) / 4
          return (
            <g key={i}>
              <line x1={L} x2={L + pw} y1={Y(v)} y2={Y(v)} stroke="rgba(160,200,230,0.07)" />
              <text x={L - 8} y={Y(v) + 3.5} textAnchor="end" fontSize={10} fill="var(--muted)" fontFamily="var(--mono)">{Math.round(v * 10) / 10}{unit}</text>
            </g>
          )
        })}
        {ticks.map((tk) => (
          <text key={tk.t} x={X(tk.t)} y={height - 6} textAnchor="middle" fontSize={10} fill="var(--muted)" fontFamily="var(--mono)">{tk.s}</text>
        ))}
        {segments.map((seg, i) => {
          if (seg.length < 2) return null
          const c = seg[seg.length - 1].flag ? flagColor : color
          const line = seg.map((p, j) => `${j ? 'L' : 'M'} ${X(p.t)} ${Y(p.v)}`).join(' ')
          const area = `${line} L ${X(seg[seg.length - 1].t)} ${Y(min)} L ${X(seg[0].t)} ${Y(min)} Z`
          return (
            <g key={i}>
              <motion.path d={area} fill={`url(#fill-${c})`} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ duration: 0.8 }} />
              <motion.path d={line} stroke={c} strokeWidth={2.2} fill="none" strokeLinejoin="round" style={{ filter: `drop-shadow(0 0 5px ${c}88)` }}
                initial={{ pathLength: 0 }} animate={{ pathLength: 1 }} transition={{ duration: 1.2, ease: [0.2, 0.8, 0.2, 1] }} />
            </g>
          )
        })}
        {dots && points.map((p) => <circle key={p.t} cx={X(p.t)} cy={Y(p.v)} r={2.6} fill={color} />)}
        {points.length > 0 && (() => {
          const p = points[points.length - 1]
          const c = p.flag ? flagColor : color
          return (
            <g>
              <motion.circle cx={X(p.t)} cy={Y(p.v)} r={10} fill={c} style={{ transformOrigin: `${X(p.t)}px ${Y(p.v)}px` }}
                animate={{ scale: [0.6, 1.3, 0.6], opacity: [0.4, 0, 0.4] }} transition={{ duration: 2, repeat: Infinity }} />
              <circle cx={X(p.t)} cy={Y(p.v)} r={4} fill={c} />
            </g>
          )
        })()}
        {hover && (
          <g>
            <line x1={X(hover.t)} x2={X(hover.t)} y1={T} y2={T + ph} stroke="rgba(234,242,248,0.25)" />
            <circle cx={X(hover.t)} cy={Y(hover.v)} r={5} fill="#04060a" stroke={hover.flag ? flagColor : color} strokeWidth={2} />
          </g>
        )}
        {points.length < 2 && (
          <text x={L + pw / 2} y={T + ph / 2} textAnchor="middle" fontSize={13} fill="var(--muted)">Collecting data — the line appears within a few minutes.</text>
        )}
      </svg>
      {hover && (
        <div className="tooltip" style={{ left: X(hover.t), top: Y(hover.v) }}>
          {label ? label(hover) : `${hover.v.toFixed(1)}${unit}`}
        </div>
      )}
    </div>
  )
}

/** Horizontal bars with labels on the left. */
export function HBars({ rows, unit, color = 'var(--grad)', decimals = 1 }: { rows: { label: string; value: number }[]; unit: string; color?: string; decimals?: number }) {
  const max = Math.max(...rows.map((r) => r.value), 0.0001)
  return (
    <div className="grid" style={{ gap: 12 }}>
      {rows.map((r, i) => (
        <div key={r.label} style={{ display: 'grid', gridTemplateColumns: '160px 1fr 70px', gap: 14, alignItems: 'center' }}>
          <div style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.label}</div>
          <div style={{ height: 10, borderRadius: 5, background: 'rgba(255,255,255,0.05)', overflow: 'hidden' }}>
            <motion.div style={{ height: '100%', borderRadius: 5, background: color }} initial={{ width: 0 }} animate={{ width: `${(r.value / max) * 100}%` }}
              transition={{ duration: 0.9, delay: i * 0.05, ease: [0.2, 0.8, 0.2, 1] }} />
          </div>
          <div className="num muted" style={{ textAlign: 'right' }}>{r.value.toFixed(decimals)} {unit}</div>
        </div>
      ))}
    </div>
  )
}

/** Vertical bars for a distribution (e.g. time spent in each 10% band). */
export function Histogram({ values, labels, height = 180, highlightFrom }: { values: number[]; labels: string[]; height?: number; highlightFrom?: number }) {
  const max = Math.max(...values, 0.0001)
  const total = values.reduce((a, b) => a + b, 0) || 1
  return (
    <div style={{ display: 'grid', gridTemplateColumns: `repeat(${values.length}, 1fr)`, gap: 8, alignItems: 'end', height }}>
      {values.map((v, i) => {
        const hot = highlightFrom != null && i >= highlightFrom
        return (
          <div key={i} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6, height: '100%', justifyContent: 'flex-end' }}>
            <div className="num" style={{ fontSize: 10.5, color: 'var(--muted)' }}>{Math.round((v / total) * 100)}%</div>
            <motion.div style={{ width: '100%', borderRadius: 8, background: hot ? 'linear-gradient(180deg,#ffb547,#ff7a3d55)' : 'linear-gradient(180deg,#3de0ff,#3dffb033)', boxShadow: hot ? '0 0 18px rgba(255,181,71,0.35)' : '0 0 18px rgba(61,224,255,0.2)' }}
              initial={{ height: 0 }} animate={{ height: `${Math.max(2, (v / max) * 78)}%` }} transition={{ duration: 0.9, delay: i * 0.04, ease: [0.2, 0.8, 0.2, 1] }} />
            <div className="num" style={{ fontSize: 10, color: 'var(--muted)' }}>{labels[i]}</div>
          </div>
        )
      })}
    </div>
  )
}

/** Weekday x hour heatmap of minutes. */
export function Heatmap({ grid, color = '61,224,255' }: { grid: number[][]; color?: string }) {
  const max = Math.max(...grid.flat(), 1)
  const days = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '38px repeat(24, 1fr)', gap: 3 }}>
      <div />
      {Array.from({ length: 24 }, (_, h) => <div key={h} className="num" style={{ fontSize: 9, color: 'var(--faint)', textAlign: 'center' }}>{h % 3 === 0 ? h : ''}</div>)}
      {grid.map((row, d) => (
        <Fragment key={d}>
          <div className="num" style={{ fontSize: 10, color: 'var(--muted)', alignSelf: 'center' }}>{days[d]}</div>
          {row.map((v, h) => (
            <motion.div key={`${d}-${h}`} title={`${days[d]} ${h}:00 — ${Math.round(v)} min`}
              initial={{ opacity: 0, scale: 0.6 }} animate={{ opacity: 1, scale: 1 }} transition={{ delay: (d * 24 + h) * 0.002 }}
              style={{ aspectRatio: '1', borderRadius: 4, background: v > 0 ? `rgba(${color},${0.12 + 0.88 * (v / max)})` : 'rgba(255,255,255,0.03)', boxShadow: v / max > 0.6 ? `0 0 10px rgba(${color},0.45)` : undefined }} />
          ))}
        </Fragment>
      ))}
    </div>
  )
}

/** Stacked daily bars: hours on the charger vs on battery. */
export function StackedDays({ days, height = 200 }: { days: { day: string; a: number; b: number }[]; height?: number }) {
  const max = 24
  return (
    <div style={{ display: 'grid', gridTemplateColumns: `repeat(${days.length}, 1fr)`, gap: 6, alignItems: 'end', height }}>
      {days.map((d, i) => (
        <div key={d.day} title={`${d.day}: ${d.a.toFixed(1)} h on charger, ${d.b.toFixed(1)} h on battery`}
          style={{ display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', height: '100%', gap: 2 }}>
          <motion.div initial={{ height: 0 }} animate={{ height: `${(d.b / max) * 85}%` }} transition={{ duration: 0.8, delay: i * 0.03 }}
            style={{ background: 'linear-gradient(180deg,#ffb547,#ffb54755)', borderRadius: '6px 6px 2px 2px' }} />
          <motion.div initial={{ height: 0 }} animate={{ height: `${(d.a / max) * 85}%` }} transition={{ duration: 0.8, delay: i * 0.03 }}
            style={{ background: 'linear-gradient(180deg,#3de0ff,#3de0ff44)', borderRadius: '2px 2px 6px 6px' }} />
          <div className="num" style={{ fontSize: 9.5, color: 'var(--muted)', textAlign: 'center', marginTop: 4 }}>{new Date(d.day + 'T12:00').getDate()}</div>
        </div>
      ))}
    </div>
  )
}
