import { Bell, Database, FileText, Monitor, Moon, Palette, Play, Rocket, Sun, Volume2, Wand2, Wrench } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Card, Loading, Seg, SkelCard, Toggle } from '../components/ui'
import { setTheme, useTheme, type ThemeChoice } from '../lib/theme'
import { call, on } from '../lib/bridge'
import type { ReportFile, Settings } from '../lib/types'

const packInfo: Record<string, string> = {
  aurora: 'Aurora · warm chimes',
  crystal: 'Crystal · glass bells',
  pulse: 'Pulse · modern plucks',
  nebula: 'Nebula · soft pads',
}
const modeOptions = [{ value: 'desk', label: 'Desk' }, { value: 'gaming', label: 'Gaming' }, { value: 'travel', label: 'Travel' }, { value: 'saver', label: 'Saver' }]
const hour = (h: number) => `${String(h).padStart(2, '0')}:00`

export function SettingsPage({ version }: { version: string }) {
  const [s, setS] = useState<Settings | null>(null)
  const [msg, setMsg] = useState('')
  const [reportMsg, setReportMsg] = useState('')
  const [reports, setReports] = useState<ReportFile[]>([])
  useEffect(() => { call<Settings>('settingsGet').then(setS); call<ReportFile[]>('reportsList').then(setReports) }, [])
  useEffect(() => on('updateStatus', (d) => setMsg(String(d))), [])
  const theme = useTheme()
  if (!s) return (
    <div className="grid settings-grid">
      <Loading what="settings" />
      <div className="grid"><SkelCard lines={6} /><SkelCard lines={4} /></div>
      <div className="grid"><SkelCard lines={3} /><SkelCard lines={4} /></div>
    </div>
  )
  const pickTheme = (t: ThemeChoice) => { setTheme(t); save({ theme: t }) }
  const save = (patch: Partial<Settings>) => { const n = { ...s, ...patch }; setS(n); call('settingsSave', patch) }
  const act = async (method: string) => { setMsg(method === 'checkUpdate' ? 'Checking for updates…' : 'Working…'); try { const r = await call<{ message?: string }>(method); setMsg(r?.message ?? 'Done.') } catch (e: any) { setMsg(e.message) } }
  const packs = s.packs?.length ? s.packs : Object.keys(packInfo)

  return (
    <div className="grid settings-grid">
      <div className="grid">
        <Card hero title="Alerts" icon={<Bell size={15} aria-hidden />}>
          <Toggle on={s.notifyHigh} onChange={(v) => save({ notifyHigh: v })} label={<>Charged to <b className="num">{s.highPercent}%</b></>} hint="Tells you to unplug (or the plug cuts power)." />
          {s.notifyHigh && <input type="range" aria-label="Charged alert level" min={60} max={100} step={5} value={s.highPercent} onChange={(e) => save({ highPercent: +e.target.value })} />}
          <Toggle on={s.notifyLow} onChange={(v) => save({ notifyLow: v })} label={<>Low battery at <b className="num">{s.lowPercent}%</b></>} hint="Plug in before it runs too low." />
          {s.notifyLow && <input type="range" aria-label="Low battery alert level" min={10} max={40} step={5} value={s.lowPercent} onChange={(e) => save({ lowPercent: +e.target.value })} />}
          <Toggle on={s.notifyFullHold} onChange={(v) => save({ notifyFullHold: v })} label="Sitting at 100% on the charger too long" />
          {s.notifyFullHold && <div style={{ padding: '2px 0 8px' }}><Seg layoutId="hold" label="Alert after" value={String(s.fullHoldMinutes)} onChange={(v) => save({ fullHoldMinutes: +v })} options={[{ value: '60', label: '1 h' }, { value: '120', label: '2 h' }, { value: '240', label: '4 h' }]} /></div>}
          <Toggle on={!!s.smartLowAlert} onChange={(v) => save({ smartLowAlert: v })} label="Warn if the charge won't last until I usually plug in" hint="Uses your usual plug-in time and the current power draw." />
          <Toggle on={s.notifySleepDrain} onChange={(v) => save({ notifySleepDrain: v })} label="Unusual drain while asleep" />
          <Toggle on={s.notifyGpu} onChange={(v) => save({ notifyGpu: v })} label="Apps waking the NVIDIA GPU on battery" />
          <Toggle on={s.toastsOn ?? true} onChange={(v) => save({ toastsOn: v })} label="Show Windows notifications" hint="Turn off to keep alerts to sounds and this window." />
          <div className="divider" />
          <Toggle on={s.quietHoursOn} onChange={(v) => save({ quietHoursOn: v })} label={<><Moon size={13} aria-hidden style={{ verticalAlign: -2 }} /> Quiet hours</>}
            hint={s.quietHoursOn ? `Alerts still show from ${hour(s.quietFrom)} to ${hour(s.quietTo)}, without sound.` : 'Silence alert sounds at night.'} />
          {s.quietHoursOn && (
            <div className="row" style={{ gap: 12, paddingBottom: 6, flexWrap: 'wrap' }}>
              <label className="field-inline">From <select className="select" value={s.quietFrom} onChange={(e) => save({ quietFrom: +e.target.value })}>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hour(h)}</option>)}</select></label>
              <label className="field-inline">to <select className="select" value={s.quietTo} onChange={(e) => save({ quietTo: +e.target.value })}>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hour(h)}</option>)}</select></label>
            </div>
          )}
        </Card>

        <Card title="Sounds" icon={<Volume2 size={15} aria-hidden />}>
          <Toggle on={s.soundsOn} onChange={(v) => save({ soundsOn: v })} label="Play sounds" hint="Guardian X's own sounds, made on this PC: stereo, with real reverb. Not Windows defaults." />
          <Toggle on={s.plugSounds} onChange={(v) => save({ plugSounds: v })} label="Sound when the charger connects or disconnects" />
          <div className="pack-grid" role="radiogroup" aria-label="Sound pack">
            {packs.map((p) => (
              <button key={p} role="radio" aria-checked={s.soundPack === p} className="pack"
                onClick={() => { save({ soundPack: p }); setTimeout(() => call('playSound', { cue: 'charged' }), 120) }}>
                <span className="pack-name">{(packInfo[p] ?? p).split(' · ')[0]}</span>
                <span className="pack-desc">{(packInfo[p] ?? '').split(' · ')[1] ?? ''}</span>
              </button>
            ))}
          </div>
          <div className="row" style={{ marginTop: 12, flexWrap: 'wrap', gap: 8 }}>
            {[['Charged', 'charged'], ['Low', 'low'], ['Plugged in', 'pluggedin'], ['Unplugged', 'unplugged'], ['Alert', 'alert']].map(([l, c]) => (
              <button key={c} className="btn small" aria-label={`Play ${l} sound`} onClick={() => call('playSound', { cue: c })}><Play size={13} aria-hidden /> {l}</button>
            ))}
          </div>
        </Card>
      </div>

      <div className="grid">
        <Card title="Appearance" icon={<Palette size={15} aria-hidden />}>
          <div className="theme-pick" role="radiogroup" aria-label="Theme">
            {([['system', 'System', Monitor], ['light', 'Light', Sun], ['dark', 'Dark', Moon]] as const).map(([v, l, Icon]) => (
              <button key={v} type="button" role="radio" aria-checked={theme.choice === v} className="theme-opt" onClick={() => pickTheme(v)}>
                <span className={`sw ${v}`} aria-hidden>{v === 'system' ? <><i style={{ background: '#fafbfc' }} /><i style={{ background: '#1c2027' }} /></> : <b />}</span>
                <span className="row"><Icon size={14} aria-hidden />{l}</span>
              </button>
            ))}
          </div>
          <p className="small muted" style={{ marginTop: 10 }}>{theme.choice === 'system' ? `Follows Windows, now ${theme.resolved}.` : `Always ${theme.choice}.`} The desktop widget uses the same theme.</p>
        </Card>

        <Card title="Auto-actions" icon={<Wand2 size={15} aria-hidden />}>
          <Toggle on={s.autoModeOnPower} onChange={(v) => save({ autoModeOnPower: v })} label="Switch mode when I plug in or unplug" hint="Changes the charge window and Windows power mode for you." />
          {s.autoModeOnPower && (
            <div className="grid" style={{ gap: 10, paddingBottom: 8 }}>
              <div className="row mode-pick"><span className="small muted">On the charger</span><Seg layoutId="acmode" label="Mode on the charger" value={s.acMode} onChange={(v) => save({ acMode: v })} options={modeOptions} /></div>
              <div className="row mode-pick"><span className="small muted">On battery</span><Seg layoutId="dcmode" label="Mode on battery" value={s.batteryMode} onChange={(v) => save({ batteryMode: v })} options={modeOptions} /></div>
            </div>
          )}
          <Toggle on={s.autoGamingMode} onChange={(v) => save({ autoGamingMode: v })} label="Gaming mode while a game is full screen" hint="Switches back when the game closes. Borderless-window games aren't detected." />
          <Toggle on={s.autoEfficiencyOnBattery} onChange={(v) => save({ autoEfficiencyOnBattery: v })} label="Efficiency mode for chosen apps on battery"
            hint={s.efficiencyApps.length ? `For ${s.efficiencyApps.join(', ')}. Pick apps with the leaf on the Apps page.` : 'Pick apps with the leaf on the Apps page.'} />
        </Card>

        <Card title="Reports and widget" icon={<FileText size={15} aria-hidden />}>
          <Toggle on={s.weeklyReportOn} onChange={(v) => save({ weeklyReportOn: v })} label="Weekly battery report" hint="Every Monday: health, trend, forecast, day-by-day stress and battery use by app. Saved on this PC." />
          <Toggle on={s.widgetOn} onChange={(v) => { setS({ ...s, widgetOn: v }); call('widgetSet', { on: v }) }} label="Mini widget on the desktop" hint="A small always-on-top readout. Drag to move, double-click to open Guardian X." />
          <div className="row" style={{ marginTop: 10, flexWrap: 'wrap', gap: 10 }}>
            <button className="btn" onClick={async () => { setReportMsg('Making the report…'); const r = await call<{ message?: string }>('weeklyReport'); setReportMsg(r?.message ?? ''); call<ReportFile[]>('reportsList').then(setReports) }}>Make a report now</button>
          </div>
          <div className="status-line" role="status" aria-live="polite">{reportMsg}</div>
          {reports.length > 0 && (
            <div style={{ marginTop: 10 }}>
              {reports.slice(0, 4).map((r) => (
                <div key={r.name} className="row list-row">
                  <span className="num" style={{ fontSize: 12.5 }}>Week of {new Date(r.date).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}</span>
                  <button className="btn small" aria-label={`Open report for week of ${new Date(r.date).toLocaleDateString()}`} onClick={() => call('openReport', { name: r.name })}>Open</button>
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card title="Start-up and modes" icon={<Rocket size={15} aria-hidden />}>
          <Toggle on={s.startWithWindows} onChange={(v) => save({ startWithWindows: v })} label="Start with Windows (in the tray)" hint="Guardian X must run to record history and switch the plug." />
          <Toggle on={s.applyPowerMode} onChange={(v) => save({ applyPowerMode: v })} label="Modes change the Windows power mode" />
        </Card>
        <Card title="Your data" icon={<Database size={15} aria-hidden />}>
          <div className="small muted">Everything stays on this PC in %LOCALAPPDATA%\GuardianXData. No account, no telemetry.</div>
          <div className="row" style={{ marginTop: 14, flexWrap: 'wrap', gap: 10 }}>
            <button className="btn" onClick={() => act('exportCsv')}>Export CSV</button>
            <button className="btn" onClick={() => act('openData')}>Open folder</button>
            <button className="btn danger" onClick={() => act('deleteHistory')}>Delete history</button>
          </div>
        </Card>
        <Card title="Maintenance" icon={<Wrench size={15} aria-hidden />}>
          <div className="row" style={{ flexWrap: 'wrap', gap: 10 }}>
            <button className="btn" onClick={() => act('checkUpdate')}>Check for updates</button>
            <button className="btn" onClick={() => act('repair')}>Repair</button>
            <button className="btn danger" onClick={() => act('quit')}>Quit Guardian X</button>
          </div>
          <div className="status-line" role="status" aria-live="polite" style={{ whiteSpace: 'pre-line', userSelect: 'text' }}>{msg}</div>
          <div className="small muted" style={{ marginTop: 12 }}>Uninstall from Windows Settings › Apps › Installed apps › Guardian X. You'll be asked whether to keep your history.</div>
          <div className="num xs faint" style={{ marginTop: 10 }}>Guardian X {version}</div>
        </Card>
      </div>
    </div>
  )
}
