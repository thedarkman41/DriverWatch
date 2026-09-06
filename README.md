# DriverWatch

A driver monitor and updater for Windows (running on *bellatrix*), with the look of Beacon Prime.

The window is a **native, frameless, always-on-top panel** in the upper-right corner (dark ground, gold accent, one row per driver), owner-drawn with GDI+. It is **not a web page**. DriverWatch lives in the **system tray** only, with no taskbar entry: left-click the tray icon to toggle the panel, right-click for the menu. It launches at logon.

## What it tracks

By design, only the drivers that matter on this machine:

- **AMD Radeon Graphics** (the integrated Vega GPU)
- **AMD High Definition Audio** (the GPU's audio device)
- **Any Realtek driver** (audio, Bluetooth, wired and wireless network, card reader, and so on)

Microsoft inbox drivers, the monitor, and the rest of the enumerator noise are left out.

## How it works

- **Enumeration:** WMI `Win32_PnPSignedDriver`, filtered to the drivers above and deduped (newest version wins). Each row shows the installed **version** and **release date**.
- **Currency check (weekly):** for each driver, DriverWatch decides whether a newer one exists.
  - **Windows Update** (the Windows Update Agent COM API) covers the Realtek drivers and the AMD audio device. These are installable in place.
  - **AMD** directly for the Radeon GPU: it reads the "Windows Driver Store Version" from the dated Polaris/Vega release-notes page, found by probing that URL newest-first (the release-notes index is a JavaScript single-page app, so its links are not in the static HTML). A vendor package opens AMD's page rather than running an unattended `.exe`.
- **Update:** drivers that are behind get a gold **Update available** pill. Click it to install (Windows Update) or open the vendor page (AMD).
- **Cadence:** a six-hourly timer runs a full check whenever seven days have elapsed, so a suspend or reboot cannot silently swallow the weekly pass. The tray icon gains an amber ring when anything needs updating.

## Build and deploy

Compiled on the Windows host with the .NET Framework C# compiler (C# 5), so there are no external dependencies.

```
Scripts/deploy.sh [ssh-host]     # default: bellatrix
```

This copies the source, compiles it with `csc` (`/target:winexe`, so there is no console window), registers a logon scheduled task at highest run level, and launches it.
