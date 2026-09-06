# DriverWatch

A Beacon-Prime-styled system-tray driver monitor and updater for Windows (running on *bellatrix*).

- Lists installed drivers with **version** and **release date** in a dark, gold-accented dashboard served on `http://127.0.0.1:48620/` (loopback only).
- Checks **weekly** whether each driver is current, using **Windows Update** *and* **vendor** sources.
- Flags out-of-date drivers with a clickable **Update available** pill. Windows Update drivers install in place; vendor packages open the vendor's download page rather than running an unattended `.exe`.
- Lives in the **system tray** only — no taskbar entry — and launches at logon.

## How it works

- **Enumeration:** WMI `Win32_PnPSignedDriver`, deduped by device class, name, and vendor (newest version wins).
- **Windows Update:** the Windows Update Agent COM API (`Microsoft.Update.Session`), searching for uninstalled driver updates. These are installable in place.
- **Vendor checks:** best-effort per-vendor lookups. For the AMD integrated Vega GPU, DriverWatch reads the "Windows Driver Store Version" from the dated Polaris/Vega release-notes page, discovered by probing its URL newest-first (the release-notes index is a JavaScript single-page app, so its links are not in the static HTML).
- **Cadence:** a six-hourly timer that runs a full check whenever seven days have elapsed, so a suspend or reboot cannot silently swallow the weekly pass.

## Build and deploy

Compiled on the Windows host with the .NET Framework C# compiler (C# 5), so there are no external dependencies.

```
Scripts/deploy.sh [ssh-host]     # default: bellatrix
```

This copies the source, compiles it with `csc` (`/target:winexe`, so there is no console window), registers a logon scheduled task at highest run level, and launches it.
