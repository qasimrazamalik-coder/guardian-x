export type TipLevel = 'Good' | 'Info' | 'Warn' | 'Alert'

export interface Battery {
  percent: number; onAc: boolean; charging: boolean; rateW: number | null; voltage: number | null; tempC: number | null
  remainingWh: number; fullWh: number; designWh: number; cycles: number
  health: number; wear: number; wearPer100: number | null; wearPerMonth: number | null; hoursLeft: number | null
  model: string; chemistry: string; manufacturer: string
}
export interface Stress { score: number; level: string; hoursFull: number; hoursHot: number; hoursLow: number; tempKnown: boolean }
export interface Sleep { start: string; end: string; lost: number; perHour: number; unusual: boolean }
export interface Tip { level: TipLevel; title: string; body: string }
export interface Mode { id: string; name: string; description: string; overlay: string; chargeLimit: number; resumeAt: number; gpuAlerts: boolean }
export interface PlugState { configured: boolean; name: string; on: boolean | null; status: string; lastAction: string | null }

export interface Dashboard {
  battery: Battery | null
  stress: Stress
  holdMinutes: number
  chargerHours: number | null
  lastSleep: Sleep | null
  gpu: { name: string | null; available: boolean; holders: { processName: string; dedicatedBytes: number }[] }
  tips: Tip[]
  mode: Mode
  plug: PlugState
  chargeLimitOn: boolean
  chargeToFull: boolean
  forecast: Forecast | null
  auto: { last: string | null; gaming: boolean; quiet: boolean }
  error: string | null
  version: string
}

export interface Forecast {
  healthNow: number; lossPerMonth: number | null; healthAt80On: string | null; monthsTo80: number | null
  hoursToEmpty: number | null; hoursToFull: number | null; hoursToLimit: number | null; percentAtNight: number | null; basis: string
}

export interface DayPoint { t: string; p: number; ac: boolean; w: number | null }

export interface History {
  daily: { day: string; health: number; fullWh: number; cycles: number }[]
  trendPerMonth: number | null
  biggestDrop: { from: string; to: string; lost: number } | null
  chargerHours: number | null
  forecast: Forecast | null
  days: { day: string; health: number; cycles: number; stress: number; level: string; hoursFull: number; lowest: number }[]
  gpuWeek: { app: string; times: number }[]
}

export interface Analytics {
  histogram: number[]
  gridAc: number[][]
  gridDc: number[][]
  split: { day: string; acActive: number; dcActive: number; acStandby: number; dcStandby: number }[]
  appEnergy: { name: string; wh: number }[]
  sampleDays: number
}

export interface AppUsage { key: string; name: string; processes: number; memoryMb: number; cpuPercent: number; diskMbPerSec: number; estimatedWatts: number | null; kind: string; efficiency: boolean; canThrottle: boolean }
export interface AppsReport { apps: AppUsage[]; onBattery: boolean; autoEfficiency: boolean; totalRamGb: number; usedRamGb: number; cpuTotal: number; tips: { app: string; text: string }[] }
export interface Storage { drives: { name: string; label: string; totalGb: number; freeGb: number }[]; largestApps: { name: string; sizeGb: number; publisher: string }[]; tempGb: number }

export interface Settings {
  startWithWindows: boolean; notifyFullHold: boolean; fullHoldMinutes: number; notifyLow: boolean; lowPercent: number
  notifyHigh: boolean; highPercent: number; notifySleepDrain: boolean; notifyGpu: boolean
  soundsOn: boolean; soundPack: string; plugSounds: boolean; mode: string; applyPowerMode: boolean; chargeLimitOn: boolean
  quietHoursOn: boolean; quietFrom: number; quietTo: number
  autoModeOnPower: boolean; acMode: string; batteryMode: string; autoGamingMode: boolean; autoEfficiencyOnBattery: boolean
  efficiencyApps: string[]; weeklyReportOn: boolean; widgetOn: boolean; packs: string[]
}

export interface PlugConfig { kind: string; host: string; username: string; hasPassword: boolean }

export interface AssistantStatus { ready: boolean; engine: string; sections: number }

export interface TempSummary { sizeGb: number; files: number; recycleBinGb: number; recycleBinItems: number }
export interface BigFile { path: string; name: string; sizeGb: number; modified: string }
export interface StartupApp { id: string; name: string; command: string; location: string; enabled: boolean; canChange: boolean }
export interface ReportFile { name: string; date: string }

export interface McpInfo { exePath: string; connected: boolean; configs: string[] }
