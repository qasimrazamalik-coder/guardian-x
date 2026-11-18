# Saving battery and memory on Windows 11

## Power mode
Settings > System > Power & battery > Power mode: Best power efficiency, Balanced or Best performance. Guardian X modes can switch this for you.

## Energy saver
Energy saver (battery saver) reduces background activity and screen brightness. It can turn on automatically at a chosen battery level in Settings > System > Power & battery.

## Graphics preference per app
Settings > System > Display > Graphics lets you set an app to "Power saving" so it uses the Intel integrated GPU instead of the NVIDIA GPU. That keeps the NVIDIA GPU asleep on battery.

## Browsers
Chrome and Edge have Memory Saver / Sleeping tabs and Energy Saver / Efficiency mode, which free RAM from inactive tabs and reduce power use on battery. Each tab runs as separate processes, so a browser often shows many processes and a lot of RAM.

## Efficiency mode for apps
In Task Manager, right-click a process and choose Efficiency mode to lower its priority and power use.

## Display
Screen brightness and refresh rate have a large effect on battery life. A 60 Hz refresh rate on battery uses less power than 165 or 240 Hz.

## Startup and background apps
Settings > Apps > Startup lists apps that start with Windows; turning off ones you don't need saves memory and power.

## Storage
Settings > System > Storage shows what uses space; Storage Sense can clean temporary files automatically. Large games and app caches are usually the biggest items.

## Useful reports
- powercfg /batteryreport: battery capacity history.
- powercfg /sleepstudy: what kept the laptop busy during sleep.
- powercfg /energy (admin): power efficiency problems.
