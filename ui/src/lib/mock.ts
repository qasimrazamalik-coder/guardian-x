// Preview data for running the UI in a normal browser. Shaped from real readings of the developer's OMEN 16.
import type { Analytics, AppsReport, Dashboard, DayPoint, History, Storage } from './types'

const modes = [
  { id: 'desk', name: 'Desk', description: 'Plugged in most of the day. Keeps the battery between 60% and 80%.', overlay: 'Balanced', chargeLimit: 80, resumeAt: 60, gpuAlerts: true },
  { id: 'gaming', name: 'Gaming', description: 'Best performance. Still keeps the battery at 80% or below, because games make it hot.', overlay: 'Performance', chargeLimit: 80, resumeAt: 60, gpuAlerts: false },
  { id: 'travel', name: 'Travel', description: 'Charges to 100% before you leave, so you get the full runtime.', overlay: 'Balanced', chargeLimit: 100, resumeAt: 95, gpuAlerts: true },
  { id: 'saver', name: 'Saver', description: 'Best power efficiency for the longest time on battery.', overlay: 'Efficiency', chargeLimit: 80, resumeAt: 50, gpuAlerts: true },
]
let mode = 'desk'
let plug = { kind: 'none', host: '', username: '', hasPassword: false }
let plugOn: boolean | null = null

function dashboard(): Dashboard {
  return {
    battery: {
      percent: 100, onAc: true, charging: false, rateW: 0, voltage: 17.21, tempC: null,
      remainingWh: 62.86, fullWh: 62.86, designWh: 70.07, cycles: 54,
      health: 89.7, wear: 10.3, wearPer100: 19.0, wearPerMonth: 3.3, hoursLeft: null,
      model: 'OMEN Gaming Laptop 16-am0xxx', chemistry: 'LION', manufacturer: 'HP',
    },
    stress: { score: 34, level: 'Medium', hoursFull: 5.6, hoursHot: 0, hoursLow: 0, tempKnown: false },
    holdMinutes: 214,
    chargerHours: 16.3,
    lastSleep: { start: new Date(Date.now() - 9 * 3.6e6).toISOString(), end: new Date(Date.now() - 1.5 * 3.6e6).toISOString(), lost: 4.0, perHour: 0.53, unusual: false },
    gpu: { name: 'NVIDIA GeForce RTX 5060 Laptop GPU', available: true, holders: [] },
    tips: [
      { level: 'Alert', title: 'Your battery is wearing faster than its cycles explain', body: 'It has lost 10.3% in 54 cycles, about 19% per 100 cycles. A typical battery loses about 4% per 100 cycles, so most of this wear isn\'t from use. The usual causes are heat from long gaming sessions and sitting at 100% for hours.' },
      { level: 'Warn', title: 'Plugged in about 16 hours a day', body: 'Windows\' own battery records show your laptop switched on and plugged in for 16.3 hours a day over the last week. Unplug and use it down to about 40% a few times a week.' },
      { level: 'Warn', title: 'At 100% on the charger for 3 h 34 min', body: 'Sitting full and warm is what ages a lithium battery fastest. Your OMEN has no fixed 80% limit, so unplugging for a while is the main thing you can do.' },
    ],
    mode: modes.find((m) => m.id === mode)!,
    plug: plug.kind === 'none'
      ? { configured: false, name: '', on: null, status: 'No smart plug set up.', lastAction: null }
      : { configured: true, name: plug.kind === 'simulated' ? 'Simulated plug' : `${plug.kind} at ${plug.host}`, on: plugOn, status: plugOn ? 'On, charging allowed' : 'Off, holding the battery', lastAction: null },
    chargeLimitOn: true,
    chargeToFull: false,
    error: null,
    version: '2.0.0',
  }
}

function day(): DayPoint[] {
  const pts: DayPoint[] = []
  const now = Date.now()
  let p = 100
  for (let i = 24 * 60; i >= 0; i -= 2) {
    const t = now - i * 60000
    const h = new Date(t).getHours()
    const asleep = h >= 3 && h < 9
    if (asleep && i % 20) continue
    const onBattery = (h >= 14 && h < 16) || (h >= 21 && h < 22)
    if (onBattery) p = Math.max(18, p - 0.55); else p = Math.min(100, p + 1.4)
    pts.push({ t: new Date(t).toISOString(), p: Math.round(p * 10) / 10, ac: !onBattery, w: onBattery ? 18 + Math.sin(i) * 6 : (p < 100 ? 62 : 0) })
  }
  return pts
}

function history(): History {
  const raw: [string, number, number][] = [
    ['2026-07-21', 68229, 1], ['2026-08-02', 69622, 2], ['2026-08-17', 69329, 2], ['2026-08-25', 69208, 4], ['2026-09-05', 69139, 6],
    ['2026-09-07', 69371, 6], ['2026-09-14', 69062, 7], ['2026-09-21', 68251, 16], ['2026-09-28', 68109, 18], ['2026-10-05', 68377, 23],
    ['2026-10-12', 65731, 30], ['2026-10-19', 63198, 36], ['2026-10-26', 63082, 42], ['2026-11-02', 62686, 47], ['2026-11-09', 61753, 49],
    ['2026-11-10', 62531, 51], ['2026-11-11', 62525, 51], ['2026-11-12', 62461, 52], ['2026-11-13', 62670, 52], ['2026-11-14', 62136, 53],
    ['2026-11-15', 61679, 54], ['2026-11-16', 61890, 54], ['2026-11-17', 62439, 54], ['2026-11-18', 62862, 54],
  ]
  return {
    daily: raw.map(([day, full, cycles]) => ({ day, health: (full * 100) / 70070, fullWh: full / 1000, cycles })),
    trendPerMonth: 3.3,
    biggestDrop: { from: '2026-10-05', to: '2026-10-19', lost: 7.4 },
    chargerHours: 16.3,
    days: [
      { day: '2026-11-18', health: 89.7, cycles: 54, stress: 34, level: 'Medium', hoursFull: 5.6, lowest: 100 },
      { day: '2026-11-17', health: 89.1, cycles: 54, stress: 61, level: 'High', hoursFull: 10.2, lowest: 64 },
      { day: '2026-11-16', health: 88.3, cycles: 54, stress: 58, level: 'Medium', hoursFull: 9.7, lowest: 71 },
    ],
    gpuWeek: [{ app: 'Discord', times: 41 }, { app: 'chrome', times: 12 }],
  }
}

function analytics(): Analytics {
  const grid = (on: (d: number, h: number) => number) => Array.from({ length: 7 }, (_, d) => Array.from({ length: 24 }, (_, h) => on(d, h)))
  return {
    histogram: [2, 8, 15, 30, 44, 60, 75, 120, 260, 940],
    gridAc: grid((d, h) => (h >= 10 || h < 2 ? 40 + ((d * 7 + h * 3) % 20) : h < 4 ? 20 : 0)),
    gridDc: grid((d, h) => ((h === 14 || h === 15) && d < 5 ? 35 : h === 21 ? 20 : 0)),
    split: Array.from({ length: 14 }, (_, i) => {
      const d = new Date(Date.now() - (13 - i) * 864e5)
      const ac = [8.9, 13.5, 22.4, 14.8, 8.6, 23.1, 22.8, 18, 12, 15, 20, 21, 9, 16][i]
      return { day: d.toISOString().slice(0, 10), acActive: ac * 0.9, acStandby: ac * 0.1, dcActive: [2.1, 1.0, 0.4, 1.3, 2.2, 0.9, 1.2, 0.8, 1.5, 2, 0.5, 0.7, 3, 1][i], dcStandby: 0.3 }
    }),
    appEnergy: [
      { name: 'Google Chrome', wh: 14.2 }, { name: 'Discord', wh: 6.1 }, { name: 'Visual Studio Code', wh: 5.4 },
      { name: 'Spotify', wh: 2.2 }, { name: 'Steam', wh: 1.6 }, { name: 'WhatsApp', wh: 1.1 },
    ],
    sampleDays: 30,
  }
}

function apps(): AppsReport {
  const jitter = (v: number) => Math.max(0, v * (0.8 + Math.random() * 0.4))
  const base = [
    ['chrome', 'Google Chrome', 31, 3480, 6.2, 0.4, 'browser'], ['Code', 'Visual Studio Code', 12, 1620, 4.1, 0.2, 'app'],
    ['Discord', 'Discord', 7, 820, 1.8, 0.05, 'app'], ['Claude', 'Claude', 9, 1150, 2.6, 0.1, 'app'],
    ['Spotify', 'Spotify', 6, 410, 0.9, 0.02, 'app'], ['steam', 'Steam', 5, 380, 0.3, 0, 'app'],
    ['MsMpEng', 'Antimalware Service Executable', 1, 310, 1.1, 1.2, 'system'], ['OMENCommandCenterBackground', 'OMEN Gaming Hub', 3, 290, 0.6, 0, 'app'],
    ['WhatsApp', 'WhatsApp', 4, 260, 0.4, 0, 'app'], ['dwm', 'Desktop Window Manager', 1, 180, 2.0, 0, 'system'],
  ] as const
  return {
    apps: base.map(([key, name, procs, mem, cpu, disk, kind]) => ({
      key, name, processes: procs, memoryMb: Math.round(jitter(mem)), cpuPercent: Math.round(jitter(cpu) * 10) / 10,
      diskMbPerSec: Math.round(jitter(disk) * 100) / 100, estimatedWatts: null, kind,
    })),
    onBattery: false, totalRamGb: 31.6, usedRamGb: 14.2, cpuTotal: 18,
    tips: [{ app: 'Google Chrome', text: 'Google Chrome is using 3.4 GB of RAM across 31 processes. Turn on sleeping tabs / Efficiency mode in its settings, and close tabs you\'re not reading.' }],
  }
}

function storage(): Storage {
  return {
    drives: [{ name: 'C:', label: 'Windows', totalGb: 203, freeGb: 14.1 }, { name: 'D:', label: '', totalGb: 300, freeGb: 90.2 }, { name: 'E:', label: '', totalGb: 350, freeGb: 314 }, { name: 'F:', label: '', totalGb: 100, freeGb: 97 }],
    largestApps: [{ name: 'Microsoft Office', sizeGb: 4.7, publisher: 'Microsoft' }, { name: 'Roblox Studio', sizeGb: 4.2, publisher: 'Roblox' }, { name: 'Lens Studio', sizeGb: 1.8, publisher: 'Snap Inc.' }],
    tempGb: 0.55,
  }
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

export async function mock(method: string, params: any, emit: (e: string, d: any) => void): Promise<any> {
  await delay(120)
  switch (method) {
    case 'dashboard': return dashboard()
    case 'day': return day()
    case 'history': return history()
    case 'analytics': return analytics()
    case 'apps': return apps()
    case 'storage': return storage()
    case 'modes': return { modes, current: mode, applyPowerMode: true, powerOverlay: 'Balanced' }
    case 'setMode': mode = params.id; return { ok: true }
    case 'plugGet': return { config: plug, state: dashboard().plug }
    case 'plugSave': plug = { kind: params.kind, host: params.host, username: params.username, hasPassword: !!params.password }; plugOn = true; return dashboard().plug
    case 'plugSet': plugOn = params.on; return dashboard().plug
    case 'settingsGet': return { startWithWindows: true, notifyFullHold: true, fullHoldMinutes: 120, notifyLow: true, lowPercent: 20, notifyHigh: true, highPercent: 80, notifySleepDrain: true, notifyGpu: true, soundsOn: true, soundPack: 'nebula', plugSounds: true, mode, applyPowerMode: true, chargeLimitOn: true, chatModel: 'qwen2.5:7b', embedModel: 'nomic-embed-text' }
    case 'assistantStatus': return { installed: true, running: true, chatModelReady: true, embedModelReady: true, chatModel: 'qwen2.5:7b', embedModel: 'nomic-embed-text', modelsFolder: 'E:\\GuardianX\\ollama-models' }
    case 'mcpInfo': return { exePath: 'C:\\Users\\you\\AppData\\Local\\GuardianX\\current\\GuardianX.Mcp.exe', connected: false, configs: [] }
    case 'ask': {
      const text = "Because sitting at 100% while warm is what ages a lithium battery fastest. Your OMEN was switched on and plugged in about 16.3 hours a day last week, and right now it has been at 100% on the charger for 3 h 34 min [K1].\n\nWhat helps:\n• Use Desk mode with a smart plug so it stays between 60% and 80%.\n• Unplug and run it down to about 40% a few times a week.\n• Keep the vents clear when gaming — your biggest drop (7.4%, 5–19 Oct) came in weeks with little charger time, so heat is likely part of it [K2]."
      const id = params.id
      ;(async () => {
        for (const word of text.split(/(\s+)/)) { await delay(18); emit('chat', { id, delta: word }) }
        emit('chat', { id, done: true, sources: ['How laptop batteries wear out: Why keeping it plugged in all the time is not ideal', 'How laptop batteries wear out: Heat'] })
      })()
      return { ok: true }
    }
    default: return { ok: true }
  }
}
