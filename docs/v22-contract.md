# Guardian X 2.2 — bridge contract (engine ⇄ interface)

All JSON is camelCase (System.Text.Json Web defaults, enums as strings). Existing fields stay; these are additions.

## dashboard (additions)
```ts
battery: {
  ...existing,
  rateW: number | null          // SMOOTHED power: + charging, − discharging (was raw)
  rateRawW: number | null       // instantaneous driver reading
  rateMethod: 'energy' | 'driver' | 'none'  // 'energy' = slope of remaining Wh over the last minutes (most accurate)
  healthPrecise: number         // median full-charge capacity of last 7 days / design, 2 decimals
  healthLow: number; healthHigh: number   // min/max of daily health over last 14 days (spread of readings)
  lastCalibrated: string | null // ISO date of last full discharge(<15%)→100% charge; null if never seen
}
heat: {
  tempC: number | null          // real reading (Windows thermal zone) when available
  source: 'thermal-zone' | 'battery' | 'estimate'
  level: 'cool' | 'warm' | 'hot'
  score: number                 // 0..100 heat load
  cpuPercent: number; gpuBusy: boolean; chargingW: number | null
  basis: string                 // one sentence on how it was worked out
}
habit: {
  score: number                 // 0..100, higher = kinder to the battery (last 7 days)
  grade: 'A' | 'B' | 'C' | 'D' | 'E'
  change: number | null         // vs the previous 7 days
  factors: { name: string; impact: number; detail: string }[]  // impact = points lost (0 = fine)
}
pattern: {
  usualUnplug: string | null    // "HH:mm", median weekday unplug time over 3 weeks
  usualPlugIn: string | null
  lastsUntilPlugIn: boolean | null   // on battery: will the charge reach usualPlugIn?
  readyBy: { time: string; percent: number } | null   // on charger: charge expected at usualUnplug
  message: string | null
}
vendor: { brand: string; nativeLimit: 'asus' | 'lenovo' | null; limitActive: boolean | null; note: string }
gaming: boolean                  // full-screen OR foreground app busy on the NVIDIA GPU
```

## settings (additions)
`theme: 'system' | 'light' | 'dark'` (persisted in settings.json, read by the widget too),
`smartLowAlert: boolean` (warn when the charge won't last to the usual plug-in time), `toastsOn: boolean`.
Setting `theme` is also sent as push event `theme` `{ theme }`.

## new methods
- `setVendorLimit { on: boolean }` → `{ ok, message }` (ASUS only; Lenovo returns guidance)

## chat (done event additions)
`actions: { label: string; method?: string; params?: object; navigate?: string }[]` — buttons under an answer
(e.g. `{label:'Switch to Saver mode', method:'setMode', params:{id:'saver'}}`, `{label:'Open Charging', navigate:'charging'}`).
The assistant also understands Roman Urdu ("battery jaldi kyun khatam hoti hai", "charger lagaye rakhna theek hai?").

## push events
`update`, `alert`, `navigate`, `theme`, `chat`, `setup`, `updateStatus` (unchanged meanings).
