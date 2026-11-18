import { motion } from 'framer-motion'
import { Activity, BatteryCharging, Cpu, Moon, Plug, Thermometer, Zap } from 'lucide-react'
import { useEffect, useState } from 'react'
import { TimeChart } from '../components/Charts'
import { Card, Counter, Eyebrow, Stat } from '../components/ui'
import { BatteryCell, HealthRing, MiniGauge } from '../components/Visuals'
import { call } from '../lib/bridge'
import { duration, fmt, levelColor, minutes, time } from '../lib/format'
import type { Dashboard, DayPoint } from '../lib/types'

export function Overview({ d, go }: { d: Dashboard; go: (p: string) => void }) {
  const [day, setDay] = useState<DayPoint[]>([])
  useEffect(() => { call<DayPoint[]>('day').then(setDay) }, [d])
  const b = d.battery
  if (!b) return <Card><div className="big">No battery found</div><p className="body">{d.error}</p></Card>
  const now = Date.now()
  const flow = b.onAc ? (b.charging ? `Charging${b.hoursLeft ? ` · full in ${duration(b.hoursLeft)}` : ''}` : b.percent >= 99 ? 'On the charger · full' : 'On the charger · holding')
    : `On battery${b.hoursLeft ? ` · ${duration(b.hoursLeft)} left` : ''}`
  const typical = 4
  const wearMax = Math.max(b.wearPer100 ?? 0, typical) * 1.15

  return (
    <div className="grid" style={{ gridTemplateColumns: '1.05fr 1fr', alignItems: 'start' }}>
      {/* Health hero */}
      <Card glow style={{ gridRow: 'span 2' }}>
        <Eyebrow dot="var(--mint)">Battery health</Eyebrow>
        <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: 26, alignItems: 'center' }}>
          <HealthRing health={b.health} size={250} />
          <div className="grid" style={{ gap: 18 }}>
            <div>
              <div className="body">Holds</div>
              <div style={{ fontSize: 26, fontWeight: 600 }}><Counter value={b.fullWh} decimals={1} /> <span className="muted" style={{ fontSize: 15 }}>of {b.designWh.toFixed(1)} Wh</span></div>
            </div>
            <div className="row" style={{ gap: 28 }}>
              <Stat label="Cycles" value={b.cycles} />
              <Stat label="Worn" value={fmt(b.wear)} unit="%" />
              <Stat label="Trend" value={b.wearPerMonth != null ? `−${fmt(b.wearPerMonth)}` : '—'} unit="%/mo" color={b.wearPerMonth && b.wearPerMonth > 1.5 ? 'var(--amber)' : undefined} />
            </div>
          </div>
        </div>
        <div style={{ marginTop: 20, padding: 18, borderRadius: 14, background: 'rgba(0,0,0,0.25)', border: '1px solid var(--line)' }}>
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <div className="eyebrow" style={{ margin: 0 }}>Wear speed</div>
            <div className="num" style={{ fontSize: 18, color: (b.wearPer100 ?? 0) > typical * 2 ? 'var(--red)' : 'var(--mint)' }}>{b.wearPer100 != null ? `${fmt(b.wearPer100)}% / 100 cycles` : 'needs 10+ cycles'}</div>
          </div>
          {b.wearPer100 != null && (
            <div style={{ position: 'relative', height: 18, marginTop: 14 }}>
              <div style={{ position: 'absolute', top: 5, left: 0, right: 0, height: 8, borderRadius: 4, background: 'rgba(255,255,255,0.05)' }} />
              <motion.div style={{ position: 'absolute', top: 5, left: 0, height: 8, borderRadius: 4, background: 'linear-gradient(90deg,#ffb547,#ff4d6a)', boxShadow: '0 0 14px rgba(255,77,106,0.5)' }}
                initial={{ width: 0 }} animate={{ width: `${(b.wearPer100 / wearMax) * 100}%` }} transition={{ duration: 1.2, ease: [0.2, 0.8, 0.2, 1] }} />
              <div style={{ position: 'absolute', top: 0, left: `${(typical / wearMax) * 100}%`, width: 3, height: 18, borderRadius: 2, background: 'var(--ink)', boxShadow: '0 0 8px #fff' }} />
            </div>
          )}
          <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>White mark = a typical battery (about {typical}%). {(b.wearPer100 ?? 0) > typical * 2 ? 'Yours is ageing faster than its use explains.' : 'You are in a healthy range.'}</div>
        </div>
      </Card>

      {/* Live cell */}
      <Card delay={0.05}>
        <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: 26, alignItems: 'center' }}>
          <BatteryCell percent={b.percent} charging={b.charging} onAc={b.onAc} width={112} height={200} />
          <div className="grid" style={{ gap: 16 }}>
            <div>
              <Eyebrow dot={b.onAc ? 'var(--cyan)' : 'var(--amber)'}>Right now</Eyebrow>
              <div style={{ fontSize: 20, fontWeight: 600 }}>{flow}</div>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
              <Stat label="Power" value={b.rateW ? `${b.rateW > 0 ? '+' : '−'}${Math.abs(b.rateW).toFixed(1)}` : '0'} unit="W" />
              <Stat label="Voltage" value={fmt(b.voltage, 2)} unit="V" />
              <Stat label="Temperature" value={b.tempC != null ? fmt(b.tempC) : 'n/a'} unit={b.tempC != null ? '°C' : ''} color={b.tempC == null ? 'var(--muted)' : undefined} />
              <Stat label="Full on charger" value={d.holdMinutes > 0 ? minutes(d.holdMinutes) : '—'} color={d.holdMinutes >= 120 ? 'var(--amber)' : undefined} />
            </div>
          </div>
        </div>
      </Card>

      <Card delay={0.1}>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <Eyebrow>Last 24 hours</Eyebrow>
          <div className="row" style={{ gap: 14, marginBottom: 12 }}>
            <span className="chip" style={{ color: 'var(--cyan)' }}>on charger</span>
            <span className="chip" style={{ color: 'var(--amber)' }}>on battery</span>
          </div>
        </div>
        <TimeChart points={day.map((p) => ({ t: new Date(p.t).getTime(), v: p.p, flag: !p.ac }))} from={now - 24 * 3.6e6} to={now}
          band={95} bandLabel="STRESS ZONE" maxGap={6 * 60000} height={200}
          label={(p) => `${new Date(p.t).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })} · ${p.v.toFixed(0)}% · ${p.flag ? 'battery' : 'charger'}`} />
      </Card>

      <div className="grid" style={{ gridColumn: '1 / -1', gridTemplateColumns: 'repeat(4, 1fr)' }}>
        <Card delay={0.15}>
          <Eyebrow><Activity size={13} /> Stress today</Eyebrow>
          <div className="row" style={{ justifyContent: 'space-between', alignItems: 'flex-end' }}>
            <div><div className="big"><Counter value={d.stress.score} /></div><div className="num" style={{ color: levelColor(d.stress.level), marginTop: 6, fontSize: 12 }}>{d.stress.level.toUpperCase()} / 100</div></div>
            <MiniGauge value={d.stress.score} color={levelColor(d.stress.level)} size={110} />
          </div>
          <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>{fmt(d.stress.hoursFull)} h at 100% on charger · {fmt(d.stress.hoursLow)} h under 10%{d.stress.tempKnown ? ` · ${fmt(d.stress.hoursHot)} h hot` : ''}</div>
        </Card>
        <Card delay={0.18}>
          <Eyebrow><Plug size={13} /> Charging habit</Eyebrow>
          <div className="big"><Counter value={d.chargerHours ?? null} decimals={1} /><span className="muted" style={{ fontSize: 16 }}> h/day</span></div>
          <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>Switched on and plugged in, average of the last week (Windows records).</div>
        </Card>
        <Card delay={0.21}>
          <Eyebrow><Moon size={13} /> Last sleep</Eyebrow>
          {d.lastSleep ? (
            <>
              <div className="big" style={{ color: d.lastSleep.unusual ? 'var(--red)' : undefined }}>−{fmt(d.lastSleep.lost)}%</div>
              <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>{time(d.lastSleep.start)} → {time(d.lastSleep.end)}, {fmt(d.lastSleep.perHour, 2)}%/h. {d.lastSleep.unusual ? 'More than usual.' : 'Normal for this laptop.'}</div>
            </>
          ) : <div className="body">No sleep of 30+ minutes recorded yet.</div>}
        </Card>
        <Card delay={0.24}>
          <Eyebrow><Cpu size={13} /> {d.gpu.name?.replace('NVIDIA GeForce ', '') ?? 'Discrete GPU'}</Eyebrow>
          <div className="big" style={{ fontSize: 30, color: !b.onAc && d.gpu.holders.length ? 'var(--amber)' : undefined }}>
            {!d.gpu.available ? '—' : b.onAc ? 'Watching' : d.gpu.holders.length ? 'Awake' : 'Asleep'}
          </div>
          <div className="body" style={{ marginTop: 10, fontSize: 12.5 }}>
            {b.onAc ? 'Checks which apps keep the NVIDIA GPU awake when you unplug.' : d.gpu.holders.length ? `Kept awake by ${d.gpu.holders.slice(0, 3).map((h) => h.processName).join(', ')}.` : 'No app is keeping it awake.'}
          </div>
        </Card>
      </div>

      <Card delay={0.28} style={{ gridColumn: '1 / -1' }}>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <Eyebrow><Zap size={13} /> What to do</Eyebrow>
          <div className="row" style={{ gap: 10, marginBottom: 12 }}>
            <button className="chip" style={{ color: 'var(--mint)' }} onClick={() => go('charging')}><BatteryCharging size={12} /> {d.mode.name} mode · {d.mode.resumeAt}–{d.mode.chargeLimit}%</button>
            <button className="chip" style={{ color: d.plug.configured ? 'var(--cyan)' : 'var(--muted)' }} onClick={() => go('charging')}><Thermometer size={12} /> {d.plug.configured ? d.plug.status : 'No smart plug'}</button>
          </div>
        </div>
        {d.tips.map((t) => (
          <motion.div className="tip" key={t.title} initial={{ opacity: 0, x: -8 }} animate={{ opacity: 1, x: 0 }}>
            <i style={{ background: levelColor(t.level), boxShadow: `0 0 12px ${levelColor(t.level)}` }} />
            <div><h4>{t.title}</h4><div className="body">{t.body}</div></div>
          </motion.div>
        ))}
      </Card>
    </div>
  )
}
