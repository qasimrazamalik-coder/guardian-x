import { AnimatePresence, motion } from 'framer-motion'
import { HardDrive, Lightbulb, MemoryStick } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Bar, Card, Counter, Eyebrow, Seg, useTicker } from '../components/ui'
import { call } from '../lib/bridge'
import type { AppsReport, Storage } from '../lib/types'

type SortKey = 'cpu' | 'ram' | 'disk' | 'power'

export function Apps() {
  const tick = useTicker(3000)
  const [r, setR] = useState<AppsReport | null>(null)
  const [st, setSt] = useState<Storage | null>(null)
  const [sort, setSort] = useState<SortKey>('ram')
  const [showSystem, setShowSystem] = useState(false)
  useEffect(() => { call<AppsReport>('apps').then(setR) }, [tick])
  useEffect(() => { call<Storage>('storage').then(setSt) }, [])
  if (!r) return null
  const key = (a: AppsReport['apps'][0]) => sort === 'cpu' ? a.cpuPercent : sort === 'ram' ? a.memoryMb : sort === 'disk' ? a.diskMbPerSec : a.estimatedWatts ?? 0
  const list = r.apps.filter((a) => showSystem || a.kind !== 'system').sort((x, y) => key(y) - key(x)).slice(0, 14)
  const maxRam = Math.max(...list.map((a) => a.memoryMb), 1)
  return (
    <div className="grid">
      <div className="grid" style={{ gridTemplateColumns: '1fr 1fr 1.4fr' }}>
        <Card glow>
          <Eyebrow dot="var(--violet)"><MemoryStick size={13} /> Memory</Eyebrow>
          <div className="big"><Counter value={r.usedRamGb} decimals={1} /><span className="muted" style={{ fontSize: 16 }}> / {r.totalRamGb.toFixed(0)} GB</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.usedRamGb} max={r.totalRamGb} color="linear-gradient(90deg,#9b8cff,#3de0ff)" /></div>
        </Card>
        <Card delay={0.04}>
          <Eyebrow dot="var(--cyan)">CPU</Eyebrow>
          <div className="big"><Counter value={r.cpuTotal} /><span className="muted" style={{ fontSize: 16 }}> %</span></div>
          <div style={{ marginTop: 14 }}><Bar value={r.cpuTotal} /></div>
        </Card>
        <Card delay={0.08}>
          <Eyebrow><Lightbulb size={13} /> How to save</Eyebrow>
          {r.tips.length === 0 ? <div className="body">Nothing heavy right now.</div> : r.tips.slice(0, 2).map((t) => <div key={t.app} className="body" style={{ marginBottom: 8, fontSize: 13 }}>{t.text}</div>)}
        </Card>
      </div>

      <Card delay={0.12}>
        <div className="row" style={{ justifyContent: 'space-between', marginBottom: 6 }}>
          <Eyebrow dot="var(--mint)">Apps · live {r.onBattery ? '· on battery, power estimated' : ''}</Eyebrow>
          <div className="row" style={{ gap: 12, marginBottom: 12 }}>
            <button className="chip" style={{ color: showSystem ? 'var(--cyan)' : 'var(--muted)' }} aria-pressed={showSystem} onClick={() => setShowSystem(!showSystem)}>{showSystem ? 'hide' : 'show'} Windows processes</button>
            <Seg layoutId="sort" value={sort} onChange={setSort} options={[{ value: 'ram', label: 'RAM' }, { value: 'cpu', label: 'CPU' }, { value: 'disk', label: 'Disk' }, { value: 'power', label: 'Battery' }]} />
          </div>
        </div>
        <table className="table">
          <thead><tr><th style={{ width: '30%' }}>App</th><th style={{ width: '26%' }}>RAM</th><th>CPU</th><th>Disk</th><th>Battery</th><th>Proc.</th></tr></thead>
          <tbody>
            <AnimatePresence initial={false}>
              {list.map((a) => (
                <motion.tr key={a.key} layout transition={{ type: 'spring', stiffness: 400, damping: 40 }} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}>
                  <td><div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{ width: 8, height: 8, borderRadius: 3, background: a.kind === 'browser' ? 'var(--cyan)' : a.kind === 'system' ? 'var(--faint)' : 'var(--mint)' }} />
                    <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 240 }}>{a.name}</span></div></td>
                  <td><div style={{ display: 'grid', gridTemplateColumns: '1fr 74px', gap: 10, alignItems: 'center' }}>
                    <Bar value={a.memoryMb} max={maxRam} color="linear-gradient(90deg,#9b8cff,#3de0ff)" />
                    <span className="num">{a.memoryMb >= 1024 ? `${(a.memoryMb / 1024).toFixed(1)} GB` : `${Math.round(a.memoryMb)} MB`}</span></div></td>
                  <td className="num" style={{ color: a.cpuPercent >= 15 ? 'var(--amber)' : undefined }}>{a.cpuPercent.toFixed(1)}%</td>
                  <td className="num">{a.diskMbPerSec.toFixed(1)} MB/s</td>
                  <td className="num">{a.estimatedWatts != null ? `~${a.estimatedWatts.toFixed(1)} W` : <span className="muted">on charger</span>}</td>
                  <td className="num muted">{a.processes}</td>
                </motion.tr>
              ))}
            </AnimatePresence>
          </tbody>
        </table>
      </Card>

      {st && (
        <Card delay={0.16}>
          <Eyebrow dot="var(--amber)"><HardDrive size={13} /> Storage</Eyebrow>
          <div className="grid" style={{ gridTemplateColumns: '1fr 1fr', gap: 30 }}>
            <div className="grid" style={{ gap: 14 }}>
              {st.drives.map((d) => {
                const used = d.totalGb - d.freeGb
                const low = d.freeGb / d.totalGb < 0.1
                return (
                  <div key={d.name}>
                    <div className="row" style={{ justifyContent: 'space-between', marginBottom: 6 }}>
                      <span>{d.name} {d.label}</span>
                      <span className="num" style={{ color: low ? 'var(--red)' : 'var(--muted)' }}>{d.freeGb.toFixed(1)} GB free of {d.totalGb.toFixed(0)}</span>
                    </div>
                    <Bar value={used} max={d.totalGb} color={low ? 'linear-gradient(90deg,#ffb547,#ff4d6a)' : undefined} />
                  </div>
                )
              })}
              <div className="body" style={{ fontSize: 12.5 }}>Temp files: {st.tempGb.toFixed(2)} GB. Settings › System › Storage › Storage Sense can clear them automatically.</div>
            </div>
            <div>
              <div className="eyebrow">Largest installed apps</div>
              {st.largestApps.map((a) => (
                <div key={a.name} className="row" style={{ justifyContent: 'space-between', padding: '8px 0', borderTop: '1px solid var(--line)' }}>
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 300 }}>{a.name}</span>
                  <span className="num muted">{a.sizeGb.toFixed(1)} GB</span>
                </div>
              ))}
            </div>
          </div>
        </Card>
      )}
    </div>
  )
}
