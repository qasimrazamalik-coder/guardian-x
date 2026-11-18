import { motion } from 'framer-motion'
import { Counter } from './ui'

/** The health gauge: a 270° arc with a gradient stroke, glow, an 80% "worn out" tick and an animated sweep. */
export function HealthRing({ health, size = 260 }: { health: number; size?: number }) {
  const stroke = size * 0.07
  const r = size / 2 - stroke
  const c = size / 2
  const sweep = 270
  const start = 135
  const arc = (from: number, to: number) => {
    const a0 = ((start + from) * Math.PI) / 180
    const a1 = ((start + to) * Math.PI) / 180
    const large = to - from > 180 ? 1 : 0
    return `M ${c + r * Math.cos(a0)} ${c + r * Math.sin(a0)} A ${r} ${r} 0 ${large} 1 ${c + r * Math.cos(a1)} ${c + r * Math.sin(a1)}`
  }
  const h = Math.max(0, Math.min(100, health))
  const color = h >= 90 ? ['#3dffb0', '#3de0ff'] : h >= 80 ? ['#ffd36b', '#ff9f43'] : ['#ff7a8f', '#ff4d6a']
  const tick = ((start + sweep * 0.8) * Math.PI) / 180
  const ticks = Array.from({ length: 28 }, (_, i) => i)
  return (
    <div style={{ position: 'relative', width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <defs>
          <linearGradient id="ring" x1="0" y1="1" x2="1" y2="0">
            <stop offset="0%" stopColor={color[0]} />
            <stop offset="100%" stopColor={color[1]} />
          </linearGradient>
          <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
            <feGaussianBlur stdDeviation={stroke * 0.55} result="b" />
            <feMerge><feMergeNode in="b" /><feMergeNode in="SourceGraphic" /></feMerge>
          </filter>
        </defs>
        {ticks.map((i) => {
          const a = ((start + (sweep * i) / (ticks.length - 1)) * Math.PI) / 180
          const r0 = r - stroke * 1.25, r1 = r - stroke * (i % 3 === 0 ? 1.9 : 1.55)
          return <line key={i} x1={c + r0 * Math.cos(a)} y1={c + r0 * Math.sin(a)} x2={c + r1 * Math.cos(a)} y2={c + r1 * Math.sin(a)} stroke="rgba(160,200,230,0.18)" strokeWidth={1.2} />
        })}
        <path d={arc(0, sweep)} stroke="rgba(160,200,230,0.08)" strokeWidth={stroke} fill="none" strokeLinecap="round" />
        <motion.path d={arc(0, sweep)} stroke="url(#ring)" strokeWidth={stroke} fill="none" strokeLinecap="round" filter="url(#glow)"
          initial={{ pathLength: 0 }} animate={{ pathLength: h / 100 }} transition={{ duration: 1.6, ease: [0.2, 0.8, 0.2, 1] }} />
        <line x1={c + (r - stroke) * Math.cos(tick)} y1={c + (r - stroke) * Math.sin(tick)} x2={c + (r + stroke) * Math.cos(tick)} y2={c + (r + stroke) * Math.sin(tick)}
          stroke="rgba(234,242,248,0.7)" strokeWidth={2} />
        <text x={c + (r + stroke * 1.9) * Math.cos(tick)} y={c + (r + stroke * 1.9) * Math.sin(tick)} fill="rgba(234,242,248,0.5)" fontSize={10} fontFamily="var(--mono)" textAnchor="middle">80</text>
      </svg>
      <div style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', textAlign: 'center' }}>
        <div>
          <div style={{ fontSize: size * 0.21, fontWeight: 600, letterSpacing: '-0.03em', lineHeight: 1 }}>
            <Counter value={h} decimals={1} /><span style={{ fontSize: size * 0.08, color: 'var(--muted)', marginLeft: 2 }}>%</span>
          </div>
          <div className="eyebrow" style={{ justifyContent: 'center', margin: '8px 0 0' }}>battery health</div>
        </div>
      </div>
    </div>
  )
}

/** A battery cell with a liquid fill that waves, and rising sparks while charging. */
export function BatteryCell({ percent, charging, onAc, width = 120, height = 210 }: { percent: number; charging: boolean; onAc: boolean; width?: number; height?: number }) {
  const pad = 8
  const innerH = height - 34 - pad * 2
  const level = Math.max(0, Math.min(100, percent))
  const fillTop = 26 + pad + innerH * (1 - level / 100)
  const color = level <= 20 ? '#ff4d6a' : level >= 95 && onAc ? '#ffb547' : '#3dffb0'
  const wave = (phase: number) => {
    const w = width - pad * 2
    let d = `M ${pad} ${fillTop}`
    for (let x = 0; x <= w; x += 6) d += ` L ${pad + x} ${fillTop + Math.sin((x / w) * Math.PI * 2 + phase) * 3.5}`
    return d + ` L ${width - pad} ${height - pad} L ${pad} ${height - pad} Z`
  }
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} style={{ overflow: 'visible' }}>
      <defs>
        <linearGradient id="liquid" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity={0.95} />
          <stop offset="100%" stopColor={color} stopOpacity={0.35} />
        </linearGradient>
        <clipPath id="cell"><rect x={pad} y={26 + pad} width={width - pad * 2} height={innerH} rx={14} /></clipPath>
        <filter id="cellglow"><feGaussianBlur stdDeviation="10" /></filter>
      </defs>
      <rect x={width * 0.32} y={6} width={width * 0.36} height={14} rx={5} fill="rgba(160,200,230,0.25)" />
      <rect x={1} y={20} width={width - 2} height={height - 21} rx={20} fill="rgba(10,16,24,0.6)" stroke="rgba(160,200,230,0.25)" strokeWidth={1.5} />
      <g clipPath="url(#cell)">
        <rect x={pad} y={fillTop} width={width - pad * 2} height={height} fill={color} opacity={0.25} filter="url(#cellglow)" />
        <motion.path fill="url(#liquid)" animate={{ d: [wave(0), wave(Math.PI), wave(Math.PI * 2)] }} transition={{ duration: 3.2, repeat: Infinity, ease: 'linear' }} />
        {charging && Array.from({ length: 6 }, (_, i) => (
          <motion.circle key={i} r={2.2} fill="#eafff6" cx={pad + 12 + i * ((width - pad * 2 - 24) / 5)}
            initial={{ cy: height - 10, opacity: 0 }} animate={{ cy: fillTop + 6, opacity: [0, 1, 0] }}
            transition={{ duration: 1.8, repeat: Infinity, delay: i * 0.3, ease: 'easeOut' }} />
        ))}
      </g>
      <text x={width / 2} y={height / 2 + 22} textAnchor="middle" fontSize={30} fontWeight={600} fill="#eaf2f8" style={{ fontFamily: 'var(--display)' }}>{Math.round(level)}%</text>
    </svg>
  )
}

/** A small semicircle gauge for 0-100 scores. */
export function MiniGauge({ value, color, size = 120 }: { value: number; color: string; size?: number }) {
  const r = size / 2 - 10
  const c = size / 2
  const len = Math.PI * r
  return (
    <svg width={size} height={size / 2 + 12}>
      <path d={`M 10 ${c} A ${r} ${r} 0 0 1 ${size - 10} ${c}`} stroke="rgba(160,200,230,0.1)" strokeWidth={10} fill="none" strokeLinecap="round" />
      <motion.path d={`M 10 ${c} A ${r} ${r} 0 0 1 ${size - 10} ${c}`} stroke={color} strokeWidth={10} fill="none" strokeLinecap="round"
        style={{ filter: `drop-shadow(0 0 6px ${color})` }} strokeDasharray={len}
        initial={{ strokeDashoffset: len }} animate={{ strokeDashoffset: len * (1 - Math.min(100, value) / 100) }} transition={{ duration: 1.2, ease: [0.2, 0.8, 0.2, 1] }} />
    </svg>
  )
}

export function Logo({ size = 34 }: { size?: number }) {
  return (
    <svg className="brand-mark" width={size} height={size} viewBox="0 0 64 64">
      <defs><linearGradient id="lg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stopColor="#3dffb0" /><stop offset="1" stopColor="#3de0ff" /></linearGradient></defs>
      <path d="M32 4 L56 11 V31 C56 45 45 55 32 60 C19 55 8 45 8 31 V11 Z" fill="#071017" stroke="url(#lg)" strokeWidth="3.5" strokeLinejoin="round" />
      <rect x="24" y="19" width="16" height="25" rx="3.5" fill="none" stroke="#eaf2f8" strokeWidth="2.6" />
      <rect x="28.5" y="15.5" width="7" height="3.5" rx="1" fill="#eaf2f8" />
      <rect x="27.5" y="29" width="9" height="11.5" rx="1.5" fill="url(#lg)" />
    </svg>
  )
}
