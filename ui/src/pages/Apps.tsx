import { AnimatePresence, motion } from 'framer-motion'
import { FolderOpen, HardDrive, Leaf, Lightbulb, MemoryStick, Power, Search, Sparkles, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Bar, Card, Counter, Eyebrow, Seg, Toggle, useTicker } from '../components/ui'
import { call } from '../lib/bridge'
import { shortDate } from '../lib/format'
import type { AppsReport, BigFile, StartupApp, Storage, TempSummary } from '../lib/types'

type SortKey = 'cpu' | 'ram' | 'disk' | 'power'

export function Apps() {
  const [tab, setTab] = useState<'apps' | 'storage'>('apps')
  return (
    <div className="grid">
      <div><Seg layoutId="apps-tab" value={tab} onChange={setTab} options={[{ value: 'apps', label: 'Apps' }, { value: 'storage', label: 'Storage & cleanup' }]} /></div>
      {tab === 'apps' ? <AppsTab /> : <StorageTab />}
    </div>
  )
}

function AppsTab() {
  const tick = useTicker(3000)
  const [r, setR] = useState<AppsReport | null>(null)
  const [sort, setSort] = useState<SortKey>('ram')
  const [showSystem, setShowSystem] = useState(false)
  const [note, setNote] = useState('')
  const load = () => call<AppsReport>('apps').then(setR)
  useEffect(() => { load() }, [tick])
  if (!r) return null
  const key = (a: AppsReport['apps'][0]) => sort === 'cpu' ? a.cpuPercent : sort === 'ram' ? a.memoryMb : sort === 'disk' ? a.diskMbPerSec : a.estimatedWatts ?? 0
  const list = r.apps.filter((a) => showSystem || a.kind !== 'system').sort((x, y) => key(y) - key(x)).slice(0, 14)
  const maxRam = Math.max(...list.map((a) => a.memoryMb), 1)
  const toggle = async (app: string, on: boolean) => {
    const res = await call<{ message?: string }>('setEfficiency', { app, on })
    setNote(res?.message ?? '')
    load()
  }
  return (
    <>
      <div className="grid apps-top">
        <Card glow>
          <Eyebrow dot="var(--violet)"><MemoryStick size={13} aria-hidden /> Memory</Eyebrow>
          <div className="big"><Counter value={r.usedRamGb} decimals={1} /><span className="muted" style={{ fontSize: 16 }}> / {r.totalRamGb.toFixed(0)} GB</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.usedRamGb} max={r.totalRamGb} color="linear-gradient(90deg,#9b8cff,#3de0ff)" /></div>
        </Card>
        <Card delay={0.04}>
          <Eyebrow dot="var(--cyan)">CPU</Eyebrow>
          <div className="big"><Counter value={r.cpuTotal} /><span className="muted" style={{ fontSize: 16 }}> %</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.cpuTotal} /></div>
        </Card>
        <Card delay={0.08}>
          <Eyebrow><Lightbulb size={13} aria-hidden /> How to save</Eyebrow>
          {r.tips.length === 0 ? <div className="body">Nothing heavy right now.</div> : r.tips.slice(0, 2).map((t) => <div key={t.app} className="body" style={{ marginBottom: 8, fontSize: 13 }}>{t.text}</div>)}
        </Card>
      </div>

      <Card delay={0.12}>
        <div className="row" style={{ justifyContent: 'space-between', marginBottom: 6, flexWrap: 'wrap', gap: 10 }}>
          <Eyebrow dot="var(--mint)">Apps · live {r.onBattery ? '· on battery, power estimated' : ''}</Eyebrow>
          <div className="row" style={{ gap: 12, marginBottom: 12, flexWrap: 'wrap' }}>
            <button className="chip" style={{ color: showSystem ? 'var(--cyan)' : 'var(--muted)' }} aria-pressed={showSystem} onClick={() => setShowSystem(!showSystem)}>{showSystem ? 'hide' : 'show'} Windows processes</button>
            <Seg layoutId="sort" value={sort} onChange={setSort} options={[{ value: 'ram', label: 'RAM' }, { value: 'cpu', label: 'CPU' }, { value: 'disk', label: 'Disk' }, { value: 'power', label: 'Battery' }]} />
          </div>
        </div>
        <div className="table-wrap">
          <table className="table apps-table">
            <thead><tr><th>App</th><th>RAM</th><th>CPU</th><th>Disk</th><th>Battery</th><th>Proc.</th><th title="Windows Efficiency mode">Efficiency</th></tr></thead>
            <tbody>
              <AnimatePresence initial={false}>
                {list.map((a) => (
                  <motion.tr key={a.key} layout transition={{ type: 'spring', stiffness: 400, damping: 40 }} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}>
                    <td><div className="app-name">
                      <span className="kind" style={{ background: a.kind === 'browser' ? 'var(--cyan)' : a.kind === 'system' ? 'var(--faint)' : 'var(--mint)' }} />
                      <span className="ellipsis" title={a.name}>{a.name}</span></div></td>
                    <td><div className="ram-cell">
                      <Bar value={a.memoryMb} max={maxRam} color="linear-gradient(90deg,#9b8cff,#3de0ff)" />
                      <span className="num">{a.memoryMb >= 1024 ? `${(a.memoryMb / 1024).toFixed(1)} GB` : `${Math.round(a.memoryMb)} MB`}</span></div></td>
                    <td className="num" style={{ color: a.cpuPercent >= 15 ? 'var(--amber)' : undefined }}>{a.cpuPercent.toFixed(1)}%</td>
                    <td className="num">{a.diskMbPerSec.toFixed(1)} MB/s</td>
                    <td className="num">{a.estimatedWatts != null ? `~${a.estimatedWatts.toFixed(1)} W` : <span className="muted" title="Battery use is estimated only while you're on battery">—</span>}</td>
                    <td className="num muted">{a.processes}</td>
                    <td>{a.canThrottle
                      ? <button className={`leaf ${a.efficiency ? 'on' : ''}`} aria-pressed={a.efficiency} title={a.efficiency ? 'Efficiency mode on. Click to turn off.' : 'Run this app in Windows Efficiency mode'}
                          onClick={() => toggle(a.key, !a.efficiency)}><Leaf size={14} aria-hidden /><span className="sr-only">Efficiency mode for {a.name}</span></button>
                      : <span className="muted" title="Windows processes are left alone">—</span>}</td>
                  </motion.tr>
                ))}
              </AnimatePresence>
            </tbody>
          </table>
        </div>
        <div className="body" role="status" aria-live="polite" style={{ fontSize: 12.5, marginTop: 10 }}>
          {note || <>The leaf turns on Windows <b>Efficiency mode</b> for an app: it runs on low-power cores at lower priority, with nothing closed. {r.autoEfficiency ? 'Auto-actions keep it on while you\'re on battery.' : 'Turn on "Efficiency mode on battery" in Settings > Auto-actions to apply it automatically.'}</>}
        </div>
      </Card>
    </>
  )
}

function StorageTab() {
  const [st, setSt] = useState<Storage | null>(null)
  const [temp, setTemp] = useState<TempSummary | null>(null)
  const [msg, setMsg] = useState('')
  const [confirmTemp, setConfirmTemp] = useState(false)
  const [files, setFiles] = useState<BigFile[] | null>(null)
  const [scanning, setScanning] = useState(false)
  const [startup, setStartup] = useState<StartupApp[] | null>(null)
  const refresh = () => { call<Storage>('storage').then(setSt); call<TempSummary>('tempScan').then(setTemp) }
  useEffect(() => { refresh(); call<StartupApp[]>('startupList').then(setStartup) }, [])
  const act = async (method: string) => { setMsg('Working…'); try { const r = await call<{ message?: string }>(method); setMsg(r?.message ?? 'Done.') } catch (e: any) { setMsg(e.message) } refresh() }
  const scan = async () => { setScanning(true); try { setFiles(await call<BigFile[]>('bigFiles')) } finally { setScanning(false) } }

  return (
    <>
      <div className="grid split-even">
        <Card glow>
          <Eyebrow dot="var(--amber)"><HardDrive size={13} aria-hidden /> Drives</Eyebrow>
          {!st ? <div className="body">Reading drives…</div> : (
            <div className="grid" style={{ gap: 14 }}>
              {st.drives.map((d) => {
                const used = d.totalGb - d.freeGb
                const low = d.freeGb / d.totalGb < 0.1
                return (
                  <div key={d.name}>
                    <div className="row" style={{ justifyContent: 'space-between', marginBottom: 6, gap: 10 }}>
                      <span className="ellipsis">{d.name} {d.label}</span>
                      <span className="num" style={{ color: low ? 'var(--red)' : 'var(--muted)', whiteSpace: 'nowrap' }}>{d.freeGb.toFixed(1)} GB free of {d.totalGb.toFixed(0)}</span>
                    </div>
                    <Bar value={used} max={d.totalGb} color={low ? 'linear-gradient(90deg,#ffb547,#ff4d6a)' : undefined} />
                  </div>
                )
              })}
            </div>
          )}
          {st && st.largestApps.length > 0 && (
            <>
              <div className="eyebrow" style={{ marginTop: 20 }}>Largest installed apps</div>
              {st.largestApps.slice(0, 6).map((a) => (
                <div key={a.name} className="row list-row">
                  <span className="ellipsis">{a.name}</span><span className="num muted">{a.sizeGb.toFixed(1)} GB</span>
                </div>
              ))}
            </>
          )}
        </Card>

        <Card delay={0.05}>
          <Eyebrow dot="var(--mint)"><Sparkles size={13} aria-hidden /> Quick cleanup</Eyebrow>
          <div className="clean-item">
            <div>
              <div style={{ fontWeight: 600 }}>Temporary files</div>
              <div className="body" style={{ fontSize: 12.5 }}>{temp ? `${temp.sizeGb.toFixed(2)} GB in ${temp.files.toLocaleString()} files older than a day. Files in use are skipped.` : 'Scanning…'}</div>
            </div>
            {confirmTemp
              ? <div className="row" style={{ gap: 8 }}><button className="btn primary" onClick={() => { setConfirmTemp(false); act('tempClean') }}>Delete them</button><button className="btn" onClick={() => setConfirmTemp(false)}>Cancel</button></div>
              : <button className="btn" disabled={!temp || temp.files === 0} onClick={() => setConfirmTemp(true)}><Trash2 size={14} aria-hidden /> Clean</button>}
          </div>
          <div className="clean-item">
            <div>
              <div style={{ fontWeight: 600 }}>Recycle Bin</div>
              <div className="body" style={{ fontSize: 12.5 }}>{temp ? (temp.recycleBinItems ? `${temp.recycleBinGb.toFixed(2)} GB in ${temp.recycleBinItems} items. Emptying is permanent; you'll be asked to confirm.` : 'Empty.') : '…'}</div>
            </div>
            <button className="btn" disabled={!temp || temp.recycleBinItems === 0} onClick={() => act('emptyRecycleBin')}><Trash2 size={14} aria-hidden /> Empty</button>
          </div>
          {msg && <div className="body" role="status" aria-live="polite" style={{ color: 'var(--cyan)', fontSize: 13, marginTop: 4 }}>{msg}</div>}
        </Card>
      </div>

      <Card delay={0.1}>
        <div className="row" style={{ justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
          <Eyebrow dot="var(--violet)"><Search size={13} aria-hidden /> Biggest files</Eyebrow>
          <button className="btn" onClick={scan} disabled={scanning} style={{ marginBottom: 12 }}>{scanning ? 'Scanning your drives…' : files ? 'Scan again' : 'Find big files'}</button>
        </div>
        {!files && !scanning && <div className="body">Looks through your user folders and other drives (not Windows or program folders) for files over 200 MB, so you can decide what to keep. Guardian X never deletes these; "Show" opens the file's folder.</div>}
        {scanning && <div className="bar" style={{ marginTop: 6 }}><motion.i initial={{ width: '5%' }} animate={{ width: '92%' }} transition={{ duration: 18, ease: 'easeOut' }} /></div>}
        {files && !scanning && (files.length === 0 ? <div className="body">No files over 200 MB found.</div> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>File</th><th>Size</th><th>Modified</th><th></th></tr></thead>
              <tbody>
                {files.map((f) => (
                  <tr key={f.path}>
                    <td><div className="ellipsis" title={f.path}>{f.name}</div><div className="num ellipsis" style={{ fontSize: 10.5, color: 'var(--faint)' }}>{f.path}</div></td>
                    <td className="num" style={{ whiteSpace: 'nowrap' }}>{f.sizeGb.toFixed(2)} GB</td>
                    <td className="num muted" style={{ whiteSpace: 'nowrap' }}>{shortDate(f.modified)}</td>
                    <td style={{ textAlign: 'right' }}><button className="btn small" onClick={() => call('showFile', { path: f.path }).catch((e) => setMsg(e.message))}><FolderOpen size={13} aria-hidden /> Show</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
      </Card>

      <Card delay={0.14}>
        <Eyebrow dot="var(--cyan)"><Power size={13} aria-hidden /> Start-up apps</Eyebrow>
        <div className="body" style={{ fontSize: 12.5, marginBottom: 10 }}>Apps that start with Windows run in the background and use battery and memory. Switching one off here only stops it starting by itself; it isn't uninstalled.</div>
        {!startup ? <div className="body">Reading…</div> : (
          <div className="grid startup-grid">
            {startup.map((a) => (
              <div key={a.id} className="startup-item">
                {a.canChange
                  ? <Toggle on={a.enabled} onChange={async (v) => setStartup(await call<StartupApp[]>('startupSet', { id: a.id, enabled: v }))} label={<span className="ellipsis" title={a.command}>{a.name}</span>} hint={a.enabled ? 'Starts with Windows' : 'Off at start-up'} />
                  : <div className="toggle" aria-disabled="true" title="Starts for all users. Change it in Task Manager > Startup apps (needs administrator)."><div><div className="ellipsis">{a.name}</div><div className="body" style={{ fontSize: 12.5 }}>{a.enabled ? 'On' : 'Off'} · all users (Task Manager)</div></div></div>}
              </div>
            ))}
          </div>
        )}
      </Card>
    </>
  )
}
