# Guardian X features

## What Guardian X measures
Guardian X reads the battery through the Windows battery driver once a minute, without admin rights: design capacity, full charge capacity, remaining charge, cycle count, charge or discharge power, voltage, and temperature when the battery reports it. It stores the readings locally in a SQLite database in %LOCALAPPDATA%\GuardianXData. Nothing is sent anywhere.

## Battery health and wear
Health is full charge capacity divided by design capacity. Wear is 100% minus health. Wear speed is wear per 100 cycles, compared with a typical 4% per 100 cycles.

## History from Windows
Guardian X imports Windows' own battery history (powercfg /batteryreport) once a day. It goes back to when the battery was new, so the wear trend, the biggest drop and the hours plugged in per day are available from the first day. Older history is weekly; recent history is daily.

## Stress score
A daily score from 0 to 100: 6 points per hour at 95% or more on the charger, 12 points per hour above 40 °C (only when the battery reports temperature), and 20 points per hour below 10%. Under 30 is low, 30-59 medium, 60 or more high.

## Sleep drain
Guardian X records the charge when the laptop goes to sleep and wakes up. A sleep is flagged as unusual when it loses at least 3% at 1% an hour or more, and more than 2.5 times the usual rate.

## NVIDIA GPU watch
On battery, Guardian X lists apps that hold memory on the NVIDIA GPU, which keeps the GPU powered and drains the battery. It reads Windows' GPU counters, which does not wake the GPU (running nvidia-smi would).

## Per-app RAM, CPU, disk and battery
The Apps page shows each app's RAM, CPU share and disk activity. On battery it also estimates each app's power: the measured discharge minus a base share for the screen, Wi-Fi and chipset (45%), split by each app's share of CPU time. It is an estimate, because Windows does not give apps a true per-app power meter without admin rights.

## Charge limit with a smart plug
Supported plugs: Shelly (Gen 1 and Gen 2+, local HTTP) and TP-Link Tapo P100, P105, P110 and P115 (local KLAP protocol, using your Tapo account email and password). Set it up on the Charging page: choose the plug type, enter its IP address, and plug the laptop charger into it. A simulated plug lets you try the feature without hardware.
When a smart plug is set up and the charge limit is on, Guardian X switches the plug off when the battery reaches the mode's limit while on the charger, and back on when it falls to the resume level on battery. If the plug is off but the laptop still charges, the charger is not on that plug and Guardian X warns once. "Charge to full now" overrides the limit until the battery is full or the laptop is unplugged. The laptop must be awake for Guardian X to switch the plug.

## Modes
- Desk: balanced power mode, keeps 60-80%.
- Gaming: best performance power mode, keeps 60-80%, GPU alerts off.
- Travel: balanced, charges to 100% (resumes at 95%) before you leave.
- Saver: best power efficiency, keeps 50-80%.
Modes switch the Windows power mode when that option is on.

## Alerts and sounds
Alerts at 80% while charging (unplug now) and 20% on battery (plug in), after sitting at 100% on the charger too long, unusual sleep drain, and apps waking the GPU. Sounds are Guardian X's own, synthesised on your PC in two packs: Nebula (soft) and Pulse (crisp).

## Assistant and MCP
The assistant answers from Guardian X's live data and this knowledge base, using a local model through Ollama, so questions and data stay on the laptop. Guardian X also includes an MCP server so Claude can read the same data when you connect it.

## Install, update, repair, uninstall
Install with GuardianX-win-Setup.exe (no admin needed). Updates come from GitHub Releases through Settings. Repair checks the database, settings, start-up entry, battery and GPU access. Uninstall from Windows Settings > Apps; you can keep or delete your history.
