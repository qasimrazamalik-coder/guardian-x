import { motion } from 'framer-motion'
import { Activity, BatteryCharging, CalendarClock, Clock3, Cpu, Flame, Gamepad2, Gauge, Moon, Plug, Wand2, Zap } from 'lucide-react'
import { useEffect, useState } from 'react'
import { TimeChart } from '../components/Charts'
import { Card, Counter, Stat } from '../components/ui'
import { BatteryCell, HealthDial, ScaleBar } from '../components/Visuals'
import { call } from '../lib/bridge'
import { clock, duration, fmt, levelColor, levelTone, minutes, minutesOfDay, monthYear, shortDate, time } from '../lib/format'
import type { Dashboard, DayPoint } from '../lib/types'

const methodLabel = { energy: 'Smoothed from stored energy over the last minutes', driver: 'Driver reading, smoothed', none: 'No power reading' } as const

export function Overview({ d, go }: { d: Dashboard; go: (p: string) => void }) {
  const [day, setDay] = useState<DayPoint[] | null>(null)
  useEffect(() => { call<DayPoint[]>('day').then(setDay).catch(() => setDay([])) }, [d])
  const b = d.battery
  if (!b) return <Card title="No battery found"><p className="body">{d.error ?? 'Windows reports no battery on this PC.'}</p></Card>
  const now = Date.now()
  const flow = b.onAc ? (b.charging ? `Charging${b.hoursLeft ? `, full in ${duration(b.hoursLeft)}` : ''}` : b.percent >= 99 ? 'On the charger, full' : 'On the charger, holding')
    : `On battery${b.hoursLeft ? `, ${duration(b.hoursLeft)} left` : ''}`
  const typical = 4
  const wearMax = Math.max(b.wearPer100 ?? 0, typical) * 1.15
  const precise = b.healthPrecise ?? b.health
  const hasRange = b.healthLow != null && b.healthHigh != null
  const rangeMin = hasRange ? Math.min(b.healthLow!, precise) - 2 : 0, rangeMax = hasRange ? Math.max(b.healthHigh!, precise) + 2 : 100
  const rpos = (v: number) => `${((v - rangeMin) / (rangeMax - rangeMin)) * 100}%`
  const heat = d.heat
  const tempC = heat?.tempC ?? b.tempC
  const rate = b.rateW ?? 0

  return (
    <div className="grid ov">
      {/* Health: the one hero instrument */}
      <Card hero className="ov-health" title="Battery health" icon={<Gauge size={15} aria-hidden />}
        meta={<span className="card-meta">7-day median of full-charge capacity</span>}>
        <div className="health-body">
          <HealthDial health={precise} low={b.healthLow} high={b.healthHigh} />
          <div className="health-side">
            <div>
              <div className="label">Holds</div>
              <div className="readout" style={{ fontSize: 'var(--fs-2xl)' }}><Counter value={b.fullWh} decimals={1} /><span className="unit">of {b.designWh.toFixed(1)} Wh when new</span></div>
            </div>
            <div className="stat-grid">
              <Stat label="Cycles" value={b.cycles} />
              <Stat label="Worn" value={fmt(b.wear)} unit="%" />
              <Stat label="Trend" value={b.wearPerMonth != null ? `−${fmt(b.wearPerMonth)}` : '—'} unit="% / month" color={b.wearPerMonth && b.wearPerMonth > 1.5 ? 'var(--warn)' : undefined} />
            </div>
            {hasRange && (
              <div>
                <div className="row between wrap" style={{ gap: 6 }}>
                  <span className="label">Daily readings, last 14 days</span>
                  <span className="num small">{fmt(b.healthLow, 2)}–{fmt(b.healthHigh, 2)}%</span>
                </div>
                <div className="range-scale" role="img" aria-label={`Daily health readings ranged from ${fmt(b.healthLow, 2)} to ${fmt(b.healthHigh, 2)} percent; now ${fmt(precise, 2)}`}>
                  <div className="track" />
                  <div className="span" style={{ left: rpos(b.healthLow!), width: `calc(${rpos(b.healthHigh!)} - ${rpos(b.healthLow!)})` }} />
                  <div className="now" style={{ left: `calc(${rpos(precise)} - 1px)` }} />
                </div>
                <div className="xs faint">A spread of a point or two is normal: capacity readings wobble with temperature and how full the last charge was.</div>
              </div>
            )}
          </div>
        </div>
        <div className="well" style={{ marginTop: 18 }}>
          <div className="row between wrap" style={{ gap: 8 }}>
            <span className="label">Wear speed</span>
            <span className="num" style={{ fontSize: 'var(--fs-l)', color: (b.wearPer100 ?? 0) > typical * 2 ? 'var(--bad)' : 'var(--good)' }}>{b.wearPer100 != null ? `${fmt(b.wearPer100)}% per 100 cycles` : 'Needs 10+ cycles'}</span>
          </div>
          {b.wearPer100 != null && (
            <div className="wearbar" role="img" aria-label={`Wear ${fmt(b.wearPer100)} percent per 100 cycles against about ${typical} for a typical battery`} style={{ marginTop: 8 }}>
              <div className="track" />
              <motion.div className="fill" style={{ background: (b.wearPer100 ?? 0) > typical * 2 ? 'var(--bad)' : 'var(--good)' }} initial={false} animate={{ width: `${(b.wearPer100 / wearMax) * 100}%` }} />
              <div className="ref" style={{ left: `${(typical / wearMax) * 100}%` }} />
            </div>
          )}
          <div className="small muted" style={{ marginTop: 6 }}>The tick mark is a typical battery (about {typical}%). {(b.wearPer100 ?? 0) > typical * 2 ? 'Yours is ageing faster than its use explains.' : 'You are in a healthy range.'}</div>
        </div>
        <div className="row small muted" style={{ marginTop: 12, gap: 8, alignItems: 'flex-start' }}>
          <Clock3 size={14} aria-hidden style={{ flex: 'none', marginTop: 3 }} />
          {b.lastCalibrated ? <span>Last full calibration (below 15%, then to 100%) on {shortDate(b.lastCalibrated)}.</span>
            : <span>No full calibration seen yet. Run it down below 15% and charge to 100% once a month for the most accurate reading.</span>}
        </div>
      </Card>

      {/* Live */}
      <Card className="ov-live" title="Right now" mark={b.onAc ? 'var(--s-charger)' : 'var(--s-battery)'}>
        <div className="live-body">
          <BatteryCell percent={b.percent} charging={b.charging} onAc={b.onAc} />
          <div className="grid" style={{ gap: 14 }}>
            <div>
              <div className="big"><Counter value={b.percent} /><span className="unit">%</span></div>
              <div style={{ fontSize: 'var(--fs-l)', fontWeight: 600, marginTop: 6 }}>{flow}</div>
            </div>
            <div className="stat-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(118px, 1fr))' }}>
              <Stat label="Power" value={rate ? `${rate > 0 ? '+' : '−'}${Math.abs(rate).toFixed(1)}` : '0.0'} unit="W"
                sub={b.rateMethod === 'energy' ? 'Energy slope' : b.rateMethod === 'driver' ? 'Driver, smoothed' : undefined}
                title={`${methodLabel[b.rateMethod ?? 'driver']}${b.rateRawW != null ? `. Instant reading ${b.rateRawW.toFixed(1)} W` : ''}`} />
              <Stat label="Voltage" value={fmt(b.voltage, 2)} unit="V" />
              <Stat label="Temperature" value={tempC != null ? fmt(tempC) : heat ? cap(heat.level) : 'n/a'} unit={tempC != null ? '°C' : ''}
                sub={tempC == null && heat ? 'Estimated' : heat?.source === 'thermal-zone' ? 'Thermal zone' : undefined} color={tempC == null && !heat ? 'var(--ink-2)' : undefined} />
              <Stat label="Full on charger" value={d.holdMinutes > 0 ? minutes(d.holdMinutes) : '—'} color={d.holdMinutes >= 120 ? 'var(--warn)' : undefined} />
            </div>
          </div>
        </div>
      </Card>

      <Card className="ov-day" title="Last 24 hours" meta={<div className="legend"><span><i style={{ background: 'var(--s-charger)' }} />On charger</span><span><i style={{ background: 'var(--s-battery)' }} />On battery</span><span><i style={{ background: 'var(--warn-soft)', border: '1px solid var(--warn)' }} />Above 95%</span></div>}>
        {day == null ? <div className="skel" style={{ height: 210 }} /> : (
          <TimeChart points={day.map((p) => ({ t: new Date(p.t).getTime(), v: p.p, flag: !p.ac }))} from={now - 24 * 3.6e6} to={now}
            band={95} maxGap={6 * 60000} height={210} ariaLabel="Charge level over the last 24 hours"
            label={(p) => `${new Date(p.t).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}  ${p.v.toFixed(0)}%  ${p.flag ? 'battery' : 'charger'}`} />
        )}
      </Card>

      {heat && (
        <Card className="ov-heat" title="Heat" icon={<Flame size={15} aria-hidden />} meta={<span className={`tag ${levelTone(heat.level)}`}>{cap(heat.level)}</span>}>
          <div className="big" style={{ color: `var(--${levelTone(heat.level)})` }}>
            {heat.tempC != null ? <>{fmt(heat.tempC)}<span className="unit">°C</span></> : <>{heat.score}<span className="unit">/ 100 load</span></>}
          </div>
          {heat.tempC != null && <div className="small muted" style={{ marginTop: 10 }}>Heat score {heat.score} / 100 (65 or more is hot)</div>}
          <div style={{ marginTop: heat.tempC != null ? 4 : 12 }}><ScaleBar value={heat.score} color={`var(--${levelTone(heat.level)})`} label={`Heat score ${heat.score} of 100`} /></div>
          <div className="row wrap" style={{ gap: 6, marginTop: 10 }}>
            <span className="tag plain">CPU {Math.round(heat.cpuPercent)}%</span>
            <span className="tag plain">GPU {heat.gpuBusy ? 'busy' : 'idle'}</span>
            {heat.chargingW != null && <span className="tag plain">Charging {heat.chargingW.toFixed(0)} W</span>}
          </div>
          <p className="small muted" style={{ marginTop: 10 }}>{heat.basis}</p>
        </Card>
      )}

      {d.habit && (
        <Card className="ov-habit" title="Charging habit score" icon={<Plug size={15} aria-hidden />} meta={<span className="card-meta">Last 7 days</span>}>
          <div className="row" style={{ gap: 16, alignItems: 'center' }}>
            <div className="grade" style={{ background: `var(--${gradeTone(d.habit.grade)}-soft)`, color: `var(--${gradeTone(d.habit.grade)})` }} aria-label={`Grade ${d.habit.grade}`}>{d.habit.grade}</div>
            <div className="stack">
              <div className="readout" style={{ fontSize: 'var(--fs-2xl)' }}><Counter value={d.habit.score} /><span className="unit">/ 100</span></div>
              <div className="small muted">{d.habit.change == null ? 'First week measured' : d.habit.change === 0 ? 'Same as the week before' : <><span className={d.habit.change > 0 ? 'good' : 'bad'}>{d.habit.change > 0 ? '+' : '−'}{Math.abs(d.habit.change)}</span> vs the week before</>}</div>
            </div>
          </div>
          <div style={{ marginTop: 12 }}>
            {d.habit.factors.map((f) => (
              <div key={f.name} className="factor">
                <span style={{ fontWeight: 500 }}>{f.name}</span>
                <span className={`num small ${f.impact === 0 ? 'good' : f.impact >= 15 ? 'bad' : 'warn'}`}>{f.impact === 0 ? 'Fine' : `−${f.impact}`}</span>
                <span className="d">{f.detail}</span>
              </div>
            ))}
          </div>
        </Card>
      )}

      {d.pattern && <PatternCard d={d} />}

      {d.forecast && (
        <Card className="ov-forecast" title="Forecast" icon={<CalendarClock size={15} aria-hidden />}>
          <div className="forecast-grid">
            <div className="well">
              <Stat label="80% health" value={d.forecast.healthAt80On ? monthYear(d.forecast.healthAt80On) : b.health <= 80 ? 'Reached' : 'No trend yet'} color="var(--s-proj)"
                sub={d.forecast.monthsTo80 != null ? `About ${fmt(d.forecast.monthsTo80)} months at −${fmt(d.forecast.lossPerMonth)}% a month` : d.forecast.healthAt80On ? '' : 'Needs two weeks of history and a downward trend'} />
            </div>
            <div className="well">{(() => {
              const f = d.forecast!
              if (!b.onAc) return <Stat label="Battery left" value={f.hoursToEmpty != null ? duration(f.hoursToEmpty) : '—'} sub="At the current power draw" />
              if (f.hoursToLimit != null) return <Stat label={`To ${d.mode.chargeLimit}% limit`} value={duration(f.hoursToLimit)} sub={f.hoursToFull != null ? `${duration(f.hoursToFull)} to 100% (slower above 80%)` : 'At the current charging power'} />
              if (f.hoursToFull != null) return <Stat label="To full" value={duration(f.hoursToFull)} sub="The last 20% charges more slowly" />
              return <Stat label="Charge" value={b.percent >= 99 ? 'Full' : `Holding ${Math.round(b.percent)}%`} sub={b.percent >= 99 ? "Unplug for a while if you don't need 100%" : 'Not charging right now'} />
            })()}</div>
            <div className="well">
              <Stat label="At 11 pm" value={d.forecast.percentAtNight != null ? `${d.forecast.percentAtNight}%` : b.onAc ? 'On charger' : '—'}
                sub={d.forecast.percentAtNight != null ? (d.forecast.percentAtNight < 20 ? 'Plug in before then' : 'If you keep using it like this') : 'Shown when you run on battery before 11 pm'} />
            </div>
          </div>
          {d.auto.last && <div className="row small muted" style={{ marginTop: 12, gap: 6, alignItems: 'flex-start' }}><Wand2 size={14} aria-hidden style={{ flex: 'none', marginTop: 2 }} /><span>{d.auto.last}</span></div>}
        </Card>
      )}

      <div className="mini-row">
        <Card title="Stress today" icon={<Activity size={15} aria-hidden />} meta={<span className={`tag ${levelTone(d.stress.level)}`}>{d.stress.level}</span>}>
          <div className="big"><Counter value={d.stress.score} /><span className="unit">/ 100</span></div>
          <div style={{ marginTop: 10 }}><ScaleBar value={d.stress.score} color={levelColor(d.stress.level)} label={`Stress ${d.stress.score} of 100`} /></div>
          <div className="small muted" style={{ marginTop: 8 }}>{fmt(d.stress.hoursFull)} h at 100% on the charger, {fmt(d.stress.hoursLow)} h under 10%{d.stress.tempKnown ? `, ${fmt(d.stress.hoursHot)} h hot` : ''}.</div>
        </Card>
        <Card title="Time on the charger" icon={<BatteryCharging size={15} aria-hidden />}>
          <div className="big"><Counter value={d.chargerHours ?? null} decimals={1} /><span className="unit">h a day</span></div>
          <div className="small muted" style={{ marginTop: 10 }}>Switched on and plugged in, average of the last week from Windows' battery records.</div>
        </Card>
        <Card title="Last sleep" icon={<Moon size={15} aria-hidden />}>
          {d.lastSleep ? (
            <>
              <div className="big" style={{ color: d.lastSleep.unusual ? 'var(--bad)' : undefined }}>−{fmt(d.lastSleep.lost)}<span className="unit">%</span></div>
              <div className="small muted" style={{ marginTop: 10 }}>{time(d.lastSleep.start)} to {time(d.lastSleep.end)}, {fmt(d.lastSleep.perHour, 2)}% an hour. {d.lastSleep.unusual ? 'More than usual.' : 'Normal for this laptop.'}</div>
            </>
          ) : <div className="small muted">No sleep of 30 minutes or more recorded yet.</div>}
        </Card>
        <Card title={d.gpu.name?.replace('NVIDIA GeForce ', '') ?? 'Discrete GPU'} icon={<Cpu size={15} aria-hidden />}>
          <div className="big" style={{ fontSize: 'var(--fs-2xl)', fontFamily: 'var(--sans)', fontWeight: 600, color: !b.onAc && d.gpu.holders.length ? 'var(--warn)' : undefined }}>
            {!d.gpu.available ? '—' : b.onAc ? 'Watching' : d.gpu.holders.length ? 'Awake' : 'Asleep'}
          </div>
          <div className="small muted" style={{ marginTop: 10 }}>
            {b.onAc ? 'Checks which apps keep the NVIDIA GPU awake when you unplug.' : d.gpu.holders.length ? `Kept awake by ${d.gpu.holders.slice(0, 3).map((h) => h.processName).join(', ')}.` : 'No app is keeping it awake.'}
          </div>
          {(d.gaming || d.auto.gaming) && <div className="row small" style={{ gap: 6, marginTop: 8, color: 'var(--s-proj)' }}><Gamepad2 size={14} aria-hidden /> A game is running</div>}
        </Card>
      </div>

      <Card title="What to do" icon={<Zap size={15} aria-hidden />}
        meta={<>
          <button className="chip" onClick={() => go('charging')}><BatteryCharging size={13} aria-hidden /><span>{d.mode.name} mode, {d.mode.resumeAt}–{d.mode.chargeLimit}%</span></button>
          <button className="chip" onClick={() => go('charging')}><Plug size={13} aria-hidden /><span>{d.plug.configured ? d.plug.status : 'No smart plug'}</span></button>
        </>}>
        {d.tips.length === 0 && <p className="body">Nothing to change right now. Guardian X tells you when something needs attention.</p>}
        {d.tips.map((t) => (
          <div className="tip" key={t.title}>
            <i style={{ background: levelColor(t.level) }} aria-hidden />
            <div>
              <span className={`tag ${levelTone(t.level)}`}>{t.level === 'Alert' ? 'Important' : t.level === 'Warn' ? 'Worth changing' : t.level === 'Info' ? 'Good to know' : 'Fine'}</span>
              <h4>{t.title}</h4>
              <p className="body">{t.body}</p>
            </div>
          </div>
        ))}
      </Card>
    </div>
  )
}

function PatternCard({ d }: { d: Dashboard }) {
  const p = d.pattern!
  const b = d.battery!
  const pos = (hhmm: string) => (minutesOfDay(hhmm) / 1440) * 100
  const nowD = new Date()
  const nowPos = ((nowD.getHours() * 60 + nowD.getMinutes()) / 1440) * 100
  const edge = (x: number) => (x < 12 ? 'translateX(0)' : x > 88 ? 'translateX(-100%)' : undefined)
  const un = p.usualUnplug ? pos(p.usualUnplug) : null, pl = p.usualPlugIn ? pos(p.usualPlugIn) : null
  return (
    <Card className="ov-pattern" title="Your usual day" icon={<Clock3 size={15} aria-hidden />} meta={<span className="card-meta">Weekdays, last 3 weeks</span>}>
      {un == null && pl == null ? <p className="small muted">{p.message ?? 'Guardian X learns when you usually unplug and plug in. This fills in after a week or two.'}</p> : (
        <>
          <div className="timeline" role="img" aria-label={`Usually unplugged at ${clock(p.usualUnplug)} and plugged back in at ${clock(p.usualPlugIn)}`}>
            <div className="track" />
            {un != null && pl != null && pl > un && <div className="away" style={{ left: `${un}%`, width: `${pl - un}%` }} />}
            {un != null && <><div className="pin" style={{ left: `${un}%` }} /><div className="lbl top" style={{ left: `${un}%`, transform: edge(un) }}>Unplug {clock(p.usualUnplug)}</div></>}
            {pl != null && <><div className="pin" style={{ left: `${pl}%` }} /><div className="lbl bot" style={{ left: `${pl}%`, transform: edge(pl) }}>Plug in {clock(p.usualPlugIn)}</div></>}
            <div className="pin now" style={{ left: `${nowPos}%` }} title="Now" />
          </div>
          <div className="legend" style={{ marginTop: 6 }}>
            <span><i style={{ background: 'var(--s-battery)' }} />Usually on battery</span>
            <span><i style={{ background: 'var(--accent)', width: 3 }} />Now</span>
          </div>
        </>
      )}
      <div className="well" style={{ marginTop: 12 }}>
        {b.onAc && p.readyBy ? (
          <Stat label={`Ready by ${clock(p.readyBy.time)}`} value={`${p.readyBy.percent}%`} sub="Expected charge when you usually unplug" />
        ) : !b.onAc && p.lastsUntilPlugIn != null ? (
          <Stat label={`Until ${clock(p.usualPlugIn)}`} value={p.lastsUntilPlugIn ? 'Lasts' : 'Runs short'} color={p.lastsUntilPlugIn ? 'var(--good)' : 'var(--bad)'} sub="At the current power draw" />
        ) : <Stat label="Usual time away" value={un != null && pl != null && pl > un ? minutes(Math.round(((pl - un) / 100) * 1440)) : '—'} />}
      </div>
      {p.message && (un != null || pl != null) && <p className="small muted" style={{ marginTop: 10 }}>{p.message}</p>}
    </Card>
  )
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)
const gradeTone = (g: string) => (g === 'A' || g === 'B' ? 'good' : g === 'C' ? 'warn' : 'bad')
