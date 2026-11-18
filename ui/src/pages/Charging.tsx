import { motion } from 'framer-motion'
import { Gamepad2, Leaf, Monitor, Plane, Plug, Power, ShieldAlert } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Card, Eyebrow, Seg, Toggle } from '../components/ui'
import { call } from '../lib/bridge'
import type { Dashboard, Mode, PlugConfig, PlugState } from '../lib/types'

const icons: Record<string, JSX.Element> = { desk: <Monitor size={18} />, gaming: <Gamepad2 size={18} />, travel: <Plane size={18} />, saver: <Leaf size={18} /> }

export function Charging({ d, refresh }: { d: Dashboard; refresh: () => void }) {
  const [modes, setModes] = useState<Mode[]>([])
  const [cfg, setCfg] = useState<PlugConfig>({ kind: 'none', host: '', username: '', hasPassword: false })
  const [password, setPassword] = useState('')
  const [state, setState] = useState<PlugState>(d.plug)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState('')
  useEffect(() => {
    call<{ modes: Mode[] }>('modes').then((m) => setModes(m.modes))
    call<{ config: PlugConfig; state: PlugState }>('plugGet').then((p) => { setCfg(p.config); setState(p.state) })
  }, [])
  const mode = d.mode
  const pct = d.battery?.percent ?? 0

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

  return (
    <div className="grid charging-grid">
      <Card glow>
        <Eyebrow dot="var(--mint)">Charge window · {mode.name} mode</Eyebrow>
        <div style={{ position: 'relative', height: 74, marginTop: 8 }}>
          <div style={{ position: 'absolute', top: 30, left: 0, right: 0, height: 14, borderRadius: 7, background: 'rgba(255,255,255,0.05)', border: '1px solid var(--line)' }} />
          <motion.div layout style={{ position: 'absolute', top: 30, height: 14, borderRadius: 7, left: `${mode.resumeAt}%`, width: `${mode.chargeLimit - mode.resumeAt}%`, background: 'var(--grad)', boxShadow: '0 0 20px rgba(61,255,176,0.45)' }} transition={{ type: 'spring', stiffness: 200, damping: 26 }} />
          <motion.div layout style={{ position: 'absolute', top: 18, left: `calc(${pct}% - 1.5px)`, width: 3, height: 38, background: 'var(--ink)', borderRadius: 2, boxShadow: '0 0 10px #fff' }} />
          <motion.div layout className="num" style={{ position: 'absolute', top: 0, left: `${Math.min(92, Math.max(5, pct))}%`, transform: 'translateX(-50%)', fontSize: 11, whiteSpace: 'nowrap' }}>{Math.round(pct)}% now</motion.div>
          {[0, 20, 40, 60, 80, 100].map((v) => <div key={v} className="num" style={{ position: 'absolute', top: 56, left: `${v}%`, transform: 'translateX(-50%)', fontSize: 10, color: 'var(--faint)' }}>{v}</div>)}
        </div>
        <div className="body" style={{ marginTop: 12 }}>
          With a smart plug, Guardian X stops charging at <b style={{ color: 'var(--mint)' }}>{mode.chargeLimit}%</b> and starts again at <b style={{ color: 'var(--mint)' }}>{mode.resumeAt}%</b>. Without one it alerts you with a sound to unplug.
        </div>
        <div className="grid mode-cards">
          {modes.map((m) => {
            const on = m.id === mode.id
            return (
              <motion.button key={m.id} whileHover={{ y: -3 }} aria-pressed={on} onClick={async () => { await call('setMode', { id: m.id }); refresh() }}
                style={{ position: 'relative', cursor: 'pointer', textAlign: 'left', font: 'inherit', color: 'inherit', display: 'flex', flexDirection: 'column', alignItems: 'flex-start', justifyContent: 'flex-start', width: '100%', padding: 16, borderRadius: 14, border: `1px solid ${on ? 'rgba(61,255,176,0.45)' : 'var(--line)'}`, background: on ? 'rgba(61,255,176,0.07)' : 'rgba(0,0,0,0.2)' }}>
                <div style={{ color: on ? 'var(--mint)' : 'var(--muted)' }}>{icons[m.id]}</div>
                <div style={{ fontWeight: 600, marginTop: 10 }}>{m.name}</div>
                <div className="num muted" style={{ fontSize: 11, marginTop: 2 }}>{m.resumeAt}–{m.chargeLimit}% · {m.overlay}</div>
                <div className="body" style={{ fontSize: 12, marginTop: 8 }}>{m.description}</div>
              </motion.button>
            )
          })}
        </div>
        <div style={{ marginTop: 14 }}>
          <Toggle on={d.chargeLimitOn} onChange={async (v) => { await call('settingsSave', { chargeLimitOn: v }); refresh() }} label="Charge limit on" hint="Turn off to let it charge normally." />
          <div className="row" style={{ marginTop: 6 }}>
            <button className="btn" disabled={!state.configured || d.chargeToFull} onClick={async () => { await call('chargeToFull'); refresh() }}><Power size={15} /> {d.chargeToFull ? 'Charging to 100%…' : 'Charge to full now'}</button>
            <span className="body" style={{ fontSize: 12.5 }}>For a trip: ignores the limit until it's full or you unplug.</span>
          </div>
        </div>
      </Card>

      <Card delay={0.06}>
        <Eyebrow dot={state.configured ? (state.on ? 'var(--mint)' : 'var(--amber)') : 'var(--faint)'}><Plug size={13} /> Smart plug</Eyebrow>
        <div style={{ fontSize: 20, fontWeight: 600 }}>{state.configured ? state.name : 'Not set up'}</div>
        <div className="body" style={{ marginTop: 4 }}>{state.status}</div>
        <div style={{ marginTop: 18 }}>
          <label className="field-label">Plug type</label>
          <Seg layoutId="plugkind" value={cfg.kind} onChange={(k) => setCfg({ ...cfg, kind: k })}
            options={[{ value: 'none', label: 'None' }, { value: 'tapo', label: 'TP-Link Tapo' }, { value: 'shelly', label: 'Shelly' }, { value: 'simulated', label: 'Try without' }]} />
        </div>
        {(cfg.kind === 'tapo' || cfg.kind === 'shelly') && (
          <div className="grid" style={{ marginTop: 16, gap: 12 }}>
            <div><label className="field-label">Plug IP address (from your router or the plug's app)</label>
              <input className="input num" placeholder="192.168.1.50" value={cfg.host} onChange={(e) => setCfg({ ...cfg, host: e.target.value })} /></div>
            {cfg.kind === 'tapo' && <>
              <div><label className="field-label">Tapo account email</label><input className="input" value={cfg.username} onChange={(e) => setCfg({ ...cfg, username: e.target.value })} /></div>
              <div><label className="field-label">Tapo account password {cfg.hasPassword && '(saved; leave empty to keep)'}</label>
                <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></div>
            </>}
          </div>
        )}
        <div className="row" style={{ marginTop: 16, flexWrap: 'wrap', gap: 10 }}>
          <button className="btn primary" disabled={busy} onClick={save}>{busy ? 'Connecting…' : 'Save & test'}</button>
          <button className="btn" disabled={busy || !state.configured} onClick={() => set(true)}>Turn on</button>
          <button className="btn" disabled={busy || !state.configured} onClick={() => set(false)}>Turn off</button>
        </div>
        {msg && <div className="body" style={{ marginTop: 10, color: 'var(--cyan)' }}>{msg}</div>}
        <div style={{ marginTop: 20, padding: 14, borderRadius: 12, border: '1px solid rgba(255,181,71,0.25)', background: 'rgba(255,181,71,0.05)' }}>
          <div className="row" style={{ gap: 10, color: 'var(--amber)', alignItems: 'flex-start' }}><ShieldAlert size={18} style={{ flex: 'none', marginTop: 2 }} />
            <div className="body" style={{ fontSize: 12.5 }}>Why a plug: HP offers an 80% charge limit only on business laptops, not on OMEN, so no app can stop charging inside your laptop. The plug cuts power at the wall instead. The password stays on this PC, encrypted by Windows, and the plug is controlled on your Wi-Fi, not through the cloud.</div>
          </div>
        </div>
      </Card>
    </div>
  )
}
