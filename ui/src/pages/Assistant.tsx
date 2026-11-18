import { motion } from 'framer-motion'
import { BookOpen, Bot, Cpu, Link2, Send, ShieldCheck, Sparkles, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Card, Eyebrow } from '../components/ui'
import { call, on } from '../lib/bridge'
import type { AssistantStatus, McpInfo } from '../lib/types'

interface Msg { role: 'user' | 'bot'; text: string; sources?: string[]; followUps?: string[]; done?: boolean }

const suggestions = [
  'How is my battery doing?',
  "Why shouldn't I keep it plugged in all the time?",
  'When will my battery reach 80% health?',
  'Which apps are draining my battery?',
  'What caused my biggest battery drop?',
  'How do I set up the 80% limit with a Tapo plug?',
]

export function Assistant() {
  const [status, setStatus] = useState<AssistantStatus | null>(null)
  const [mcp, setMcp] = useState<McpInfo | null>(null)
  const [msgs, setMsgs] = useState<Msg[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
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
        if (e.error) { last.text += (last.text ? '\n\n' : '') + "Sorry, that didn't work: " + e.error; last.done = true; setBusy(false) }
        if (e.done) { last.done = true; last.sources = e.sources; last.followUps = e.followUps; setBusy(false) }
        copy[copy.length - 1] = last
        return copy
      })
    })
  }, [])
  useEffect(() => { scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: 'smooth' }) }, [msgs])

  const ask = async (q: string) => {
    if (!q.trim() || busy) return
    const id = Date.now()
    current.current = id
    setMsgs((m) => [...m, { role: 'user', text: q.trim() }, { role: 'bot', text: '' }])
    setInput('')
    setBusy(true)
    try { await call('ask', { id, question: q.trim() }) } catch (e: any) {
      setMsgs((m) => { const c = [...m]; c[c.length - 1] = { role: 'bot', text: "Sorry, that didn't work: " + e.message, done: true }; return c }); setBusy(false)
    }
  }
  const lastBot = [...msgs].reverse().find((m) => m.role === 'bot' && m.done)

  return (
    <div className="grid assistant-grid">
      <Card glow className="chat-card">
        <div className="row" style={{ justifyContent: 'space-between', flexWrap: 'wrap', gap: 8 }}>
          <Eyebrow dot="var(--mint)"><Bot size={13} aria-hidden /> Guardian assistant · on this PC</Eyebrow>
          {msgs.length > 0 && <button className="btn" style={{ padding: '6px 10px', marginBottom: 12 }} onClick={() => { setMsgs([]); call('assistantReset') }}><Trash2 size={13} aria-hidden /> New chat</button>}
        </div>
        <div ref={scroller} className="chat" role="log" aria-live="polite" aria-label="Conversation">
          {msgs.length === 0 && (
            <div className="chat-empty">
              <motion.div animate={{ rotate: [0, 8, -8, 0] }} transition={{ duration: 6, repeat: Infinity }} style={{ display: 'inline-block', color: 'var(--mint)' }}><Sparkles size={34} aria-hidden /></motion.div>
              <div style={{ fontSize: 20, fontWeight: 600, marginTop: 12 }}>Ask about your battery</div>
              <div className="body" style={{ maxWidth: 480, margin: '8px auto 22px' }}>Answers come straight from this laptop's battery data and Guardian X's built-in guides. Instant, offline, and nothing leaves this PC.</div>
              <div className="suggestions">
                {suggestions.map((s) => <button key={s} className="btn" onClick={() => ask(s)}>{s}</button>)}
              </div>
            </div>
          )}
          {msgs.map((m, i) => (
            <motion.div key={i} className={`msg ${m.role}`} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }}>
              {m.text}{m.role === 'bot' && !m.done && <span className="caret" />}
              {m.sources && m.sources.length > 0 && (
                <div className="sources">
                  {m.sources.map((s, k) => <span key={k} className="source"><BookOpen size={11} aria-hidden /> {s}</span>)}
                </div>
              )}
            </motion.div>
          ))}
          {!busy && lastBot?.followUps && lastBot.followUps.length > 0 && msgs[msgs.length - 1] === lastBot && (
            <div className="suggestions left">
              {lastBot.followUps.map((f) => <button key={f} className="btn chip-btn" onClick={() => ask(f)}>{f}</button>)}
            </div>
          )}
        </div>
        <form onSubmit={(e) => { e.preventDefault(); ask(input) }} className="row" style={{ gap: 10 }}>
          <label htmlFor="ask" className="sr-only">Ask Guardian X</label>
          <input id="ask" className="input" placeholder="Ask about battery health, charging, apps, storage, sleep…" value={input} onChange={(e) => setInput(e.target.value)} maxLength={300} />
          <button className="btn primary" disabled={busy || !input.trim()} aria-label="Send"><Send size={15} aria-hidden /></button>
        </form>
      </Card>

      <div className="grid side-col">
        <Card delay={0.05}>
          <Eyebrow dot="var(--mint)"><Cpu size={13} aria-hidden /> How it works</Eyebrow>
          <div className="grid" style={{ gap: 10 }}>
            <Row icon={<ShieldCheck size={14} />} k="Runs on" v="this PC" />
            <Row icon={<BookOpen size={14} />} k="Knowledge" v={status ? `${status.sections} sections` : '…'} />
            <Row icon={<Sparkles size={14} />} k="Download" v="none" />
          </div>
          <div className="body" style={{ fontSize: 12.5, marginTop: 12 }}>
            It works out what you're asking, answers from your live readings and history, and backs the answer with the matching section of its battery guides (retrieval-augmented). No AI model and no internet.
          </div>
        </Card>
        <Card delay={0.1}>
          <Eyebrow><Link2 size={13} aria-hidden /> MCP server</Eyebrow>
          <div className="body" style={{ fontSize: 13 }}>Lets Claude read your Guardian X data (battery, forecast, history, apps, storage, modes) and ask this assistant, through read-only tools.</div>
          <button className="btn" style={{ marginTop: 12, width: '100%', justifyContent: 'center' }} disabled={mcp?.connected}
            onClick={async () => { await call('mcpConnect'); load() }}>{mcp?.connected ? 'Connected to Claude Desktop' : 'Connect to Claude Desktop'}</button>
          <div className="num path">{mcp?.exePath}</div>
          {mcp?.connected && <div className="body" style={{ fontSize: 12, marginTop: 8 }}>Restart Claude Desktop to load it.</div>}
        </Card>
      </div>
    </div>
  )
}

function Row({ icon, k, v }: { icon: React.ReactNode; k: string; v: string }) {
  return (
    <div className="row" style={{ justifyContent: 'space-between' }}>
      <span className="row" style={{ gap: 8, fontSize: 13 }}><span style={{ color: 'var(--mint)', display: 'inline-flex' }} aria-hidden>{icon}</span>{k}</span>
      <span className="num" style={{ fontSize: 12.5, color: 'var(--mint)' }}>{v}</span>
    </div>
  )
}
