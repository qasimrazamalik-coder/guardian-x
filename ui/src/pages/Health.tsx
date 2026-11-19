import { Cpu, LineChart, ListOrdered } from 'lucide-react'
import { useEffect, useState } from 'react'
import { TimeChart } from '../components/Charts'
import { Card, Loading, SkelCard, Stat } from '../components/ui'
import { call } from '../lib/bridge'
import { fmt, levelTone, monthYear, shortDate } from '../lib/format'
import type { Dashboard, History } from '../lib/types'

export function Health() {
  const [h, setH] = useState<History | null>(null)
  const [d, setD] = useState<Dashboard | null>(null)
  useEffect(() => { call<History>('history').then(setH); call<Dashboard>('dashboard').then(setD).catch(() => {}) }, [])
  if (!h) return (
    <div className="grid">
      <Loading what="health history" />
      <div className="grid stat-row">{Array.from({ length: 5 }, (_, i) => <SkelCard key={i} lines={2} />)}</div>
      <SkelCard chart={300} lines={1} />
    </div>
  )
  const pts = h.daily.map((x) => ({ t: new Date(x.day + 'T12:00').getTime(), v: x.health }))
  const from = pts.length ? Math.min(pts[0].t - 864e5, Date.now() - 14 * 864e5) : Date.now() - 14 * 864e5
  const first = h.daily[0]
  const last = h.daily[h.daily.length - 1]
  const f = h.forecast
  const bat = d?.battery
  // Project the trend to the 80% date, but never stretch the chart past a year ahead.
  const at80 = f?.healthAt80On ? new Date(f.healthAt80On + 'T12:00').getTime() : null
  const projEnd = at80 && last ? Math.min(at80, Date.now() + 365 * 864e5) : null
  const projection = projEnd && last && f?.lossPerMonth
    ? [{ t: pts[pts.length - 1].t, v: last.health }, { t: projEnd, v: Math.max(80, last.health - (f.lossPerMonth * (projEnd - pts[pts.length - 1].t)) / (30.44 * 864e5)) }]
    : undefined
  const to = projection ? projection[1].t + 10 * 864e5 : Date.now() + 864e5
  const min = Math.floor(Math.min(78, ...pts.map((p) => p.v - 2)))
  return (
    <div className="grid">
      <div className="grid stat-row">
        <Card as="div"><Stat label="First reading" value={first ? shortDate(first.day) : '—'} sub={first ? `${fmt(first.health)}% health then` : ''} /></Card>
        <Card as="div"><Stat label="Now (precise)" value={bat?.healthPrecise != null ? fmt(bat.healthPrecise, 2) : last ? fmt(last.health) : '—'} unit="%"
          sub={bat?.healthLow != null && bat.healthHigh != null ? `Range ${fmt(bat.healthLow, 2)}–${fmt(bat.healthHigh, 2)}% over 14 days` : last ? `${last.fullWh.toFixed(1)} Wh, ${last.cycles} cycles` : ''} /></Card>
        <Card as="div"><Stat label="Trend" value={h.trendPerMonth != null ? `−${fmt(h.trendPerMonth)}` : '—'} unit="% / month" color={h.trendPerMonth && h.trendPerMonth > 1.5 ? 'var(--warn)' : undefined} sub="Least-squares slope of all points" /></Card>
        <Card as="div"><Stat label="Reaches 80%" value={f?.healthAt80On ? monthYear(f.healthAt80On) : '—'} color="var(--s-proj)" sub={f?.monthsTo80 != null ? `In about ${fmt(f.monthsTo80)} months at this trend` : 'Needs a downward trend'} /></Card>
        <Card as="div"><Stat label="Biggest drop" value={h.biggestDrop ? `−${fmt(h.biggestDrop.lost)}` : '—'} unit="%" color={h.biggestDrop ? 'var(--bad)' : undefined} sub={h.biggestDrop ? `${shortDate(h.biggestDrop.from)} to ${shortDate(h.biggestDrop.to)}` : 'Within any 14 days'} /></Card>
      </div>
      <Card hero title="Health over time" icon={<LineChart size={15} aria-hidden />}
        meta={<div className="legend"><span><i style={{ background: 'var(--s-charger)' }} />Measured</span>{projection && <span><i className="dash" style={{ borderColor: 'var(--s-proj)' }} />Projected</span>}<span><i className="dash" style={{ borderColor: 'var(--bad)' }} />80% replacement point</span></div>}>
        <TimeChart points={pts} from={from} to={to} min={min} max={100} maxGap={16 * 864e5} height={300} dots tickMode="days"
          projection={projection} projectionLabel={projection && at80 ? `80% in ${monthYear(f!.healthAt80On!)}` : undefined} threshold={80} thresholdLabel="80% replacement point"
          ariaLabel={`Battery health from ${first ? shortDate(first.day) : ''} to now${projection ? ', with the projected trend' : ''}`}
          label={(p) => `${new Date(p.t).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}  ${p.v.toFixed(1)}%`} />
        <p className="small muted" style={{ marginTop: 10 }}>Older points are Windows' own weekly battery records; recent ones are daily. {projection ? 'The dashed line projects your measured trend.' : ''} Hover for exact values.
          {bat && (bat.lastCalibrated ? ` Last full calibration ${shortDate(bat.lastCalibrated)}.` : ' No full calibration seen yet.')}</p>
      </Card>
      <div className="grid split-2">
        <Card title="Recent days" icon={<ListOrdered size={15} aria-hidden />}>
          {h.days.length === 0 ? <p className="small muted">No full days recorded yet.</p> : (
            <div className="table-wrap">
              <table className="table" style={{ minWidth: 520 }}>
                <thead><tr><th>Day</th><th className="r">Health</th><th className="r">Cycles</th><th>Stress</th><th className="r">Hours full</th><th className="r">Lowest</th></tr></thead>
                <tbody>
                  {h.days.map((x) => (
                    <tr key={x.day}>
                      <td style={{ whiteSpace: 'nowrap' }}>{new Date(x.day + 'T12:00').toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}</td>
                      <td className="num r">{fmt(x.health)}%</td><td className="num r">{x.cycles}</td>
                      <td><span className={`tag ${levelTone(x.level)}`}>{x.stress} {x.level.toLowerCase()}</span></td>
                      <td className="num r">{fmt(x.hoursFull)} h</td><td className="num r">{Math.round(x.lowest)}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
        <Card title="Woke the NVIDIA GPU on battery" icon={<Cpu size={15} aria-hidden />} meta={<span className="card-meta">7 days</span>}>
          {h.gpuWeek.length === 0 ? <p className="small muted">None so far.</p> : h.gpuWeek.map((g) => (
            <div key={g.app} className="row list-row">
              <span className="ellipsis">{g.app}</span><span className="num muted">{g.times} min</span>
            </div>
          ))}
        </Card>
      </div>
    </div>
  )
}
