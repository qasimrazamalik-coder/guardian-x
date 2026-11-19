import { motion, useReducedMotion } from 'framer-motion'
import { Fragment, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'

/** Width of an element, kept current with a ResizeObserver (measured before first paint). */
export function useWidth<T extends HTMLElement>(fallback = 0) {
  const ref = useRef<T>(null)
  const [w, setW] = useState(fallback)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    setW(el.getBoundingClientRect().width)
    const ro = new ResizeObserver(([e]) => setW(e.contentRect.width))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])
  return [ref, w] as const
}

export interface Pt { t: number; v: number; flag?: boolean }

const MONO = 'var(--mono)'
const AXIS = { fontSize: 11, fill: 'var(--ink-3)', fontFamily: MONO } as const

/**
 * Time series: area + line, segments coloured by a flag (e.g. on battery), an optional shaded band, a threshold,
 * a dashed projection, gaps where data is missing, and a crosshair tooltip. Sizes itself to its container.
 */
export function TimeChart({ points, from, to, min = 0, max = 100, height = 220, band, bandLabel, maxGap, unit = '%', color = 'var(--s-charger)', flagColor = 'var(--s-battery)',
  dots, tickMode = 'hours', label, projection, projectionLabel, threshold, thresholdLabel, ariaLabel }: {
  points: Pt[]; from: number; to: number; min?: number; max?: number; height?: number; band?: number; bandLabel?: string; maxGap: number
  unit?: string; color?: string; flagColor?: string; dots?: boolean; tickMode?: 'hours' | 'days'; label?: (p: Pt) => string
  projection?: Pt[]; projectionLabel?: string; threshold?: number; thresholdLabel?: string; ariaLabel: string
}) {
  const [ref, w] = useWidth<HTMLDivElement>()
  const reduce = useReducedMotion()
  const uid = useId().replace(/:/g, '')
  const [hover, setHover] = useState<Pt | null>(null)
  const yLabels = [0, 1, 2, 3, 4].map((i) => min + ((max - min) * i) / 4)
  const yText = (v: number) => `${Math.round(v * 10) / 10}${unit}`
  const L = 10 + Math.max(...yLabels.map((v) => yText(v).length)) * 6.8, R = 14, T = 12, B = 28
  const pw = Math.max(10, w - L - R), ph = height - T - B
  const X = (t: number) => L + (pw * (t - from)) / Math.max(1, to - from)
  const Y = (v: number) => T + ph * (1 - (Math.min(max, Math.max(min, v)) - min) / Math.max(0.0001, max - min))

  const segments = useMemo(() => {
    const segs: Pt[][] = []
    let cur: Pt[] = []
    points.forEach((p, i) => {
      if (i > 0 && (p.t - points[i - 1].t > maxGap || !!p.flag !== !!points[i - 1].flag)) {
        if (cur.length) segs.push(cur)
        cur = p.t - points[i - 1].t <= maxGap ? [points[i - 1]] : []
      }
      cur.push(p)
    })
    if (cur.length) segs.push(cur)
    return segs
  }, [points, maxGap])

  const ticks = useMemo(() => {
    const out: { t: number; s: string }[] = []
    if (pw < 40) return out
    if (tickMode === 'hours') {
      const hours = (to - from) / 3.6e6
      const step = [1, 2, 3, 4, 6, 8, 12, 24].find((s) => pw / (hours / s) >= 58) ?? 24
      const d = new Date(from); d.setMinutes(0, 0, 0); d.setHours(Math.ceil((d.getHours() + 0.01) / step) * step)
      for (let t = d.getTime(); t <= to; t += step * 3.6e6) out.push({ t, s: new Date(t).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) })
    } else {
      const days = (to - from) / 864e5
      const maxTicks = Math.max(2, Math.floor(pw / 74))
      const step = Math.max(1, Math.ceil(days / maxTicks)) * 864e5
      for (let t = from; t <= to; t += step) out.push({ t, s: new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) })
    }
    return out
  }, [from, to, tickMode, pw])

  // place tick labels left to right, dropping any that would collide with the previous one
  const tickLabels: { tk: { t: number; s: string }; x: number; anchor: 'start' | 'middle' | 'end' }[] = []
  let right = -Infinity
  for (const tk of ticks) {
    const x = X(tk.t)
    if (x < L - 1 || x > L + pw + 1) continue
    const tw = tk.s.length * 6.8
    const anchor = x - tw / 2 < L - 4 ? 'start' : x + tw / 2 > L + pw + R - 2 ? 'end' : 'middle'
    const x0 = anchor === 'start' ? x : anchor === 'end' ? x - tw : x - tw / 2
    if (x0 < right + 10) continue
    right = x0 + tw
    tickLabels.push({ tk, x, anchor })
  }

  const onMove = (e: React.PointerEvent) => {
    if (!points.length) return
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    const t = from + ((e.clientX - rect.left - L) / pw) * (to - from)
    let best = points[0]
    for (const p of points) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p
    setHover(best)
  }
  const draw = (delay = 0) => reduce ? {} : { initial: { pathLength: 0 }, animate: { pathLength: 1 }, transition: { duration: 0.9, delay, ease: [0.2, 0.8, 0.2, 1] as const } }
  const last = points[points.length - 1]

  return (
    <div ref={ref} className="chart" role="img" aria-label={ariaLabel} style={{ position: 'relative', height, width: '100%', overflow: 'hidden' }}
      onPointerMove={onMove} onPointerLeave={() => setHover(null)}>
      {w > 0 && (
        <svg width={w} height={height} style={{ position: 'absolute', inset: 0, display: 'block' }} aria-hidden>
          <defs>
            <linearGradient id={`a-${uid}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" style={{ stopColor: color, stopOpacity: 0.22 }} /><stop offset="1" style={{ stopColor: color, stopOpacity: 0 }} /></linearGradient>
            <linearGradient id={`b-${uid}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" style={{ stopColor: flagColor, stopOpacity: 0.22 }} /><stop offset="1" style={{ stopColor: flagColor, stopOpacity: 0 }} /></linearGradient>
          </defs>
          {band != null && (
            <g>
              <rect x={L} y={Y(max)} width={pw} height={Math.max(0, Y(band) - Y(max))} style={{ fill: 'var(--warn-soft)' }} />
              <line x1={L} x2={L + pw} y1={Y(band)} y2={Y(band)} style={{ stroke: 'var(--warn)', strokeOpacity: 0.55 }} strokeDasharray="4 4" />
              {bandLabel && pw > 160 && <text x={L + pw - 6} y={Y(band) + 14} textAnchor="end" style={{ ...AXIS, fill: 'var(--warn)' }}>{bandLabel}</text>}
            </g>
          )}
          {yLabels.map((v, i) => (
            <g key={i}>
              <line x1={L} x2={L + pw} y1={Y(v)} y2={Y(v)} style={{ stroke: 'var(--grid)' }} />
              <text x={L - 8} y={Y(v) + 4} textAnchor="end" style={AXIS}>{yText(v)}</text>
            </g>
          ))}
          <line x1={L} x2={L + pw} y1={T + ph} y2={T + ph} style={{ stroke: 'var(--line-2)' }} />
          {tickLabels.map(({ tk, x, anchor }) => (
            <g key={tk.t}>
              <line x1={x} x2={x} y1={T + ph} y2={T + ph + 4} style={{ stroke: 'var(--line-2)' }} />
              <text x={x} y={height - 8} textAnchor={anchor} style={AXIS}>{tk.s}</text>
            </g>
          ))}
          {segments.map((seg, i) => {
            if (seg.length < 2) return null
            const flag = !!seg[seg.length - 1].flag
            const c = flag ? flagColor : color
            const line = seg.map((p, j) => `${j ? 'L' : 'M'} ${X(p.t).toFixed(1)} ${Y(p.v).toFixed(1)}`).join(' ')
            const area = `${line} L ${X(seg[seg.length - 1].t)} ${Y(min)} L ${X(seg[0].t)} ${Y(min)} Z`
            return (
              <g key={i}>
                <path d={area} fill={`url(#${flag ? 'b' : 'a'}-${uid})`} />
                <motion.path d={line} style={{ stroke: c }} strokeWidth={2} fill="none" strokeLinejoin="round" strokeLinecap="round" {...draw()} />
              </g>
            )
          })}
          {threshold != null && (
            <g>
              <line x1={L} x2={L + pw} y1={Y(threshold)} y2={Y(threshold)} style={{ stroke: 'var(--bad)', strokeOpacity: 0.7 }} strokeDasharray="2 5" />
              {thresholdLabel && <text x={L + 6} y={Y(threshold) + 15} style={{ ...AXIS, fill: 'var(--bad)' }}>{thresholdLabel}</text>}
            </g>
          )}
          {projection && projection.length >= 2 && (() => {
            const e = projection[projection.length - 1]
            const x = X(e.t), y = Y(e.v)
            return (
              <g>
                <motion.path d={projection.map((p, j) => `${j ? 'L' : 'M'} ${X(p.t)} ${Y(p.v)}`).join(' ')} style={{ stroke: 'var(--s-proj)' }} strokeWidth={2} strokeDasharray="6 5" fill="none" {...draw(0.5)} />
                <circle cx={x} cy={y} r={4} style={{ fill: 'var(--s-proj)' }} />
                {projectionLabel && <text x={Math.min(x, L + pw)} y={y - 10} textAnchor="end" style={{ ...AXIS, fill: 'var(--s-proj)', fontWeight: 600 }}>{projectionLabel}</text>}
              </g>
            )
          })()}
          {dots && points.map((p) => <circle key={p.t} cx={X(p.t)} cy={Y(p.v)} r={2.4} style={{ fill: color }} />)}
          {last && (
            <g>
              <circle className="ping" cx={X(last.t)} cy={Y(last.v)} r={9} style={{ fill: last.flag ? flagColor : color }} />
              <circle cx={X(last.t)} cy={Y(last.v)} r={4} style={{ fill: last.flag ? flagColor : color, stroke: 'var(--panel)', strokeWidth: 2 }} />
            </g>
          )}
          {hover && (
            <g>
              <line x1={X(hover.t)} x2={X(hover.t)} y1={T} y2={T + ph} style={{ stroke: 'var(--ink-3)' }} strokeDasharray="2 3" />
              <circle cx={X(hover.t)} cy={Y(hover.v)} r={5} style={{ fill: 'var(--panel)', stroke: hover.flag ? flagColor : color }} strokeWidth={2} />
            </g>
          )}
          {points.length < 2 && (
            <text x={L + pw / 2} y={T + ph / 2} textAnchor="middle" style={{ fontSize: 13, fill: 'var(--ink-2)' }}>Collecting data. The line appears within a few minutes.</text>
          )}
        </svg>
      )}
      {hover && (
        <div className="tooltip" style={{ left: Math.min(Math.max(X(hover.t), 90), w - 90), top: Math.max(Y(hover.v), 40) }}>
          {label ? label(hover) : `${hover.v.toFixed(1)}${unit}`}
        </div>
      )}
    </div>
  )
}

/** Horizontal bars with labels on the left; label column shrinks with the panel. */
export function HBars({ rows, unit, color = 'var(--accent)', decimals = 1 }: { rows: { label: string; value: number }[]; unit: string; color?: string; decimals?: number }) {
  const max = Math.max(...rows.map((r) => r.value), 0.0001)
  const reduce = useReducedMotion()
  return (
    <div className="grid" style={{ gap: 10 }} role="list">
      {rows.map((r, i) => (
        <div key={r.label} role="listitem" style={{ display: 'grid', gridTemplateColumns: 'minmax(84px, 34%) minmax(40px, 1fr) auto', gap: 12, alignItems: 'center' }}>
          <div className="ellipsis" title={r.label}>{r.label}</div>
          <div className="bar" style={{ height: 8 }}>
            <motion.i style={{ background: color }} initial={reduce ? false : { width: 0 }} animate={{ width: `${(r.value / max) * 100}%` }}
              transition={{ duration: 0.7, delay: i * 0.04, ease: [0.2, 0.8, 0.2, 1] }} />
          </div>
          <div className="num muted small" style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{r.value.toFixed(decimals)} {unit}</div>
        </div>
      ))}
    </div>
  )
}

/** Vertical bars for a distribution (time spent in each 10% band), with the share printed on each bar. */
export function Histogram({ values, labels, height = 180, highlightFrom, ariaLabel }: { values: number[]; labels: string[]; height?: number; highlightFrom?: number; ariaLabel: string }) {
  const max = Math.max(...values, 0.0001)
  const total = values.reduce((a, b) => a + b, 0) || 1
  const reduce = useReducedMotion()
  return (
    <div role="img" aria-label={ariaLabel}>
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${values.length}, minmax(0, 1fr))`, gap: 'clamp(3px, 0.8cqw, 8px)', alignItems: 'end', height, borderBottom: '1px solid var(--line-2)' }}>
        {values.map((v, i) => {
          const hot = highlightFrom != null && i >= highlightFrom
          return (
            <div key={i} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4, height: '100%', justifyContent: 'flex-end', minWidth: 0 }}>
              <div className="num xs" style={{ color: hot ? 'var(--warn)' : 'var(--ink-2)' }}>{Math.round((v / total) * 100)}%</div>
              <motion.div style={{ width: '100%', borderRadius: '3px 3px 0 0', background: hot ? 'var(--warn)' : 'var(--accent)', opacity: hot ? 0.9 : 0.85 }}
                initial={reduce ? false : { height: 0 }} animate={{ height: `${Math.max(2, (v / max) * 80)}%` }} transition={{ duration: 0.7, delay: i * 0.03, ease: [0.2, 0.8, 0.2, 1] }} />
            </div>
          )
        })}
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${values.length}, minmax(0, 1fr))`, gap: 'clamp(3px, 0.8cqw, 8px)', marginTop: 6 }}>
        {labels.map((l, i) => <div key={i} className="num xs faint" style={{ textAlign: 'center' }}>{l}</div>)}
      </div>
    </div>
  )
}

/** Weekday x hour heatmap of minutes, on a perceptually even ramp (oklab mix of the series colour into the window). */
export function Heatmap({ grid, color = 'var(--s-charger)', ariaLabel }: { grid: number[][]; color?: string; ariaLabel: string }) {
  const max = Math.max(...grid.flat(), 1)
  const days = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
  const fill = (v: number) => v <= 0 ? 'var(--well)' : `color-mix(in oklab, ${color} ${Math.round(18 + 82 * (v / max))}%, var(--well))`
  return (
    <div role="img" aria-label={ariaLabel}>
      <div style={{ display: 'grid', gridTemplateColumns: '34px repeat(24, minmax(0, 1fr))', gap: 2 }}>
        <div />
        {Array.from({ length: 24 }, (_, h) => <div key={h} className="num xs faint" style={{ textAlign: 'left', whiteSpace: 'nowrap', overflow: 'visible', height: 16 }}>{h % 6 === 0 ? `${String(h).padStart(2, '0')}` : ''}</div>)}
        {grid.map((row, d) => (
          <Fragment key={d}>
            <div className="xs muted" style={{ alignSelf: 'center' }}>{days[d]}</div>
            {row.map((v, h) => (
              <div key={h} title={`${days[d]} ${h}:00, ${Math.round(v)} min`} style={{ height: 'clamp(12px, 1.6cqw, 22px)', borderRadius: 2, background: fill(v) }} />
            ))}
          </Fragment>
        ))}
      </div>
      <div className="legend" style={{ marginTop: 10, justifyContent: 'flex-end', alignItems: 'center' }}>
        <span>Fewer minutes</span>
        <span style={{ gap: 2 }}>{[0, 0.25, 0.5, 0.75, 1].map((k) => <i key={k} style={{ background: k ? `color-mix(in oklab, ${color} ${Math.round(18 + 82 * k)}%, var(--well))` : 'var(--well)', border: '1px solid var(--line)' }} />)}</span>
        <span>More</span>
      </div>
    </div>
  )
}

/** Stacked daily bars: hours on the charger vs on battery, on a 0–24 h axis. */
export function StackedDays({ days, height = 200, ariaLabel }: { days: { day: string; a: number; b: number }[]; height?: number; ariaLabel: string }) {
  const reduce = useReducedMotion()
  const anim = (h: number, i: number) => reduce ? { initial: false as const, animate: { height: `${h}%` } } : { initial: { height: 0 }, animate: { height: `${h}%` }, transition: { duration: 0.6, delay: i * 0.025 } }
  return (
    <div role="img" aria-label={ariaLabel}>
      <div style={{ display: 'grid', gridTemplateColumns: '30px minmax(0, 1fr)', gap: 6 }}>
        <div style={{ position: 'relative', height }}>
          {[0, 12, 24].map((v) => <div key={v} className="num xs faint" style={{ position: 'absolute', right: 0, top: `${(1 - v / 24) * 100}%`, transform: 'translateY(-50%)' }}>{v}h</div>)}
        </div>
        <div style={{ position: 'relative', height }}>
          {[0, 6, 12, 18, 24].map((v) => <div key={v} style={{ position: 'absolute', left: 0, right: 0, top: `${(1 - v / 24) * 100}%`, borderTop: `1px solid ${v === 0 ? 'var(--line-2)' : 'var(--grid)'}` }} />)}
          <div style={{ position: 'absolute', inset: 0, display: 'grid', gridTemplateColumns: `repeat(${days.length}, minmax(0, 1fr))`, gap: 'clamp(2px, 0.6cqw, 6px)', alignItems: 'end' }}>
            {days.map((d, i) => (
              <div key={d.day} title={`${d.day}: ${d.a.toFixed(1)} h on charger, ${d.b.toFixed(1)} h on battery`} style={{ display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', height: '100%', gap: 1 }}>
                <motion.div {...anim(Math.min(100, (d.b / 24) * 100), i)} style={{ background: 'var(--s-battery)', borderRadius: '2px 2px 0 0' }} />
                <motion.div {...anim(Math.min(100, (d.a / 24) * 100), i)} style={{ background: 'var(--s-charger)', borderRadius: d.b > 0.05 ? 0 : '2px 2px 0 0' }} />
              </div>
            ))}
          </div>
        </div>
        <div />
        <div style={{ display: 'grid', gridTemplateColumns: `repeat(${days.length}, minmax(0, 1fr))`, gap: 'clamp(2px, 0.6cqw, 6px)' }}>
          {days.map((d) => <div key={d.day} className="num xs faint" style={{ textAlign: 'center' }}>{new Date(d.day + 'T12:00').getDate()}</div>)}
        </div>
      </div>
    </div>
  )
}
