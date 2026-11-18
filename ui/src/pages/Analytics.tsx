import { useEffect, useState } from 'react'
import { HBars, Heatmap, Histogram, StackedDays } from '../components/Charts'
import { Card, Eyebrow, Seg } from '../components/ui'
import { call } from '../lib/bridge'
import type { Analytics as A } from '../lib/types'

export function Analytics() {
  const [a, setA] = useState<A | null>(null)
  const [which, setWhich] = useState<'ac' | 'dc'>('ac')
  useEffect(() => { call<A>('analytics').then(setA) }, [])
  if (!a) return null
  const total = a.histogram.reduce((x, y) => x + y, 0) || 1
  const highShare = ((a.histogram[8] + a.histogram[9]) / total) * 100
  const split = a.split.map((s) => ({ day: s.day, a: s.acActive + s.acStandby, b: s.dcActive + s.dcStandby }))
  const avgAc = split.length ? split.reduce((x, s) => x + s.a, 0) / split.length : 0
  return (
    <div className="grid" style={{ gridTemplateColumns: '1fr 1fr' }}>
      <Card glow>
        <Eyebrow dot="var(--cyan)">Where your charge level lives · {a.sampleDays} days</Eyebrow>
        <Histogram values={a.histogram} labels={['0', '10', '20', '30', '40', '50', '60', '70', '80', '90']} highlightFrom={8} height={220} />
        <div className="body" style={{ marginTop: 14, fontSize: 12.5 }}>
          <b style={{ color: highShare > 50 ? 'var(--amber)' : 'var(--mint)' }}>{Math.round(highShare)}%</b> of the time above 80%. Batteries last longest spending most time between 30% and 80%.
        </div>
      </Card>
      <Card glow delay={0.05}>
        <Eyebrow dot="var(--amber)">On charger vs on battery · hours per day</Eyebrow>
        <StackedDays days={split} height={220} />
        <div className="body" style={{ marginTop: 14, fontSize: 12.5 }}>Average <b style={{ color: 'var(--cyan)' }}>{avgAc.toFixed(1)} h</b> a day switched on and plugged in. Blue: charger · amber: battery. From Windows' battery records.</div>
      </Card>
      <Card delay={0.1}>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <Eyebrow>When you use the laptop</Eyebrow>
          <div style={{ marginBottom: 12 }}>
            <Seg layoutId="heat" value={which} onChange={setWhich} options={[{ value: 'ac', label: 'On charger' }, { value: 'dc', label: 'On battery' }]} />
          </div>
        </div>
        <Heatmap grid={which === 'ac' ? a.gridAc : a.gridDc} color={which === 'ac' ? '61,224,255' : '255,181,71'} />
      </Card>
      <Card delay={0.15}>
        <Eyebrow>Estimated battery use by app · 7 days</Eyebrow>
        {a.appEnergy.length === 0 ? <div className="body">Recorded while you're on battery. Unplug for a while and this fills in.</div>
          : <HBars rows={a.appEnergy.map((e) => ({ label: e.name, value: e.wh }))} unit="Wh" decimals={1} />}
        <div className="body" style={{ marginTop: 14, fontSize: 12 }}>Estimate: measured discharge, minus a base share for screen and Wi-Fi, split by each app's CPU time.</div>
      </Card>
    </div>
  )
}
