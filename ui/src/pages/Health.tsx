import { useEffect, useState } from 'react'
import { TimeChart } from '../components/Charts'
import { Card, Eyebrow, Stat } from '../components/ui'
import { call } from '../lib/bridge'
import { fmt, levelColor, shortDate } from '../lib/format'
import type { History } from '../lib/types'

export function Health() {
  const [h, setH] = useState<History | null>(null)
  useEffect(() => { call<History>('history').then(setH) }, [])
  if (!h) return null
  const pts = h.daily.map((d) => ({ t: new Date(d.day + 'T12:00').getTime(), v: d.health }))
  const from = pts.length ? Math.min(pts[0].t - 864e5, Date.now() - 14 * 864e5) : Date.now() - 14 * 864e5
  const min = Math.floor(Math.min(80, ...pts.map((p) => p.v - 2)))
  const first = h.daily[0]
  const last = h.daily[h.daily.length - 1]
  return (
    <div className="grid">
      <div className="grid" style={{ gridTemplateColumns: 'repeat(4, 1fr)' }}>
        <Card><Stat label="Since" value={first ? shortDate(first.day) : '—'} /><div className="body" style={{ fontSize: 12.5, marginTop: 8 }}>{first ? `${fmt(first.health)}% health then` : ''}</div></Card>
        <Card delay={0.04}><Stat label="Now" value={last ? fmt(last.health) : '—'} unit="%" /><div className="body" style={{ fontSize: 12.5, marginTop: 8 }}>{last ? `${last.fullWh.toFixed(1)} Wh · ${last.cycles} cycles` : ''}</div></Card>
        <Card delay={0.08}><Stat label="Trend" value={h.trendPerMonth != null ? `−${fmt(h.trendPerMonth)}` : '—'} unit="% / month" color={h.trendPerMonth && h.trendPerMonth > 1.5 ? 'var(--amber)' : undefined} /><div className="body" style={{ fontSize: 12.5, marginTop: 8 }}>Least-squares slope of all points</div></Card>
        <Card delay={0.12}><Stat label="Biggest drop" value={h.biggestDrop ? `−${fmt(h.biggestDrop.lost)}` : '—'} unit="%" color={h.biggestDrop ? 'var(--red)' : undefined} /><div className="body" style={{ fontSize: 12.5, marginTop: 8 }}>{h.biggestDrop ? `${shortDate(h.biggestDrop.from)} → ${shortDate(h.biggestDrop.to)}` : 'Within any 14 days'}</div></Card>
      </div>
      <Card glow delay={0.16}>
        <Eyebrow dot="var(--mint)">Health over time</Eyebrow>
        <TimeChart points={pts} from={from} to={Date.now() + 864e5} min={min} max={100} maxGap={16 * 864e5} height={300} color="#3dffb0" dots tickMode="days"
          label={(p) => `${new Date(p.t).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })} · ${p.v.toFixed(1)}%`} />
        <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>Older points are Windows' own weekly battery records; recent ones are daily. Hover for exact values.</div>
      </Card>
      <div className="grid" style={{ gridTemplateColumns: '1.6fr 1fr' }}>
        <Card delay={0.2}>
          <Eyebrow>Recent days</Eyebrow>
          <table className="table">
            <thead><tr><th>Day</th><th>Health</th><th>Cycles</th><th>Stress</th><th>Hours full</th><th>Lowest</th></tr></thead>
            <tbody>
              {h.days.map((d) => (
                <tr key={d.day}>
                  <td>{new Date(d.day + 'T12:00').toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}</td>
                  <td className="num">{fmt(d.health)}%</td><td className="num">{d.cycles}</td>
                  <td className="num" style={{ color: levelColor(d.level) }}>{d.stress} {d.level.toLowerCase()}</td>
                  <td className="num">{fmt(d.hoursFull)} h</td><td className="num">{Math.round(d.lowest)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
          {h.days.length === 0 && <div className="body">No full days recorded yet.</div>}
        </Card>
        <Card delay={0.24}>
          <Eyebrow>Woke the NVIDIA GPU on battery · 7 days</Eyebrow>
          {h.gpuWeek.length === 0 ? <div className="body">None so far.</div> : h.gpuWeek.map((g) => (
            <div key={g.app} className="row" style={{ justifyContent: 'space-between', padding: '9px 0', borderTop: '1px solid var(--line)' }}>
              <span>{g.app}</span><span className="num muted">{g.times} min</span>
            </div>
          ))}
        </Card>
      </div>
    </div>
  )
}
