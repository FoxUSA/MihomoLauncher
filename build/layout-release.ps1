param(
    [Parameter(Mandatory=$true)][string]$OutDir,
    # Master geo folder on the dev machine, refreshed by the MaxMind update script. The
    # release always ships the copy from here when it is newer than what is staged, so a
    # published build never goes out with a stale GeoLite2 DB. Falls back to bin\maxmind.
    # Default: <repo>\..\..\maxmind (next to the project tree), or $env:MIHOMOLAUNCHER_GEO_SOURCE.
    [string]$GeoSource = ''
)
if (-not $GeoSource) { $GeoSource = $env:MIHOMOLAUNCHER_GEO_SOURCE }
if (-not $GeoSource) { $GeoSource = Join-Path $PSScriptRoot '..\..\..\maxmind' }

# Release layout: keep ONLY "Mihomo Launcher.exe" in the output root and move every other build
# artifact (dependency DLLs, .config, pdb, manifest.json, icons, etc.) into Data\. App.cs
# resolves those DLLs from Data\ via AssemblyResolve, so no .config is needed next to the exe.
#
# User data (config, geo DB, service list, manifest_geo.json) lives in DataUser\ — it SURVIVES
# an update (which wipes Data\ + the exe). help.html / uninstall.bat are shipped into
# Data\ via csproj Links; the launcher creates "help" / "uninstall" shortcuts in the root at
# runtime (FolderIcons). Idempotent — safe to run after every Release build.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $OutDir).Path
$data = Join-Path $root 'Data'
$dataUser = Join-Path $root 'DataUser'
New-Item -ItemType Directory -Force -Path $data | Out-Null
New-Item -ItemType Directory -Force -Path $dataUser | Out-Null

# The launcher ships as "Mihomo Launcher.exe" (2026-11-19). The assembly stays MihomoLauncher
# (namespaces, pack URIs), so MSBuild writes MihomoLauncher.exe and it is renamed here. The old
# name lives on as a tiny forwarder, built below into bin\Release\legacy\ and added to
# update.zip only (make-zips.bat) - see build\launchers\legacy.cs.
$exeName = 'Mihomo Launcher.exe'
$built = Join-Path $root 'MihomoLauncher.exe'
if (Test-Path -LiteralPath $built) {
    for ($try = 1; ; $try++) {
        try { Move-Item -LiteralPath $built -Destination (Join-Path $root $exeName) -Force; break }
        catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
    }
}
# uninstall.exe stays too (rebuilt below): moving last build's copy into Data\ left a stale
# Data\uninstall.exe in every release. help.exe is gone (2026-11-19) - drop it here as well,
# or this very loop would carry the old one into Data\.
$keepInRoot = @($exeName, 'uninstall.exe')
foreach ($n in 'help.exe') { Remove-Item -LiteralPath (Join-Path $root $n) -Force -ErrorAction SilentlyContinue }
foreach ($n in 'help.exe','help.ico','uninstall.exe') { Remove-Item -LiteralPath (Join-Path $data $n) -Force -ErrorAction SilentlyContinue }

# Keep ONLY the launcher exe in the root; everything else (DLLs, pdb, manifest, icons, Data\
# content...) is either already under Data\/DataUser\ or gets moved into Data\. DataUser\ is
# never touched.
Get-ChildItem -LiteralPath $root -Force |
    Where-Object { $_.Name -ne 'Data' -and $_.Name -ne 'DataUser' -and ($keepInRoot -notcontains $_.Name) } |
    ForEach-Object {
        $dest = Join-Path $data $_.Name
        # The output lives under Dropbox, and a file the build has only just written is often
        # held for a moment (Dropbox indexing it, a scanner reading it). One failed move used to
        # fail the whole Release build and leave the DLLs strewn in the root. Retry briefly.
        for ($try = 1; ; $try++) {
            try {
                if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
                Move-Item -LiteralPath $_.FullName -Destination $dest -Force
                break
            } catch {
                if ($try -ge 10) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
    }

# Geo DB lives in DataUser\maxmind (survives updates). It's not a build artifact, so seed it
# from the working copy kept at bin\maxmind when DataUser\maxmind doesn't have it yet. The app
# also auto-updates it. (Legacy builds kept it in Data\maxmind — migrate that too.)
$geoDst = Join-Path $dataUser 'maxmind'
$geoDb  = Join-Path $geoDst 'GeoLite2-City.mmdb'
New-Item -ItemType Directory -Force -Path $geoDst | Out-Null

# ALWAYS take the master copy - both the DB and its date marker. No timestamp comparison:
# whatever sits in the master folder is by definition what this release ships, and the marker
# has to travel WITH the DB or the app has no way to know what it just received (a build that
# copied the DB but left an older marker behind is exactly how a fresh release ended up asking
# to update itself on first run).
$masterDb = Join-Path $GeoSource 'GeoLite2-City.mmdb'
if (Test-Path -LiteralPath $masterDb) {
    Copy-Item -LiteralPath $masterDb -Destination $geoDb -Force
    $stamp = Join-Path $GeoSource 'geolite2_data.txt'
    $ver = '?'
    if (Test-Path -LiteralPath $stamp) {
        Copy-Item -LiteralPath $stamp -Destination (Join-Path $geoDst 'geolite2_data.txt') -Force
        $ver = (Get-Content -LiteralPath $stamp -Raw).Trim()
    } else {
        Write-Host "[layout-release] WARNING: $GeoSource has no geolite2_data.txt - shipping the DB without a date marker"
    }
    Write-Host "[layout-release] geo DB + marker copied from $GeoSource (version $ver)"
}

if (-not (Test-Path -LiteralPath $geoDb)) {
    $binMax = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'maxmind'   # bin\maxmind
    $legacyMax = Join-Path $data 'maxmind'
    if (Test-Path -LiteralPath (Join-Path $binMax 'GeoLite2-City.mmdb')) {
        Copy-Item -Path (Join-Path $binMax '*') -Destination $geoDst -Recurse -Force
        Write-Host "[layout-release] seeded geo DB from bin\maxmind -> DataUser\maxmind"
    } elseif (Test-Path -LiteralPath (Join-Path $legacyMax 'GeoLite2-City.mmdb')) {
        Copy-Item -Path (Join-Path $legacyMax '*') -Destination $geoDst -Recurse -Force
        Write-Host "[layout-release] migrated geo DB from Data\maxmind -> DataUser\maxmind"
    } else {
        Write-Host "[layout-release] WARNING: no geo DB found to seed DataUser\maxmind"
    }
}

# --- Rule-template catalog ---
# One file, DataUser\templates.json — the same one the app's daily refresh overwrites. Every
# release takes it fresh from api.byfox.dev (gen-rules.ps1 updates <repo>\templates.json); with
# the API unreachable the build goes on with the repo copy it already has.
$repoTemplates = Join-Path $PSScriptRoot '..\templates.json'
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'gen-rules.ps1')
    if ($LASTEXITCODE -ne 0) { throw "gen-rules.ps1 exit $LASTEXITCODE" }
} catch {
    Write-Host "[layout-release] WARNING: templates.json not refreshed ($($_.Exception.Message)) - shipping the repo copy"
}
if (Test-Path -LiteralPath $repoTemplates) {
    Copy-Item -LiteralPath $repoTemplates -Destination (Join-Path $dataUser 'templates.json') -Force
    Write-Host "[layout-release] templates.json -> DataUser\templates.json"
} else {
    Write-Host "[layout-release] WARNING: no templates.json to ship"
}
# Older builds shipped it in Data\, and before that as the merged DataUser\rules file.
Remove-Item -LiteralPath (Join-Path $data 'templates.json') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $dataUser 'rules') -Force -ErrorAction SilentlyContinue

# Drop any legacy Data\maxmind so we don't ship the 65 MB DB twice.
$legacyMax = Join-Path $data 'maxmind'
if (Test-Path -LiteralPath $legacyMax) { Remove-Item -LiteralPath $legacyMax -Recurse -Force }

# Scrub USER/RUNTIME files from DataUser so the distributable ships DEFAULTS only — these
# appear if the exe is ever run from the build folder (config, custom icons, downloaded engine,
# logs, config pointer). The baseline that SHOULD ship stays: manifest_geo.json,
# blackmatrix7+www.txt, maxmind\, desktop.ini. The app recreates a default config on first run.
foreach ($u in 'config.xml','config-location.txt','update.log') {
    Remove-Item -LiteralPath (Join-Path $dataUser $u) -Force -ErrorAction SilentlyContinue
}

# The engine SHIPS in the full archive (user's call, 2026-11-12): the newest mihomo, taken ONLY
# from api.byfox.dev/engine/ (the same source the app uses), zip and exe sha256-checked against
# engine/manifest.json. Cached in bin\engine-cache with its version — never learned by running
# the binary: a second process named mihomo-windows-amd64 makes the live launcher restart its
# engine. Downloaded only when the API has a newer version; offline -> the cached one, warned.
$engineName  = 'mihomo-windows-amd64.exe'
$engineCache = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'engine-cache'
$cacheExe    = Join-Path $engineCache $engineName
$cacheVer    = Join-Path $engineCache 'version.txt'
New-Item -ItemType Directory -Force -Path $engineCache | Out-Null
$haveVer = if (Test-Path -LiteralPath $cacheVer) { (Get-Content -LiteralPath $cacheVer -Raw).Trim() } else { '' }
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ua = @{ 'User-Agent' = 'MihomoLauncher-build' }
    $em = Invoke-RestMethod -Uri 'https://api.byfox.dev/engine/manifest.json' -Headers $ua -TimeoutSec 30
    $ver = ([string]$em.mihomo.version).Trim().TrimStart('v')
    if (-not $ver) { throw 'engine/manifest.json has no mihomo.version' }
    $zipInfo = $em.mihomo.files.'mihomo-windows-amd64.zip'
    $exeInfo = $em.mihomo.files.'mihomo-windows-amd64.exe'
    if ($ver -ne $haveVer -or -not (Test-Path -LiteralPath $cacheExe)) {
        $tmpZip = Join-Path $engineCache 'download.zip'
        $tmpExe = Join-Path $engineCache 'download.exe'
        Invoke-WebRequest -Uri 'https://api.byfox.dev/engine/mihomo-windows-amd64.zip' -Headers $ua -OutFile $tmpZip -UseBasicParsing -TimeoutSec 300
        $zsha = (Get-FileHash -LiteralPath $tmpZip -Algorithm SHA256).Hash.ToLower()
        if ($zsha -ne ([string]$zipInfo.sha256).ToLower()) { Remove-Item -LiteralPath $tmpZip -Force; throw "zip sha256 mismatch ($zsha)" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $za = [IO.Compression.ZipFile]::OpenRead($tmpZip)
        try {
            $entry = $za.Entries | Where-Object { $_.Name -ieq $engineName } | Select-Object -First 1
            if (-not $entry) { throw "$engineName not in the zip" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmpExe, $true)
        } finally { $za.Dispose(); Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue }
        $esha = (Get-FileHash -LiteralPath $tmpExe -Algorithm SHA256).Hash.ToLower()
        if ($exeInfo.sha256 -and $esha -ne ([string]$exeInfo.sha256).ToLower()) { Remove-Item -LiteralPath $tmpExe -Force; throw "exe sha256 mismatch ($esha)" }
        Move-Item -LiteralPath $tmpExe -Destination $cacheExe -Force
        Set-Content -LiteralPath $cacheVer -Value $ver -Encoding ASCII
        $haveVer = $ver
        Write-Host "[layout-release] engine: downloaded mihomo v$ver from api.byfox.dev, sha256 ok"
    } else {
        Write-Host "[layout-release] engine: mihomo v$ver is the latest on api.byfox.dev, cached"
    }
} catch {
    Write-Host "[layout-release] WARNING: api.byfox.dev engine check failed ($($_.Exception.Message)) - shipping cached v$haveVer"
}
if (Test-Path -LiteralPath $cacheExe) {
    Copy-Item -LiteralPath $cacheExe -Destination (Join-Path $dataUser $engineName) -Force
    Write-Host "[layout-release] engine: DataUser\$engineName = mihomo v$haveVer"
} else {
    Write-Host "[layout-release] WARNING: no engine to ship (api.byfox.dev unreachable and nothing cached)"
}
Remove-Item -LiteralPath (Join-Path $dataUser 'icons') -Recurse -Force -ErrorAction SilentlyContinue
# Downloaded at runtime: the cached parts of api.byfox.dev's search index.
Remove-Item -LiteralPath (Join-Path $dataUser 'rule-index') -Recurse -Force -ErrorAction SilentlyContinue
# Runtime logs aren't shipped either. They live in DataUser\logs now (Data\ is wiped by an
# update, which is exactly when the log matters); the Data\ path is cleaned up for old trees.
Remove-Item -LiteralPath (Join-Path $dataUser 'logs') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $data 'logs') -Recurse -Force -ErrorAction SilentlyContinue

# --- Folder icons (desktop.ini, RELATIVE paths so they survive the folder being moved /
#     copied to another PC) + tiny launcher EXEs in the root with EMBEDDED icons. A .bat/.lnk
#     can't show a custom Explorer icon (.bat) or breaks on move (.lnk absolute paths), so
#     uninstall.exe carries trash.ico embedded and starts the uninstaller via its own
#     directory (portable, icon shows with no first launch). ---

function Set-FolderIcon([string]$folder, [string]$iconRel) {
    if (-not (Test-Path -LiteralPath $folder)) { return }
    $ini = Join-Path $folder 'desktop.ini'
    if (Test-Path -LiteralPath $ini) { & attrib -h -s -r "$ini" 2>$null | Out-Null }
    $text = "[.ShellClassInfo]`r`nIconResource=$iconRel,0`r`n[ViewState]`r`nMode=`r`nVid=`r`nFolderType=Generic`r`n"
    Set-Content -LiteralPath $ini -Value $text -Encoding ASCII
    & attrib +h +s "$ini" 2>$null | Out-Null
    & attrib +r "$folder" 2>$null | Out-Null
}

# Remove stale launchers from older builds (.lnk / .bat).
# help.exe was dropped on 2026-11-19 (the user asked to remove it from the project).
foreach ($n in 'help.exe','help.lnk','uninstall.lnk','help.bat','uninstall.bat') {
    Remove-Item -LiteralPath (Join-Path $root $n) -Force -ErrorAction SilentlyContinue
}

# Compile the launcher EXEs with their icons embedded (framework csc, no SDK needed).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$launchers = Join-Path $PSScriptRoot 'launchers'
if (Test-Path -LiteralPath $csc) {
    if (Test-Path -LiteralPath (Join-Path $data 'trash.ico')) {
        & $csc /nologo /target:winexe /win32icon:"$(Join-Path $data 'trash.ico')" /out:"$(Join-Path $root 'uninstall.exe')" "$(Join-Path $launchers 'uninstall.cs')" | Out-Null
    }
    # The engine's Windows service host (MihomoSetup.Service). In Data\ - an update replaces it
    # with the rest of the app; the service stops itself when idle, so the file is not locked.
    & $csc /nologo /target:winexe /codepage:65001 /r:System.ServiceProcess.dll /win32icon:"$(Join-Path $data 'fox.ico')" /out:"$(Join-Path $data 'service.exe')" "$(Join-Path $launchers 'service.cs')" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "[layout-release] WARNING: service.exe not built" }
    # The old-name forwarder, beside the output folder (never inside: the full archive must not
    # carry it). make-zips.bat puts it into update.zip as <folder>\MihomoLauncher.exe.
    $legacyDir = Join-Path (Split-Path $root -Parent) 'legacy'
    New-Item -ItemType Directory -Force -Path $legacyDir | Out-Null
    & $csc /nologo /target:winexe /codepage:65001 /win32icon:"$(Join-Path $data 'fox.ico')" /out:"$(Join-Path $legacyDir 'MihomoLauncher.exe')" "$(Join-Path $launchers 'legacy.cs')" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "legacy forwarder (MihomoLauncher.exe) not built - update.zip would break 3.1.0 updates" }
} else {
    Write-Host "[layout-release] WARNING: csc.exe not found - uninstall.exe/service.exe/forwarder not built"
}

# ---- code signing (optional) --------------------------------------------------
# Defender's ML (Wacatac.B!ml / Sabsik.FL.A!ml) scores an UNSIGNED binary on its own
# hash, which is brand new on every build - so a clean release can be flagged while the
# previous one was not. A signature moves reputation from the hash to the certificate
# and is the only durable fix. Nothing here fails the build when no cert is configured:
# set MIHOMOLAUNCHER_SIGN_THUMBPRINT (a cert in the user's store) or _PFX + _PFX_PASS.
$signThumb = $env:MIHOMOLAUNCHER_SIGN_THUMBPRINT
$signPfx   = $env:MIHOMOLAUNCHER_SIGN_PFX
if ($signThumb -or $signPfx) {
    $signtool = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like '*\x64\*' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $signtool) {
        Write-Host "[layout-release] WARNING: signtool.exe not found - release left UNSIGNED"
    } else {
        $targets = @((Join-Path $root $exeName), (Join-Path $root 'uninstall.exe'), (Join-Path $data 'service.exe'), (Join-Path (Split-Path $root -Parent) 'legacy\MihomoLauncher.exe')) |
                   Where-Object { Test-Path -LiteralPath $_ }
        $signArgs = @('sign','/fd','SHA256','/tr','http://timestamp.digicert.com','/td','SHA256')
        if ($signThumb) { $signArgs += @('/sha1', $signThumb) }
        else            { $signArgs += @('/f', $signPfx); if ($env:MIHOMOLAUNCHER_SIGN_PFX_PASS) { $signArgs += @('/p', $env:MIHOMOLAUNCHER_SIGN_PFX_PASS) } }
        foreach ($t in $targets) {
            & $signtool.FullName ($signArgs + $t) | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Host "[layout-release] signed $(Split-Path $t -Leaf)" }
            else { Write-Host "[layout-release] WARNING: signing failed for $(Split-Path $t -Leaf) (exit $LASTEXITCODE)" }
        }
    }
}

# Icon index (2026-11-16): regenerated from ico\ + icon-aliases.txt on every Release, shipped in
# Data\ AND put beside the zips in bin\Release — mirrored from there to
# /var/www/byfox.dev/data/mihomolauncher/icons.txt, where installed launchers read it (IconFetcher).
try {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    & (Join-Path $PSScriptRoot 'gen-icons.ps1')
    $idx = Join-Path $repo 'icons.txt'
    Copy-Item -LiteralPath $idx -Destination (Join-Path $data 'icons.txt') -Force
    Copy-Item -LiteralPath $idx -Destination (Join-Path (Split-Path $root -Parent) 'icons.txt') -Force
    Write-Host "[layout-release] icons.txt -> Data\ and bin\Release\ (upload beside manifest.json)"
} catch { Write-Host "[layout-release] WARNING: icons.txt not refreshed: $($_.Exception.Message)" }

Set-FolderIcon $data     'data.ico'
Set-FolderIcon $dataUser '..\Data\user_data.ico'
Set-FolderIcon $root     'Data\fox.ico'

Write-Host "[layout-release] root = $exeName + uninstall.exe; Data\ = app; DataUser\ = user data; legacy\MihomoLauncher.exe = forwarder for update.zip"
