import { BarChart3, CalendarDays, PlugZap, Zap } from 'lucide-react'
import { useEffect, useState } from 'react'
import { HBars, Heatmap, Histogram, StackedDays } from '../components/Charts'
import { Card, Loading, Seg, SkelCard } from '../components/ui'
import { call } from '../lib/bridge'
import type { Analytics as A } from '../lib/types'

export function Analytics() {
  const [a, setA] = useState<A | null>(null)
  const [which, setWhich] = useState<'ac' | 'dc'>('ac')
  useEffect(() => { call<A>('analytics').then(setA) }, [])
  if (!a) return (
    <div className="grid split-even">
      <Loading what="analytics" />
      {[240, 240, 200, 180].map((h, i) => <SkelCard key={i} chart={h} lines={1} />)}
    </div>
  )
  const total = a.histogram.reduce((x, y) => x + y, 0) || 1
  const highShare = ((a.histogram[8] + a.histogram[9]) / total) * 100
  const split = a.split.map((s) => ({ day: s.day, a: s.acActive + s.acStandby, b: s.dcActive + s.dcStandby }))
  const avgAc = split.length ? split.reduce((x, s) => x + s.a, 0) / split.length : 0
  const grid = which === 'ac' ? a.gridAc : a.gridDc
  return (
    <div className="grid split-even">
      <Card hero title="Where your charge level lives" icon={<BarChart3 size={15} aria-hidden />} meta={<span className="card-meta">{a.sampleDays} {a.sampleDays === 1 ? 'day' : 'days'}</span>}>
        <Histogram values={a.histogram} labels={['0', '10', '20', '30', '40', '50', '60', '70', '80', '90']} highlightFrom={8} height={210}
          ariaLabel={`Share of time in each 10 percent band of charge; ${Math.round(highShare)} percent of the time above 80`} />
        <p className="small muted" style={{ marginTop: 12 }}>
          <b className={highShare > 50 ? 'warn' : 'good'}>{Math.round(highShare)}%</b> of the time above 80% (amber bars). Batteries last longest spending most time between 30% and 80%.
          {a.sampleDays < 3 && ' Guardian X has only a little history so far, so this fills in over the next few days.'}
        </p>
      </Card>
      <Card title="On the charger vs on battery" icon={<PlugZap size={15} aria-hidden />}
        meta={<div className="legend"><span><i style={{ background: 'var(--s-charger)' }} />Charger</span><span><i style={{ background: 'var(--s-battery)' }} />Battery</span></div>}>
        {split.length === 0 ? <div className="empty" style={{ height: 210 }}>Windows' battery records haven't been read yet. They load within a day of first start.</div>
          : <StackedDays days={split} height={210} ariaLabel={`Hours a day on the charger and on battery, last ${split.length} days; average ${avgAc.toFixed(1)} hours on the charger`} />}
        <p className="small muted" style={{ marginTop: 12 }}>Average <b>{avgAc.toFixed(1)} h</b> a day switched on and plugged in, from Windows' battery records. Day of the month underneath.</p>
      </Card>
      <Card title="When you use the laptop" icon={<CalendarDays size={15} aria-hidden />}
        meta={<Seg layoutId="heat" label="Power source" value={which} onChange={setWhich} options={[{ value: 'ac', label: 'On charger' }, { value: 'dc', label: 'On battery' }]} />}>
        <Heatmap grid={grid} color={which === 'ac' ? 'var(--s-charger)' : 'var(--s-battery)'} ariaLabel={`Minutes ${which === 'ac' ? 'on the charger' : 'on battery'} by weekday and hour, last 30 days`} />
        {grid.flat().every((v) => v === 0) && <p className="small muted" style={{ marginTop: 10 }}>{which === 'ac' ? 'Nothing recorded on the charger in the last 30 days yet.' : 'No time on battery recorded in the last 30 days yet.'}</p>}
      </Card>
      <Card title="Estimated battery use by app" icon={<Zap size={15} aria-hidden />} meta={<span className="card-meta">7 days</span>}>
        {a.appEnergy.length === 0 ? <p className="small muted">Recorded while you're on battery. Unplug for a while and this fills in.</p>
          : <HBars rows={a.appEnergy.map((e) => ({ label: e.name, value: e.wh }))} unit="Wh" decimals={1} color="var(--s-battery)" />}
        <p className="small muted" style={{ marginTop: 12 }}>Estimate: measured discharge, minus a base share for screen and Wi-Fi, split by each app's CPU time.</p>
      </Card>
    </div>
  )
}
