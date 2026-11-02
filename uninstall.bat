@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Mihomo Launcher - udalenie

rem ==== Povtornyy vhod s pravami administratora: tolko udalenie zadachi ====
if /i "%~1"=="__deltask" goto :deltask

set "S_APP=propusheno"
set "S_MIHOMO=propusheno"
set "S_AUTORUN=propusheno"
set "S_TASK=propusheno"
set "S_DATA=sohraneny"

cls
echo ============================================================
echo    Mihomo Launcher - ostanovka i udalenie
echo ============================================================
echo.

echo [1/4] Zakryvayu prilozhenie MihomoLauncher...
taskkill /F /IM "MihomoLauncher.exe" /T >nul 2>&1
if !errorlevel! equ 0 (set "S_APP=zakryto") else (set "S_APP=ne bylo zapuscheno")
echo        !S_APP!

echo [2/4] Ostanavlivayu dvizhok mihomo (bez prav admina)...
schtasks /End /TN "MihomoLauncher_mihomo" >nul 2>&1
taskkill /F /IM "mihomo-windows-amd64.exe" /T >nul 2>&1
taskkill /F /IM "mihomo.exe" /T >nul 2>&1
tasklist /FI "IMAGENAME eq mihomo-windows-amd64.exe" 2>nul | find /i "mihomo" >nul
if !errorlevel! equ 0 (set "S_MIHOMO=NE udalos ostanovit") else (set "S_MIHOMO=ostanovlen")
echo        !S_MIHOMO!

echo [3/4] Ubirayu iz avtozapuska...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "MihomoLauncher" /f >nul 2>&1
if !errorlevel! equ 0 (set "S_AUTORUN=udalen") else (set "S_AUTORUN=zapisi ne bylo")
del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\MihomoLauncher*.lnk" >nul 2>&1
echo        !S_AUTORUN!

echo [4/4] Udalyayu zadachu planirovshika...
schtasks /Query /TN "MihomoLauncher_mihomo" >nul 2>&1
if !errorlevel! neq 0 (
    set "S_TASK=zadachi net"
) else (
    schtasks /Delete /TN "MihomoLauncher_mihomo" /F >nul 2>&1
    if !errorlevel! equ 0 (
        set "S_TASK=udalena"
    ) else (
        echo        nuzhny prava administratora - podtverdite zapros UAC...
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '__deltask' -Verb RunAs -Wait" >nul 2>&1
        schtasks /Query /TN "MihomoLauncher_mihomo" >nul 2>&1
        if !errorlevel! neq 0 (set "S_TASK=udalena") else (set "S_TASK=NE udalena - UAC otklonen?")
    )
)
echo        !S_TASK!

if /i "%~1"=="all" (
    rmdir /s /q "%LOCALAPPDATA%\MihomoLauncher" >nul 2>&1
    if exist "%LOCALAPPDATA%\MihomoLauncher" (set "S_DATA=NE udalos udalit") else (set "S_DATA=udaleny")
)

echo.
echo ============================================================
echo    GOTOVO. Itog udaleniya:
echo ============================================================
echo      Prilozhenie:     !S_APP!
echo      Dvizhok mihomo:  !S_MIHOMO!
echo      Avtozapusk:      !S_AUTORUN!
echo      Zadacha mihomo:  !S_TASK!
echo      Dannye/nastr.:   !S_DATA!
echo ------------------------------------------------------------
echo    Mihomo Launcher ostanovlen i snyat s avtozapuska.
echo    Chtoby steret i nastroyki tozhe - zapustite:  uninstall.bat all
echo ============================================================
echo.
echo Skript zavershilsya korrektno. Nazhmite lyubuyu klavishu chtoby zakryt...
pause >nul
exit /b

rem ==== Vhod pod adminom: ostanovit i udalit zadachu ====
:deltask
schtasks /End /TN "MihomoLauncher_mihomo" >nul 2>&1
taskkill /F /IM "mihomo-windows-amd64.exe" /T >nul 2>&1
schtasks /Delete /TN "MihomoLauncher_mihomo" /F >nul 2>&1
exit /b
