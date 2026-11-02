<div align="center">

<img src="images/fox.png" width="96" alt="Mihomo Launcher logo">

# Mihomo Launcher

**Your own VPN for every app.**

Per‑app split tunneling for Windows: only the apps and sites you choose go through VPN, everything else goes direct.

[![Download .zip](https://img.shields.io/badge/Download-MihomoLauncher.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip)
[![Website](https://img.shields.io/badge/Website-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/mihomolauncher/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![Engine: mihomo](https://img.shields.io/badge/engine-mihomo-444)
![Free](https://img.shields.io/badge/price-free-2ea44f)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

**English** · [Русский](README.ru.md)

</div>

Set it up once — which apps and sites go through which VPN. After that it's one **Connect** button, and the launcher finds a working node by itself.

- **Every VPN protocol** — runs on the mihomo engine: WireGuard, VLESS/Reality, Hysteria 2, Clash subscriptions and more.
- **Tunnel by app and by site** — only what you picked goes through VPN, everything else goes direct.
- **Dozens of VPNs at once** — each app gets its own VPN, primary and fallback, all in one table.
- **Apps from anywhere** — from running processes, via Explorer, or installed from the Microsoft Store.
- **Kill‑switch** — no VPN, no network for the app. Your real IP won't leak.
- **One rule, everything in it** — an app, sites and ready‑made templates: hundreds of services and countries, picked by search, updated automatically.

## Get started

1. Unpack the [archive](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip)
2. Run `MihomoLauncher.exe` — the engine installs itself
3. Add your VPN and apps — and press **Connect**

Windows 10 / 11 (x64). Free, no telemetry.

## Build from source

The ready-to-run build is the [archive above](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip) — the app updates itself from it. To build your own:

1. Windows with the .NET SDK (any recent, e.g. 8 or 9) and .NET Framework 4.8 — the app targets `net48`
2. `dotnet build MihomoLauncher.csproj -c Release`
3. The result is in `bin\Release\MihomoLauncher\`: `MihomoLauncher.exe` in the root, everything else in `Data\`

`build\layout-release.ps1` (run by the Release build) shapes that folder. It looks for a GeoLite2 database in `..\..\maxmind` next to the project or in `%MIHOMOLAUNCHER_GEO_SOURCE%`; without one, download it in the app: **Settings → Updates → Geolocation database**. Code signing is optional — set `MIHOMOLAUNCHER_SIGN_THUMBPRINT` (or `MIHOMOLAUNCHER_SIGN_PFX` + `MIHOMOLAUNCHER_SIGN_PFX_PASS`). The mihomo engine is not in the repo: the app downloads it on first run.

## License

[GPL-3.0](LICENSE) with an author-attribution term — see [NOTICE](NOTICE). Forks and modified versions must keep the line *“Based on Mihomo Launcher by byFox — https://byfox.dev/mihomolauncher/”*.

---

<sub>This product includes GeoLite2 data created by MaxMind, available from <https://www.maxmind.com>. VPN engine: [mihomo](https://github.com/MetaCubeX/mihomo). Rule templates: [blackmatrix7 / ios_rule_script](https://github.com/blackmatrix7/ios_rule_script). Tile icons: [dashboard-icons](https://github.com/homarr-labs/dashboard-icons). UI: [WPF-UI](https://github.com/lepoco/wpfui).</sub>

<sub>Keywords: per-app VPN, split tunneling, mihomo, Clash.Meta, WireGuard, VLESS, Reality, Hysteria2, kill-switch, Windows VPN launcher.</sub>
