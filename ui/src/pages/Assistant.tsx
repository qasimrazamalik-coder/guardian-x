import { ArrowRight, BookOpen, Bot, Check, Cpu, Link2, Send, ShieldCheck, Sparkles, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Card } from '../components/ui'
import { call, on } from '../lib/bridge'
import type { AssistantStatus, ChatAction, McpInfo } from '../lib/types'

interface Msg { role: 'user' | 'bot'; text: string; sources?: string[]; followUps?: string[]; actions?: ChatAction[]; done?: boolean }

const suggestions = [
  'How is my battery doing?',
  "Why shouldn't I keep it plugged in all the time?",
  'When will my battery reach 80% health?',
  'Which apps are draining my battery?',
  'What caused my biggest battery drop?',
  'Battery jaldi kyun khatam hoti hai?',
]

export function Assistant({ go, refresh }: { go: (p: string) => void; refresh: () => void }) {
  const [status, setStatus] = useState<AssistantStatus | null>(null)
  const [mcp, setMcp] = useState<McpInfo | null>(null)
  const [msgs, setMsgs] = useState<Msg[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<Record<string, string>>({})
  const scroller = useRef<HTMLDivElement>(null)
  const current = useRef<number>(0)

  const load = () => { call<AssistantStatus>('assistantStatus').then(setStatus); call<McpInfo>('mcpInfo').then(setMcp) }
  useEffect(() => {
    load()
    return on('chat', (e) => {
      if (e.id !== current.current) return
      setMsgs((m) => {
        const copy = [...m]
        const last = { ...copy[copy.length - 1] }
        if (e.delta) last.text += e.delta
        if (e.error) { last.text += (last.text ? '\n\n' : '') + "That didn't work: " + e.error; last.done = true; setBusy(false) }
        if (e.done) { last.done = true; last.sources = e.sources; last.followUps = e.followUps; last.actions = Array.isArray(e.actions) ? e.actions : undefined; setBusy(false) }
        copy[copy.length - 1] = last
        return copy
      })
    })
  }, [])
  useEffect(() => {
    const el = scroller.current
    if (!el) return
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    el.scrollTo({ top: el.scrollHeight, behavior: reduce ? 'auto' : 'smooth' })
  }, [msgs])

  const ask = async (q: string) => {
    if (!q.trim() || busy) return
    const id = Date.now()
    current.current = id
    setMsgs((m) => [...m, { role: 'user', text: q.trim() }, { role: 'bot', text: '' }])
    setInput('')
    setBusy(true)
    try { await call('ask', { id, question: q.trim() }) } catch (e: any) {
      setMsgs((m) => { const c = [...m]; c[c.length - 1] = { role: 'bot', text: "That didn't work: " + e.message, done: true }; return c }); setBusy(false)
    }
  }
  const runAction = async (a: ChatAction, key: string) => {
    if (a.navigate) { go(a.navigate); return }
    if (!a.method) return
    setDone((x) => ({ ...x, [key]: 'Working…' }))
    try {
      const r = await call<{ ok?: boolean; message?: string }>(a.method, a.params ?? {})
      setDone((x) => ({ ...x, [key]: r?.message || 'Done' }))
      refresh()
    } catch (e: any) { setDone((x) => ({ ...x, [key]: e.message })) }
  }
  const lastBot = [...msgs].reverse().find((m) => m.role === 'bot' && m.done)

  return (
    <div className="grid assistant-grid">
      <Card hero className="chat-card" title="Ask Guardian X" icon={<Bot size={15} aria-hidden />}
        meta={msgs.length > 0 ? <button className="btn small" onClick={() => { setMsgs([]); setDone({}); call('assistantReset') }}><Trash2 size={13} aria-hidden /> New chat</button> : <span className="card-meta">Runs on this PC</span>}>
        <div ref={scroller} className="chat" role="log" aria-live="polite" aria-label="Conversation">
          {msgs.length === 0 && (
            <div className="chat-empty">
              <h3>Ask about your battery</h3>
              <p className="body" style={{ margin: '6px 0 18px' }}>Answers come from this laptop's battery data and Guardian X's built-in guides. Instant, offline, and nothing leaves this PC. English and Roman Urdu both work.</p>
              <div className="suggestions">
                {suggestions.map((s) => <button key={s} className="btn" onClick={() => ask(s)}>{s}</button>)}
              </div>
            </div>
          )}
          {msgs.map((m, i) => (
            <div key={i} className={`msg ${m.role}`}>
              {m.text}{m.role === 'bot' && !m.done && <span className="caret" aria-hidden />}
              {m.role === 'bot' && m.done && m.actions && m.actions.length > 0 && (
                <div className="msg-actions">
                  {m.actions.map((a, k) => {
                    const key = `${i}-${k}`
                    return (
                      <span key={key} className="row" style={{ gap: 8 }}>
                        <button className="btn small action" onClick={() => runAction(a, key)}>
                          {a.navigate ? <ArrowRight size={13} aria-hidden /> : done[key] && done[key] !== 'Working…' ? <Check size={13} aria-hidden /> : <Sparkles size={13} aria-hidden />}
                          {a.label}
                        </button>
                      </span>
                    )
                  })}
                </div>
              )}
              {m.role === 'bot' && Object.entries(done).filter(([k]) => k.startsWith(`${i}-`)).map(([k, v]) => <div key={k} className="small accent" style={{ marginTop: 6 }} role="status">{v}</div>)}
              {m.sources && m.sources.length > 0 && (
                <div className="sources">
                  {m.sources.map((s, k) => <span key={k} className="source"><BookOpen size={11} aria-hidden /> {s}</span>)}
                </div>
              )}
            </div>
          ))}
          {!busy && lastBot?.followUps && lastBot.followUps.length > 0 && msgs[msgs.length - 1] === lastBot && (
            <div className="suggestions" aria-label="Follow-up questions">
              {lastBot.followUps.map((f) => <button key={f} className="chip" onClick={() => ask(f)}><span>{f}</span></button>)}
            </div>
          )}
        </div>
        <form onSubmit={(e) => { e.preventDefault(); ask(input) }} className="composer">
          <label htmlFor="ask" className="sr-only">Ask Guardian X</label>
          <input id="ask" className="input" placeholder="Ask about battery health, charging, apps, storage, sleep…" value={input} onChange={(e) => setInput(e.target.value)} maxLength={300} />
          <button className="btn primary" disabled={busy || !input.trim()} aria-label="Send"><Send size={15} aria-hidden /></button>
        </form>
      </Card>

      <div className="grid side-col">
        <Card title="How it works" icon={<Cpu size={15} aria-hidden />}>
          <div className="grid" style={{ gap: 8 }}>
            <Row icon={<ShieldCheck size={14} />} k="Runs on" v="this PC" />
            <Row icon={<BookOpen size={14} />} k="Knowledge" v={status ? `${status.sections} sections` : '…'} />
            <Row icon={<Sparkles size={14} />} k="Download" v="none" />
          </div>
          <p className="small muted" style={{ marginTop: 12 }}>
            It works out what you're asking, answers from your live readings and history, and backs the answer with the matching section of its battery guides. Buttons under an answer can switch a mode or open the right page. No AI model and no internet.
          </p>
        </Card>
        <Card title="MCP server" icon={<Link2 size={15} aria-hidden />}>
          <p className="small muted">Lets Claude read your Guardian X data (battery, forecast, history, apps, storage, modes) and ask this assistant, through read-only tools.</p>
          <button className="btn" style={{ marginTop: 12, width: '100%' }} disabled={mcp?.connected}
            onClick={async () => { await call('mcpConnect'); load() }}>{mcp?.connected ? 'Connected to Claude Desktop' : 'Connect to Claude Desktop'}</button>
          <div className="num path">{mcp?.exePath}</div>
          {mcp?.connected && <p className="small muted" style={{ marginTop: 8 }}>Restart Claude Desktop to load it.</p>}
        </Card>
      </div>
    </div>
  )
}

function Row({ icon, k, v }: { icon: React.ReactNode; k: string; v: string }) {
  return (
    <div className="row between" style={{ padding: '6px 0', borderTop: '1px solid var(--line)' }}>
      <span className="row" style={{ gap: 8 }}><span style={{ color: 'var(--ink-3)', display: 'inline-flex' }} aria-hidden>{icon}</span>{k}</span>
      <span className="num small">{v}</span>
    </div>
  )
}
