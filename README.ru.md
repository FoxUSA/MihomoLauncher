<div align="center">

<img src="images/fox.png" width="96" alt="Логотип Mihomo Launcher">

# Mihomo Launcher

**Свой VPN для каждого приложения.**

Раздельное туннелирование для Windows: через VPN идут только выбранные приложения и сайты, остальное — напрямую.

[![Скачать .zip](https://img.shields.io/badge/Скачать-MihomoLauncher.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip)
[![Сайт](https://img.shields.io/badge/Сайт-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/mihomolauncher/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![Движок: mihomo](https://img.shields.io/badge/движок-mihomo-444)
![Бесплатно](https://img.shields.io/badge/цена-бесплатно-2ea44f)
[![Лицензия: GPL-3.0](https://img.shields.io/badge/лицензия-GPL--3.0-blue)](LICENSE)

[English](README.md) · **Русский**

</div>

Настраиваете один раз: какие приложения и сайты через какой VPN. Дальше — одна кнопка **Подключиться**, лаунчер сам подберёт рабочий узел.

- **Все протоколы VPN** — работает на движке mihomo: WireGuard, VLESS/Reality, Hysteria 2, Clash-подписки и другие.
- **Туннель по приложениям и сайтам** — через VPN идёт только то, что вы выбрали, всё остальное напрямую.
- **Десятки VPN одновременно** — каждому приложению свой VPN, основной и запасной, всё в одной таблице.
- **Приложения откуда угодно** — из запущенных процессов, через проводник или установленные из Microsoft Store.
- **Kill-switch** — нет VPN — приложение не выходит в сеть. Реальный IP не утечёт.
- **Одно правило — всё сразу** — приложение, сайты и готовые шаблоны: сотни сервисов и страны, выбираются поиском, обновляются сами.

## Как начать

1. Распаковать [архив](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip)
2. Запустить `MihomoLauncher.exe` — движок поставится сам
3. Добавить VPN и приложения — и нажать **Подключиться**

Windows 10 / 11 (x64). Бесплатно, без телеметрии.

## Сборка из исходников

Готовая сборка — [архив выше](https://byfox.dev/data/mihomolauncher/MihomoLauncher.zip), утилита сама обновляется из него. Собрать свою:

1. Windows с .NET SDK (любой свежий, например 8 или 9) и .NET Framework 4.8 — проект собирается под `net48`
2. `dotnet build MihomoLauncher.csproj -c Release`
3. Результат в `bin\Release\MihomoLauncher\`: в корне `MihomoLauncher.exe`, всё остальное в `Data\`

Папку раскладывает `build\layout-release.ps1` (его запускает Release-сборка). Базу GeoLite2 он ищет в `..\..\maxmind` рядом с проектом или в `%MIHOMOLAUNCHER_GEO_SOURCE%`; если её нет, скачайте базу в самой утилите: **Настройки → Обновление → База геолокации (GeoLite2)**. Подпись кода необязательна — переменные `MIHOMOLAUNCHER_SIGN_THUMBPRINT` (или `MIHOMOLAUNCHER_SIGN_PFX` + `MIHOMOLAUNCHER_SIGN_PFX_PASS`). Движка mihomo в репозитории нет: утилита скачивает его при первом запуске.

## Лицензия

[GPL-3.0](LICENSE) с условием об указании автора — см. [NOTICE](NOTICE). Форки и изменённые версии обязаны сохранять строку *«Based on Mihomo Launcher by byFox — https://byfox.dev/mihomolauncher/»*.

---

<sub>This product includes GeoLite2 data created by MaxMind, available from <https://www.maxmind.com>. Движок VPN: [mihomo](https://github.com/MetaCubeX/mihomo). Шаблоны правил: [blackmatrix7 / ios_rule_script](https://github.com/blackmatrix7/ios_rule_script). Иконки плиток: [dashboard-icons](https://github.com/homarr-labs/dashboard-icons). Интерфейс: [WPF-UI](https://github.com/lepoco/wpfui).</sub>

<sub>Ключевые слова: VPN по приложениям, раздельное туннелирование, mihomo, Clash.Meta, WireGuard, VLESS, Reality, Hysteria2, kill-switch, VPN-лаунчер для Windows.</sub>
