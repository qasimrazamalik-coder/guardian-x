import { AnimatePresence, motion } from 'framer-motion'
import { Activity, AppWindow, BarChart3, BatteryCharging, Bot, HeartPulse, Settings as Cog } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { Logo } from './components/Visuals'
import { call, inHost, on } from './lib/bridge'
import type { Dashboard } from './lib/types'
import { Analytics } from './pages/Analytics'
import { Apps } from './pages/Apps'
import { Assistant } from './pages/Assistant'
import { Charging } from './pages/Charging'
import { Health } from './pages/Health'
import { Overview } from './pages/Overview'
import { SettingsPage } from './pages/SettingsPage'

const pages = [
  { id: 'overview', label: 'Overview', icon: Activity, sub: 'Live battery, stress and what to do' },
  { id: 'health', label: 'Health', icon: HeartPulse, sub: 'Capacity history since the battery was new' },
  { id: 'analytics', label: 'Analytics', icon: BarChart3, sub: 'Charging habits, usage patterns, app energy' },
  { id: 'apps', label: 'Apps & storage', icon: AppWindow, sub: 'RAM, CPU, disk and battery by app' },
  { id: 'charging', label: 'Charging', icon: BatteryCharging, sub: 'Modes, 80% limit and smart plug' },
  { id: 'assistant', label: 'Assistant', icon: Bot, sub: 'Answers from your own battery data, on this PC' },
  { id: 'settings', label: 'Settings', icon: Cog, sub: 'Alerts, sounds, data and maintenance' },
]

export default function App() {
  const initial = new URLSearchParams(location.search).get('page') ?? 'overview'
  const [page, setPage] = useState(initial)
  const [d, setD] = useState<Dashboard | null>(null)
  const refresh = useCallback(() => { call<Dashboard>('dashboard').then(setD) }, [])
  useEffect(() => {
    refresh()
    const off = on('update', refresh)
    const off2 = on('navigate', (p: string) => setPage(p))
    const t = setInterval(refresh, 30000)
    return () => { off(); off2(); clearInterval(t) }
  }, [refresh])

  const meta = pages.find((p) => p.id === page)!
  const b = d?.battery
  return (
    <>
      <div className="field" />
      <div className="shell">
        <nav className="rail">
          <div className="brand"><Logo /><div><h1>Guardian X</h1><small>BATTERY COACH · v{d?.version ?? '2'}</small></div></div>
          {pages.map((p) => (
            <button key={p.id} className={`nav ${page === p.id ? 'active' : ''}`} onClick={() => setPage(p.id)} aria-current={page === p.id ? 'page' : undefined}>
              {page === p.id && <motion.div layoutId="navpill" className="pill" transition={{ type: 'spring', stiffness: 420, damping: 36 }} />}
              <p.icon size={18} aria-hidden /> {p.label}
            </button>
          ))}
          <div className="rail-foot">
            {b?.model}<br />{b ? `${b.manufacturer} ${b.chemistry} · ${b.designWh.toFixed(1)} Wh new` : ''}<br />{inHost ? 'data stays on this PC' : 'preview data'}
          </div>
        </nav>
        <main className="main">
          <header className="topbar">
            <div>
              <h2>{meta.label}</h2>
              <div className="sub">{meta.sub}</div>
            </div>
            <div className="spacer" />
            {b && (
              <>
                <span className="chip live" style={{ color: b.onAc ? 'var(--cyan)' : 'var(--amber)' }}>{b.onAc ? (b.charging ? 'CHARGING' : 'ON CHARGER') : 'ON BATTERY'} · {Math.round(b.percent)}%</span>
                <span className="chip" style={{ color: 'var(--mint)' }}>{d!.mode.name.toUpperCase()} MODE</span>
              </>
            )}
          </header>
          <div className="page">
            <AnimatePresence mode="wait">
              <motion.div key={page} initial={{ opacity: 0, y: 10, filter: 'blur(4px)' }} animate={{ opacity: 1, y: 0, filter: 'blur(0px)' }}
                exit={{ opacity: 0, y: -6, filter: 'blur(4px)' }} transition={{ duration: 0.28, ease: [0.2, 0.8, 0.2, 1] }} style={{ height: page === 'assistant' ? '100%' : undefined }}>
                {d && page === 'overview' && <Overview d={d} go={setPage} />}
                {page === 'health' && <Health />}
                {page === 'analytics' && <Analytics />}
                {page === 'apps' && <Apps />}
                {d && page === 'charging' && <Charging d={d} refresh={refresh} />}
                {page === 'assistant' && <Assistant />}
                {page === 'settings' && <SettingsPage version={d?.version ?? ''} />}
              </motion.div>
            </AnimatePresence>
          </div>
        </main>
      </div>
    </>
  )
}
