# HP OMEN battery options

## There is no 80% charge limit on OMEN laptops
HP offers a fixed charge limit (Battery Care Function or "Maximize my battery health", lowering the maximum charge to 80%) only on some HP business laptops. HP support staff in the HP Community state that consumer notebooks, including OMEN gaming laptops, do not support limiting the charge to 80%. No Windows app can force the limit on these models; apps that claim to only show alerts.

## Adaptive Battery Optimizer
OMEN and other HP gaming laptops have Adaptive Battery Optimizer instead. It monitors battery temperature, charging status and usage patterns, and activates protective measures by itself when it detects conditions that could harm the battery. It is not a fixed 80% cap, so the battery can still charge to 100%.
- Turn it on: restart, press F10 for BIOS setup, open Configuration, set Adaptive Battery Optimizer to Enabled, save with F10.
- Check whether it has stepped in: restart, press F2 for HP PC Hardware Diagnostics UEFI, then Power > Battery > Run Once > Battery Details. "Enabled/Not Activated" means it is on but no protective measures are active yet.
- Disabling it does not affect the battery warranty.

## The smart plug workaround
Because the laptop cannot stop charging by itself, the reliable way to keep an OMEN battery between, say, 60% and 80% is to cut power at the wall. Guardian X can switch a Wi-Fi smart plug (Shelly, or TP-Link Tapo P100/P105/P110/P115) off when the battery reaches the limit and back on when it drops to the resume level.

## OMEN Gaming Hub
OMEN Gaming Hub controls performance modes, fans and lighting. Higher performance modes produce more heat, which also ages the battery.
