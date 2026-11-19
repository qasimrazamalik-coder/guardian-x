import { AnimatePresence, motion } from 'framer-motion'
import { Cpu, FolderOpen, HardDrive, Leaf, Lightbulb, MemoryStick, Power, Search, Sparkles, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Bar, Card, Counter, Loading, Seg, Skel, SkelCard, Toggle, useTicker } from '../components/ui'
import { call } from '../lib/bridge'
import { shortDate } from '../lib/format'
import type { AppsReport, BigFile, StartupApp, Storage, TempSummary } from '../lib/types'

type SortKey = 'cpu' | 'ram' | 'disk' | 'power'

export function Apps() {
  const [tab, setTab] = useState<'apps' | 'storage'>('apps')
  return (
    <div className="grid">
      <div><Seg layoutId="apps-tab" label="View" value={tab} onChange={setTab} options={[{ value: 'apps', label: 'Apps' }, { value: 'storage', label: 'Storage & cleanup' }]} /></div>
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
  if (!r) return (
    <>
      <Loading what="apps" />
      <div className="grid apps-top"><SkelCard lines={2} /><SkelCard lines={2} /><SkelCard lines={3} /></div>
      <SkelCard chart={360} lines={1} />
    </>
  )
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
        <Card title="Memory" icon={<MemoryStick size={15} aria-hidden />}>
          <div className="big"><Counter value={r.usedRamGb} decimals={1} /><span className="unit">of {r.totalRamGb.toFixed(0)} GB</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.usedRamGb} max={r.totalRamGb} label={`${r.usedRamGb.toFixed(1)} of ${r.totalRamGb.toFixed(0)} GB used`} /></div>
        </Card>
        <Card title="CPU" icon={<Cpu size={15} aria-hidden />}>
          <div className="big"><Counter value={r.cpuTotal} /><span className="unit">%</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.cpuTotal} label={`CPU ${r.cpuTotal} percent`} /></div>
        </Card>
        <Card title="How to save" icon={<Lightbulb size={15} aria-hidden />}>
          {r.tips.length === 0 ? <p className="small muted">Nothing heavy right now.</p> : r.tips.slice(0, 2).map((t) => <p key={t.app} className="small muted" style={{ marginBottom: 8 }}>{t.text}</p>)}
        </Card>
      </div>

      <Card hero title={<>Apps, live{r.onBattery ? <span className="card-meta" style={{ fontWeight: 400 }}>on battery, power estimated</span> : null}</>} mark="var(--good)"
        meta={<>
          <button className="chip" aria-pressed={showSystem} onClick={() => setShowSystem(!showSystem)} style={{ color: showSystem ? 'var(--accent)' : undefined }}><span>{showSystem ? 'Hide' : 'Show'} Windows processes</span></button>
          <Seg layoutId="sort" label="Sort by" value={sort} onChange={setSort} options={[{ value: 'ram', label: 'RAM' }, { value: 'cpu', label: 'CPU' }, { value: 'disk', label: 'Disk' }, { value: 'power', label: 'Battery' }]} />
        </>}>
        <div className="table-wrap">
          <table className="table apps-table">
            <thead><tr><th>App</th><th>RAM</th><th className="r">CPU</th><th className="r">Disk</th><th className="r">Battery</th><th className="r">Processes</th><th title="Windows Efficiency mode">Efficiency</th></tr></thead>
            <tbody>
              <AnimatePresence initial={false}>
                {list.map((a) => (
                  <motion.tr key={a.key} layout transition={{ type: 'spring', stiffness: 400, damping: 40 }} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}>
                    <td><div className="app-name">
                      <span className="kind" aria-hidden style={{ background: a.kind === 'browser' ? 'var(--accent)' : a.kind === 'system' ? 'var(--ink-3)' : 'var(--s-battery)' }} />
                      <span className="ellipsis" title={a.name}>{a.name}</span></div></td>
                    <td><div className="ram-cell">
                      <Bar value={a.memoryMb} max={maxRam} />
                      <span className="num small" style={{ textAlign: 'right' }}>{a.memoryMb >= 1024 ? `${(a.memoryMb / 1024).toFixed(1)} GB` : `${Math.round(a.memoryMb)} MB`}</span></div></td>
                    <td className="num r" style={{ color: a.cpuPercent >= 15 ? 'var(--warn)' : undefined }}>{a.cpuPercent.toFixed(1)}%</td>
                    <td className="num r" style={{ whiteSpace: 'nowrap' }}>{a.diskMbPerSec.toFixed(1)} MB/s</td>
                    <td className="num r">{a.estimatedWatts != null ? `~${a.estimatedWatts.toFixed(1)} W` : <span className="muted" title="Battery use is estimated only while you're on battery">—</span>}</td>
                    <td className="num muted r">{a.processes}</td>
                    <td>{a.canThrottle
                      ? <button className="leaf" aria-pressed={a.efficiency} title={a.efficiency ? 'Efficiency mode on. Click to turn off.' : 'Run this app in Windows Efficiency mode'}
                          onClick={() => toggle(a.key, !a.efficiency)}><Leaf size={14} aria-hidden /><span className="sr-only">Efficiency mode for {a.name}</span></button>
                      : <span className="muted" title="Windows processes are left alone">—</span>}</td>
                  </motion.tr>
                ))}
              </AnimatePresence>
            </tbody>
          </table>
        </div>
        <div className="small muted" role="status" aria-live="polite" style={{ marginTop: 12 }}>
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
  const refresh = () => { call<Storage>('storage').then(setSt).catch((e) => setMsg(e.message)); call<TempSummary>('tempScan').then(setTemp).catch((e) => setMsg(e.message)) }
  useEffect(() => { refresh(); call<StartupApp[]>('startupList').then(setStartup) }, [])
  const act = async (method: string) => { setMsg('Working…'); try { const r = await call<{ message?: string }>(method); setMsg(r?.message ?? 'Done.') } catch (e: any) { setMsg(e.message) } refresh() }
  const scan = async () => { setScanning(true); try { setFiles(await call<BigFile[]>('bigFiles')) } finally { setScanning(false) } }

  return (
    <>
      <div className="grid split-even">
        <Card hero title="Drives" icon={<HardDrive size={15} aria-hidden />} meta={!st ? <span className="card-meta" role="status">Reading drives, this can take a few seconds…</span> : undefined}>
          {!st ? (
            <div className="grid" style={{ gap: 18 }} aria-hidden>
              {Array.from({ length: 4 }, (_, i) => <div key={i} className="drive"><div className="row between"><Skel h={13} w="30%" /><Skel h={13} w="34%" /></div><Skel h={6} /></div>)}
              <Skel h={13} w="40%" /><Skel h={12} /><Skel h={12} /><Skel h={12} />
            </div>
          ) : (
            <div className="grid" style={{ gap: 14 }}>
              {st.drives.map((d) => {
                const used = d.totalGb - d.freeGb
                const low = d.freeGb / d.totalGb < 0.1
                return (
                  <div key={d.name} className="drive">
                    <div className="row between" style={{ gap: 10 }}>
                      <span className="ellipsis"><b>{d.name}</b> {d.label}</span>
                      <span className="num small" style={{ color: low ? 'var(--bad)' : 'var(--ink-2)', whiteSpace: 'nowrap' }}>{d.freeGb.toFixed(1)} GB free of {d.totalGb.toFixed(0)}</span>
                    </div>
                    <Bar value={used} max={d.totalGb} color={low ? 'var(--bad)' : undefined} label={`${d.name} ${Math.round((used / d.totalGb) * 100)} percent full`} />
                  </div>
                )
              })}
              {st.largestApps.length > 0 && (
                <div style={{ marginTop: 6 }}>
                  <h4 className="label" style={{ fontWeight: 600, marginBottom: 4 }}>Largest installed apps</h4>
                  {st.largestApps.slice(0, 6).map((a) => (
                    <div key={a.name} className="row list-row">
                      <span className="ellipsis">{a.name}</span><span className="num muted small" style={{ whiteSpace: 'nowrap' }}>{a.sizeGb.toFixed(1)} GB</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </Card>

        <Card title="Quick cleanup" icon={<Sparkles size={15} aria-hidden />}>
          <div className="clean-item">
            <div>
              <div style={{ fontWeight: 600 }}>Temporary files</div>
              {temp ? <div className="small muted">{temp.sizeGb.toFixed(2)} GB in {temp.files.toLocaleString()} files older than a day. Files in use are skipped.</div>
                : <div className="grid" style={{ gap: 6, marginTop: 6 }} aria-hidden><Skel h={12} w="90%" /><Skel h={12} w="60%" /></div>}
            </div>
            {confirmTemp
              ? <div className="row wrap" style={{ gap: 8 }}><button className="btn primary" onClick={() => { setConfirmTemp(false); act('tempClean') }}>Delete them</button><button className="btn" onClick={() => setConfirmTemp(false)}>Cancel</button></div>
              : <button className="btn" disabled={!temp || temp.files === 0} onClick={() => setConfirmTemp(true)}><Trash2 size={14} aria-hidden /> Clean</button>}
          </div>
          <div className="clean-item">
            <div>
              <div style={{ fontWeight: 600 }}>Recycle Bin</div>
              {temp ? <div className="small muted">{temp.recycleBinItems ? `${temp.recycleBinGb.toFixed(2)} GB in ${temp.recycleBinItems} items. Emptying is permanent; you'll be asked to confirm.` : 'Empty.'}</div>
                : <div className="grid" style={{ gap: 6, marginTop: 6 }} aria-hidden><Skel h={12} w="80%" /></div>}
            </div>
            <button className="btn" disabled={!temp || temp.recycleBinItems === 0} onClick={() => act('emptyRecycleBin')}><Trash2 size={14} aria-hidden /> Empty</button>
          </div>
          {!temp && <div className="small faint" role="status">Measuring temporary files…</div>}
          <div className="status-line" role="status" aria-live="polite">{msg}</div>
        </Card>
      </div>

      <Card title="Biggest files" icon={<Search size={15} aria-hidden />}
        meta={<button className="btn small" onClick={scan} disabled={scanning}>{scanning ? 'Scanning your drives…' : files ? 'Scan again' : 'Find big files'}</button>}>
        {!files && !scanning && <p className="small muted">Looks through your user folders and other drives (not Windows or program folders) for files over 200 MB, so you can decide what to keep. Guardian X never deletes these; "Show" opens the file's folder.</p>}
        {scanning && (
          <div className="grid" style={{ gap: 10 }} role="status" aria-label="Scanning for big files">
            {Array.from({ length: 3 }, (_, i) => <div key={i} className="row between" aria-hidden><Skel h={13} w="55%" /><Skel h={13} w="12%" /></div>)}
          </div>
        )}
        {files && !scanning && (files.length === 0 ? <p className="small muted">No files over 200 MB found.</p> : (
          <div className="table-wrap">
            <table className="table" style={{ tableLayout: 'fixed', minWidth: 480 }}>
              <colgroup><col /><col style={{ width: 90 }} /><col style={{ width: 90 }} /><col style={{ width: 96 }} /></colgroup>
              <thead><tr><th>File</th><th className="r">Size</th><th>Modified</th><th><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {files.map((f) => (
                  <tr key={f.path}>
                    <td><div className="ellipsis" title={f.path}>{f.name}</div><div className="num ellipsis xs faint">{f.path}</div></td>
                    <td className="num r" style={{ whiteSpace: 'nowrap' }}>{f.sizeGb.toFixed(2)} GB</td>
                    <td className="num muted" style={{ whiteSpace: 'nowrap' }}>{shortDate(f.modified)}</td>
                    <td style={{ textAlign: 'right' }}><button className="btn small" aria-label={`Show ${f.name} in its folder`} onClick={() => call('showFile', { path: f.path }).catch((e) => setMsg(e.message))}><FolderOpen size={13} aria-hidden /> Show</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
      </Card>

      <Card title="Start-up apps" icon={<Power size={15} aria-hidden />}>
        <p className="small muted" style={{ marginBottom: 6 }}>Apps that start with Windows run in the background and use battery and memory. Switching one off here only stops it starting by itself; it isn't uninstalled.</p>
        {!startup ? <div className="grid" style={{ gap: 10 }} aria-hidden>{Array.from({ length: 3 }, (_, i) => <Skel key={i} h={36} />)}</div> : (
          <div className="grid startup-grid">
            {startup.map((a) => (
              <div key={a.id} className="startup-item">
                {a.canChange
                  ? <Toggle on={a.enabled} onChange={async (v) => setStartup(await call<StartupApp[]>('startupSet', { id: a.id, enabled: v }))} label={<span className="ellipsis" title={a.command} style={{ display: 'block' }}>{a.name}</span>} hint={a.enabled ? 'Starts with Windows' : 'Off at start-up'} />
                  : <div className="toggle" aria-disabled="true" title="Starts for all users. Change it in Task Manager > Startup apps (needs administrator)."><span className="t-text"><span className="t-label ellipsis" style={{ display: 'block' }}>{a.name}</span><span className="t-hint" style={{ display: 'block' }}>{a.enabled ? 'On' : 'Off'}, for all users (change in Task Manager)</span></span></div>}
              </div>
            ))}
          </div>
        )}
      </Card>
    </>
  )
}
