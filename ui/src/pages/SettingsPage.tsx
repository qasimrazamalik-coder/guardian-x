import { Bell, Database, Play, Rocket, Volume2, Wrench } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Card, Eyebrow, Seg, Toggle } from '../components/ui'
import { call, on } from '../lib/bridge'
import type { Settings } from '../lib/types'

export function SettingsPage({ version }: { version: string }) {
  const [s, setS] = useState<Settings | null>(null)
  const [msg, setMsg] = useState('')
  useEffect(() => { call<Settings>('settingsGet').then(setS) }, [])
  useEffect(() => on('updateStatus', (d) => setMsg(String(d))), [])
  if (!s) return null
  const save = (patch: Partial<Settings>) => { const n = { ...s, ...patch }; setS(n); call('settingsSave', patch) }
  const act = async (method: string) => { setMsg(method === 'checkUpdate' ? 'Checking for updates…' : 'Working…'); try { const r = await call<{ message?: string }>(method); setMsg(r?.message ?? 'Done.') } catch (e: any) { setMsg(e.message) } }
  return (
    <div className="grid" style={{ gridTemplateColumns: '1fr 1fr', alignItems: 'start' }}>
      <div className="grid">
        <Card glow>
          <Eyebrow dot="var(--mint)"><Bell size={13} /> Alerts</Eyebrow>
          <Toggle on={s.notifyHigh} onChange={(v) => save({ notifyHigh: v })} label={<>Charged to <b className="num">{s.highPercent}%</b></>} hint="Tells you to unplug (or the plug cuts power)." />
          {s.notifyHigh && <input type="range" min={60} max={100} step={5} value={s.highPercent} onChange={(e) => save({ highPercent: +e.target.value })} />}
          <Toggle on={s.notifyLow} onChange={(v) => save({ notifyLow: v })} label={<>Low battery at <b className="num">{s.lowPercent}%</b></>} hint="Plug in before it runs too low." />
          {s.notifyLow && <input type="range" min={10} max={40} step={5} value={s.lowPercent} onChange={(e) => save({ lowPercent: +e.target.value })} />}
          <Toggle on={s.notifyFullHold} onChange={(v) => save({ notifyFullHold: v })} label="Sitting at 100% on the charger too long" />
          {s.notifyFullHold && <div style={{ padding: '2px 0 8px' }}><Seg layoutId="hold" value={String(s.fullHoldMinutes)} onChange={(v) => save({ fullHoldMinutes: +v })} options={[{ value: '60', label: '1 h' }, { value: '120', label: '2 h' }, { value: '240', label: '4 h' }]} /></div>}
          <Toggle on={s.notifySleepDrain} onChange={(v) => save({ notifySleepDrain: v })} label="Unusual drain while asleep" />
          <Toggle on={s.notifyGpu} onChange={(v) => save({ notifyGpu: v })} label="Apps waking the NVIDIA GPU on battery" />
        </Card>
        <Card delay={0.05}>
          <Eyebrow><Volume2 size={13} /> Sounds</Eyebrow>
          <Toggle on={s.soundsOn} onChange={(v) => save({ soundsOn: v })} label="Play sounds" hint="Guardian X's own synthesised sounds, not Windows defaults." />
          <Toggle on={s.plugSounds} onChange={(v) => save({ plugSounds: v })} label="Sound when the charger connects or disconnects" />
          <div className="row" style={{ marginTop: 10, justifyContent: 'space-between' }}>
            <Seg layoutId="pack" value={s.soundPack} onChange={(v) => save({ soundPack: v })} options={[{ value: 'nebula', label: 'Nebula · soft' }, { value: 'pulse', label: 'Pulse · crisp' }]} />
          </div>
          <div className="row" style={{ marginTop: 12, flexWrap: 'wrap', gap: 8 }}>
            {[['Charged', 'charged'], ['Low', 'low'], ['Plugged in', 'pluggedin'], ['Unplugged', 'unplugged'], ['Alert', 'alert']].map(([l, c]) => (
              <button key={c} className="btn" style={{ padding: '7px 12px' }} onClick={() => call('playSound', { cue: c })}><Play size={13} /> {l}</button>
            ))}
          </div>
        </Card>
      </div>
      <div className="grid">
        <Card delay={0.08}>
          <Eyebrow><Rocket size={13} /> Start-up & modes</Eyebrow>
          <Toggle on={s.startWithWindows} onChange={(v) => save({ startWithWindows: v })} label="Start with Windows (in the tray)" hint="Guardian X must run to record history and switch the plug." />
          <Toggle on={s.applyPowerMode} onChange={(v) => save({ applyPowerMode: v })} label="Modes change the Windows power mode" />
        </Card>
        <Card delay={0.11}>
          <Eyebrow><Database size={13} /> Your data</Eyebrow>
          <div className="body">Everything stays on this PC in %LOCALAPPDATA%\GuardianXData. No account, no telemetry.</div>
          <div className="row" style={{ marginTop: 14, flexWrap: 'wrap', gap: 10 }}>
            <button className="btn" onClick={() => act('exportCsv')}>Export CSV</button>
            <button className="btn" onClick={() => act('openData')}>Open folder</button>
            <button className="btn danger" onClick={() => act('deleteHistory')}>Delete history</button>
          </div>
        </Card>
        <Card delay={0.14}>
          <Eyebrow><Wrench size={13} /> Maintenance</Eyebrow>
          <div className="row" style={{ flexWrap: 'wrap', gap: 10 }}>
            <button className="btn" onClick={() => act('checkUpdate')}>Check for updates</button>
            <button className="btn" onClick={() => act('repair')}>Repair</button>
            <button className="btn danger" onClick={() => act('quit')}>Quit Guardian X</button>
          </div>
          {msg && <div className="body" role="status" aria-live="polite" style={{ marginTop: 12, whiteSpace: 'pre-line', color: 'var(--cyan)', userSelect: 'text' }}>{msg}</div>}
          <div className="body" style={{ fontSize: 12, marginTop: 12 }}>Uninstall from Windows Settings › Apps › Installed apps › Guardian X. You'll be asked whether to keep your history.</div>
          <div className="num" style={{ fontSize: 11, color: 'var(--faint)', marginTop: 10 }}>Guardian X {version}</div>
        </Card>
      </div>
    </div>
  )
}
