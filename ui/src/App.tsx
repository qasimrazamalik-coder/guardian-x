import { AnimatePresence, MotionConfig, motion } from 'framer-motion'
import { Activity, AppWindow, BarChart3, BatteryCharging, Bot, Gamepad2, HeartPulse, Monitor, Moon, Settings as Cog, Sun } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { SkelCard } from './components/ui'
import { Logo } from './components/Visuals'
import { call, inHost, on } from './lib/bridge'
import { setTheme, useTheme, type ThemeChoice } from './lib/theme'
import type { Dashboard, Settings } from './lib/types'
import { Analytics } from './pages/Analytics'
import { Apps } from './pages/Apps'
import { Assistant } from './pages/Assistant'
import { Charging } from './pages/Charging'
import { Health } from './pages/Health'
import { Overview } from './pages/Overview'
import { SettingsPage } from './pages/SettingsPage'

const pages = [
  { id: 'overview', label: 'Overview', icon: Activity, sub: 'Live charge, health and what to change today' },
  { id: 'health', label: 'Health', icon: HeartPulse, sub: 'Capacity since the battery was new, and where it is heading' },
  { id: 'analytics', label: 'Analytics', icon: BarChart3, sub: 'Charging habits, when you use the laptop, energy by app' },
  { id: 'apps', label: 'Apps & storage', icon: AppWindow, sub: 'Memory, CPU, disk and battery by app; space and cleanup' },
  { id: 'charging', label: 'Charging', icon: BatteryCharging, sub: 'Modes, the charge window and limits' },
  { id: 'assistant', label: 'Assistant', icon: Bot, sub: 'Answers from your own battery data, on this PC' },
  { id: 'settings', label: 'Settings', icon: Cog, sub: 'Alerts, sounds, appearance and maintenance' },
]
const valid = (p: unknown): p is string => typeof p === 'string' && pages.some((x) => x.id === p)
const themeNext: Record<ThemeChoice, ThemeChoice> = { system: 'light', light: 'dark', dark: 'system' }
const themeName: Record<ThemeChoice, string> = { system: 'System', light: 'Light', dark: 'Dark' }

export default function App() {
  const q = new URLSearchParams(location.search).get('page')
  const [page, setPage] = useState(valid(q) ? q : 'overview')
  const [d, setD] = useState<Dashboard | null>(null)
  const theme = useTheme()
  const refresh = useCallback(() => { call<Dashboard>('dashboard').then(setD).catch(() => {}) }, [])
  const go = useCallback((p: string) => { if (valid(p)) setPage(p) }, [])
  useEffect(() => {
    refresh()
    call<Settings>('settingsGet').then((s) => { if (s?.theme) setTheme(s.theme) }).catch(() => {})
    const offs = [
      on('update', refresh),
      on('navigate', (p: any) => go(typeof p === 'string' ? p : p?.page)),
      on('theme', (e: any) => setTheme(typeof e === 'string' ? e : e?.theme)),
    ]
    const t = setInterval(refresh, 30000)
    return () => { offs.forEach((f) => f()); clearInterval(t) }
  }, [refresh, go])

  const cycleTheme = () => {
    const n = themeNext[theme.choice]
    setTheme(n)
    call('settingsSave', { theme: n }).catch(() => {})
  }
  const ThemeIcon = theme.choice === 'system' ? Monitor : theme.choice === 'light' ? Sun : Moon

  const meta = pages.find((p) => p.id === page)!
  const b = d?.battery
  const gaming = !!(d?.gaming || d?.auto.gaming)
  return (
    <MotionConfig reducedMotion="user">
      <div className="shell">
        <nav className="rail" aria-label="Sections">
          <div className="brand"><Logo /><div><h1>Guardian X</h1><small>Battery coach {d?.version ? `v${d.version}` : ''}</small></div></div>
          {pages.map((p) => (
            <button key={p.id} className="nav" onClick={() => setPage(p.id)} aria-current={page === p.id ? 'page' : undefined} aria-label={p.label} title={p.label}>
              {page === p.id && <motion.span layoutId="navpill" className="pill" transition={{ type: 'spring', stiffness: 480, damping: 40 }} />}
              <span className="led" aria-hidden />
              <p.icon size={18} aria-hidden />
              <span className="nav-label">{p.label}</span>
            </button>
          ))}
          <div className="rail-foot">
            {b && <div className="model">{b.model}</div>}
            {b && <div>{b.manufacturer} {b.chemistry}, {b.designWh.toFixed(1)} Wh new</div>}
            <div>{inHost ? 'Data stays on this PC' : 'Preview data'}</div>
          </div>
        </nav>
        <main className="main">
          <header className="topbar">
            <div className="title">
              <h2>{meta.label}</h2>
              <div className="sub">{meta.sub}</div>
            </div>
            <div className="status" role="status" aria-live="polite">
              {b && (
                <span className="readchip live" style={{ color: b.onAc ? 'var(--s-charger)' : 'var(--s-battery)' }}>
                  <span className="mark" aria-hidden />
                  <span style={{ color: 'var(--ink-2)' }}>{b.onAc ? (b.charging ? 'Charging' : 'On charger') : 'On battery'}</span>
                  <span className="v">{Math.round(b.percent)}%</span>
                </span>
              )}
              {d && <span className="readchip"><span style={{ color: 'var(--ink-2)' }}>Mode</span><span className="v">{d.mode.name}</span></span>}
              {gaming && <span className="readchip" style={{ color: 'var(--s-proj)' }}><Gamepad2 size={14} aria-hidden /><span className="v" style={{ color: 'var(--ink)' }}>Game running</span></span>}
            </div>
            <button className="icon-btn" onClick={cycleTheme} aria-label={`Theme: ${themeName[theme.choice]}. Switch to ${themeName[themeNext[theme.choice]]}`} title={`Theme: ${themeName[theme.choice]} (click for ${themeName[themeNext[theme.choice]]})`}>
              <ThemeIcon size={16} aria-hidden />
            </button>
          </header>
          <div className="page" id="page">
            <AnimatePresence mode="wait" initial={false}>
              <motion.div key={page} className="page-inner" initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }}
                transition={{ duration: 0.18, ease: [0.2, 0.8, 0.2, 1] }}>
                {page === 'overview' && (d ? <Overview d={d} go={go} /> : <PageSkeleton />)}
                {page === 'health' && <Health />}
                {page === 'analytics' && <Analytics />}
                {page === 'apps' && <Apps />}
                {page === 'charging' && (d ? <Charging d={d} refresh={refresh} /> : <PageSkeleton />)}
                {page === 'assistant' && <Assistant go={go} refresh={refresh} />}
                {page === 'settings' && <SettingsPage version={d?.version ?? ''} />}
              </motion.div>
            </AnimatePresence>
          </div>
        </main>
      </div>
    </MotionConfig>
  )
}

function PageSkeleton() {
  return (
    <div className="grid ov">
      <div role="status" className="sr-only">Loading battery readings…</div>
      <SkelCard className="ov-health" chart={200} lines={2} />
      <SkelCard className="ov-live" chart={140} lines={3} />
      <SkelCard className="ov-day" chart={180} lines={0} />
      <SkelCard className="ov-heat" lines={4} />
    </div>
  )
}
