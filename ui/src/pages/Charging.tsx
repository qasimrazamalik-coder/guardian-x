import { motion } from 'framer-motion'
import { BatteryCharging, Cpu, Gamepad2, Leaf, Monitor, Plane, Plug, Power, ShieldAlert } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Card, Seg, Skel, Toggle } from '../components/ui'
import { call } from '../lib/bridge'
import type { Dashboard, Mode, PlugConfig, PlugState } from '../lib/types'

const icons: Record<string, JSX.Element> = { desk: <Monitor size={18} />, gaming: <Gamepad2 size={18} />, travel: <Plane size={18} />, saver: <Leaf size={18} /> }

export function Charging({ d, refresh }: { d: Dashboard; refresh: () => void }) {
  const [modes, setModes] = useState<Mode[] | null>(null)
  const [cfg, setCfg] = useState<PlugConfig>({ kind: 'none', host: '', username: '', hasPassword: false })
  const [password, setPassword] = useState('')
  const [state, setState] = useState<PlugState>(d.plug)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState('')
  const [vendorMsg, setVendorMsg] = useState('')
  const [vendorBusy, setVendorBusy] = useState(false)
  useEffect(() => {
    call<{ modes: Mode[] }>('modes').then((m) => setModes(m.modes))
    call<{ config: PlugConfig; state: PlugState }>('plugGet').then((p) => { setCfg(p.config); setState(p.state) })
  }, [])
  const mode = d.mode
  const pct = d.battery?.percent ?? 0
  const v = d.vendor

  const save = async () => {
    setBusy(true); setMsg('')
    try {
      const s = await call<PlugState>('plugSave', { ...cfg, password: password || null })
      setState(s); setPassword(''); setMsg(s.on == null ? s.status : 'Connected. ' + s.status)
      refresh()
    } catch (e: any) { setMsg(e.message) } finally { setBusy(false) }
  }
  const set = async (on: boolean) => {
    setBusy(true)
    try { setState(await call<PlugState>('plugSet', { on })); refresh() } catch (e: any) { setMsg(e.message) } finally { setBusy(false) }
  }
  const setVendor = async (on: boolean) => {
    setVendorBusy(true)
    try { const r = await call<{ ok: boolean; message: string }>('setVendorLimit', { on }); setVendorMsg(r?.message ?? '') ; refresh() }
    catch (e: any) { setVendorMsg(e.message) } finally { setVendorBusy(false) }
  }

  return (
    <div className="grid charging-grid">
      <Card hero title={`Charge window, ${mode.name} mode`} icon={<BatteryCharging size={15} aria-hidden />}>
        <div className="window-scale" role="img" aria-label={`Charge window ${mode.resumeAt} to ${mode.chargeLimit} percent; battery now at ${Math.round(pct)} percent`}>
          <div className="track" />
          <motion.div className="win" initial={false} animate={{ left: `${mode.resumeAt}%`, width: `${mode.chargeLimit - mode.resumeAt}%` }} transition={{ type: 'spring', stiffness: 220, damping: 28 }} />
          <motion.div className="needle" initial={false} animate={{ left: `calc(${pct}% - 1px)` }} />
          <motion.div className="now-lbl" initial={false} animate={{ left: `${Math.min(94, Math.max(6, pct))}%` }}>{Math.round(pct)}% now</motion.div>
          {[0, 20, 40, 60, 80, 100].map((t) => <div key={t} className="tick" style={{ left: `${t}%`, transform: t === 0 ? 'none' : t === 100 ? 'translateX(-100%)' : undefined }}>{t}</div>)}
        </div>
        <p className="body" style={{ marginTop: 10 }}>
          {v?.nativeLimit === 'asus' && v.limitActive
            ? <>The laptop's built-in limit stops charging at <b>80%</b>. Guardian X alerts you at <b>{mode.chargeLimit}%</b> as well.</>
            : <>With a smart plug, Guardian X stops charging at <b>{mode.chargeLimit}%</b> and starts again at <b>{mode.resumeAt}%</b>. Without one it plays a sound to tell you to unplug.</>}
        </p>
        <div className="mode-cards" role="group" aria-label="Mode">
          {modes == null ? Array.from({ length: 4 }, (_, i) => <Skel key={i} h={150} r={7} />) : modes.map((m) => {
            const on = m.id === mode.id
            return (
              <button key={m.id} className="mode-card" aria-pressed={on} onClick={async () => { await call('setMode', { id: m.id }); refresh() }}>
                <span className="ic" aria-hidden>{icons[m.id] ?? <Power size={18} />}</span>
                <span className="nm">{m.name}</span>
                <span className="num small muted">{m.resumeAt}–{m.chargeLimit}%, {m.overlay}</span>
                <span className="small muted" style={{ marginTop: 6, lineHeight: 1.45 }}>{m.description}</span>
              </button>
            )
          })}
        </div>
        <div style={{ marginTop: 10 }}>
          <Toggle on={d.chargeLimitOn} onChange={async (val) => { await call('settingsSave', { chargeLimitOn: val }); refresh() }} label="Charge limit on" hint="Turn off to let it charge normally." />
          <div className="row wrap" style={{ marginTop: 6, gap: 10 }}>
            <button className="btn" disabled={!state.configured || d.chargeToFull} onClick={async () => { await call('chargeToFull'); refresh() }}><Power size={15} aria-hidden /> {d.chargeToFull ? 'Charging to 100%…' : 'Charge to full now'}</button>
            <span className="small muted" style={{ flex: '1 1 200px' }}>For a trip: ignores the limit until it's full or you unplug.</span>
          </div>
        </div>
      </Card>

      <div className="grid">
        {v && (
          <Card title={`Built-in limit, ${v.brand}`} icon={<Cpu size={15} aria-hidden />}
            meta={v.nativeLimit === 'asus' ? <span className={`tag ${v.limitActive ? 'good' : 'plain'}`}>{v.limitActive ? 'On' : 'Off'}</span> : <span className="tag plain">{v.nativeLimit === 'lenovo' ? 'Lenovo Vantage' : 'Not available'}</span>}>
            {v.nativeLimit === 'asus' ? (
              <>
                <Toggle on={!!v.limitActive} disabled={vendorBusy} onChange={setVendor} label="Stop charging at 80% (ASUS Battery Health Charging)" hint="Works inside the laptop, no plug needed. Turn off before a trip for the full runtime." />
                <p className="small muted" style={{ marginTop: 4 }}>{v.note}</p>
              </>
            ) : <p className="small muted">{v.note}</p>}
            <div className="status-line" role="status" aria-live="polite">{vendorMsg}</div>
          </Card>
        )}

        <Card title="Smart plug" icon={<Plug size={15} aria-hidden />} mark={state.configured ? (state.on ? 'var(--good)' : 'var(--warn)') : 'var(--line-2)'}>
          <div style={{ fontSize: 'var(--fs-l)', fontWeight: 600 }}>{state.configured ? state.name : 'Not set up'}</div>
          <p className="small muted" style={{ marginTop: 2 }}>{state.status}</p>
          <div style={{ marginTop: 16 }}>
            <span className="field-label" id="plugkind">Plug type</span>
            <Seg layoutId="plugkind" label="Plug type" value={cfg.kind} onChange={(k) => setCfg({ ...cfg, kind: k })}
              options={[{ value: 'none', label: 'None' }, { value: 'tapo', label: 'TP-Link Tapo' }, { value: 'shelly', label: 'Shelly' }, { value: 'simulated', label: 'Try without' }]} />
          </div>
          {(cfg.kind === 'tapo' || cfg.kind === 'shelly') && (
            <div className="grid" style={{ marginTop: 14, gap: 12 }}>
              <div><label className="field-label" htmlFor="plug-host">Plug IP address (from your router or the plug's app)</label>
                <input id="plug-host" className="input num" placeholder="192.168.1.50" value={cfg.host} onChange={(e) => setCfg({ ...cfg, host: e.target.value })} /></div>
              {cfg.kind === 'tapo' && <>
                <div><label className="field-label" htmlFor="plug-user">Tapo account email</label><input id="plug-user" className="input" value={cfg.username} onChange={(e) => setCfg({ ...cfg, username: e.target.value })} /></div>
                <div><label className="field-label" htmlFor="plug-pass">Tapo account password {cfg.hasPassword && '(saved; leave empty to keep)'}</label>
                  <input id="plug-pass" className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></div>
              </>}
            </div>
          )}
          <div className="row wrap" style={{ marginTop: 16, gap: 8 }}>
            <button className="btn primary" disabled={busy} onClick={save}>{busy ? 'Connecting…' : 'Save and test'}</button>
            <button className="btn" disabled={busy || !state.configured} onClick={() => set(true)}>Turn on</button>
            <button className="btn" disabled={busy || !state.configured} onClick={() => set(false)}>Turn off</button>
          </div>
          <div className="status-line" role="status" aria-live="polite">{msg}</div>
          {v?.nativeLimit !== 'asus' && (
            <div className="notice" style={{ marginTop: 16 }}>
              <ShieldAlert size={17} aria-hidden style={{ color: 'var(--warn)' }} />
              <p className="small muted">The plug cuts power at the wall, so it works on any laptop. The password stays on this PC, encrypted by Windows, and the plug is controlled on your Wi-Fi, not through the cloud.</p>
            </div>
          )}
        </Card>
      </div>
    </div>
  )
}
