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

# Release layout: keep ONLY MihomoLauncher.exe in the output root and move every other build
# artifact (dependency DLLs, .config, pdb, manifest.json, icons, etc.) into Data\. App.cs
# resolves those DLLs from Data\ via AssemblyResolve, so no .config is needed next to the exe.
#
# User data (config, geo DB, service list, manifest_geo.json) lives in DataUser\ — it SURVIVES
# an update (which wipes Data\ + MihomoLauncher.exe). help.html / uninstall.bat are shipped into
# Data\ via csproj Links; the launcher creates "help" / "uninstall" shortcuts in the root at
# runtime (FolderIcons). Idempotent — safe to run after every Release build.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $OutDir).Path
$data = Join-Path $root 'Data'
$dataUser = Join-Path $root 'DataUser'
New-Item -ItemType Directory -Force -Path $data | Out-Null
New-Item -ItemType Directory -Force -Path $dataUser | Out-Null

$keepInRoot = @('MihomoLauncher.exe')

# Keep ONLY MihomoLauncher.exe in the root; everything else (DLLs, pdb, manifest, icons, Data\
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

# --- Rule-template baseline (DataUser\rules) ---
# Same idea as the geo DB: ship what the master folder has, not whatever was generated once.
# Only the blackmatrix7 half comes from there; the byfox awg-panel rows already in the staged
# file are KEPT (they come from a different source and would otherwise be silently dropped).
# The catalog date is the first line of the master file, "<!--dd.MM.yyyy-->".
$rulesDst = Join-Path $dataUser 'rules'
$bmMaster = Join-Path $GeoSource 'blackmatrix7+www.txt'
if ((Test-Path -LiteralPath $bmMaster) -and (Test-Path -LiteralPath $rulesDst)) {
    function Get-CatalogDate([string]$firstLine) {
        $m = [regex]::Match($firstLine, '(\d{2})\.(\d{2})\.(\d{4})')
        if ($m.Success) { return [datetime]::new([int]$m.Groups[3].Value, [int]$m.Groups[2].Value, [int]$m.Groups[1].Value) }
        return [datetime]::MinValue
    }
    $masterLines = [System.IO.File]::ReadAllLines($bmMaster)
    $stagedLines = [System.IO.File]::ReadAllLines($rulesDst)
    $masterDate = Get-CatalogDate $masterLines[0]
    $stagedDate = Get-CatalogDate $stagedLines[0]

    if ($masterDate -gt $stagedDate) {
        # Rows already shipped, by name, so byfox entries and their explicit ClashUrl survive.
        $staged = [ordered]@{}
        foreach ($line in $stagedLines) {
            $t = $line.Trim()
            if ($t -eq '' -or $t.StartsWith('<!--') -or $t.StartsWith('#') -or $t.StartsWith('-->')) { continue }
            $name = $t.Split('|')[0]
            if ($name -ne '' -and -not $staged.Contains($name)) { $staged[$name] = $t }
        }
        # Master rows are "Name|CheckUrl"; the shipped format adds an empty ClashUrl column.
        $merged = [ordered]@{}
        foreach ($line in $masterLines) {
            $t = $line.Trim()
            if ($t -eq '' -or $t.StartsWith('<!--') -or $t.StartsWith('#') -or $t.StartsWith('-->')) { continue }
            $parts = $t.Split('|')
            $name = $parts[0]
            if ($name -eq '' -or $merged.Contains($name)) { continue }
            $check = if ($parts.Count -gt 1) { $parts[1] } else { '' }
            $clash = if ($parts.Count -gt 2) { $parts[2] } else { '' }
            $merged[$name] = "$name|$check|$clash"
        }
        foreach ($name in $staged.Keys) { if (-not $merged.Contains($name)) { $merged[$name] = $staged[$name] } }

        $stamp = $masterDate.ToString('dd.MM.yyyy')
        $out = [System.Collections.Generic.List[string]]::new()
        $out.Add("<!--$stamp")
        $out.Add('# Merged rule catalog - blackmatrix7 (byfox.dev/data/maxmind/blackmatrix7+www.txt)')
        $out.Add('# + byfox awg-panel folders (byfox.dev/data/awg-panel/rules/folders.txt)')
        $out.Add('# Format: Name|CheckUrl|ClashUrl  (ClashUrl empty => derived from name)')
        $out.Add('-->')
        foreach ($name in ($merged.Keys | Sort-Object)) { $out.Add($merged[$name]) }
        [System.IO.File]::WriteAllLines($rulesDst, $out, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "[layout-release] rule templates refreshed from $bmMaster (version $stamp, $($merged.Count) entries)"
    } else {
        Write-Host "[layout-release] rule templates already current"
    }
}

# Drop any legacy Data\maxmind so we don't ship the 65 MB DB twice.
$legacyMax = Join-Path $data 'maxmind'
if (Test-Path -LiteralPath $legacyMax) { Remove-Item -LiteralPath $legacyMax -Recurse -Force }

# Scrub USER/RUNTIME files from DataUser so the distributable ships DEFAULTS only — these
# appear if the exe is ever run from the build folder (config, custom icons, downloaded engine,
# logs, config pointer). The baseline that SHOULD ship stays: manifest_geo.json,
# blackmatrix7+www.txt, maxmind\, desktop.ini. The app recreates a default config on first run.
foreach ($u in 'config.xml','config-location.txt','update.log','mihomo-windows-amd64.exe') {
    Remove-Item -LiteralPath (Join-Path $dataUser $u) -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath (Join-Path $dataUser 'icons') -Recurse -Force -ErrorAction SilentlyContinue
# Runtime logs aren't shipped either. They live in DataUser\logs now (Data\ is wiped by an
# update, which is exactly when the log matters); the Data\ path is cleaned up for old trees.
Remove-Item -LiteralPath (Join-Path $dataUser 'logs') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $data 'logs') -Recurse -Force -ErrorAction SilentlyContinue

# --- Folder icons (desktop.ini, RELATIVE paths so they survive the folder being moved /
#     copied to another PC) + tiny launcher EXEs in the root with EMBEDDED icons. A .bat/.lnk
#     can't show a custom Explorer icon (.bat) or breaks on move (.lnk absolute paths), so
#     help.exe/uninstall.exe carry help.ico/trash.ico embedded and open the real files in
#     Data\ via their own directory (portable, icon shows with no first launch). ---

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
foreach ($n in 'help.lnk','uninstall.lnk','help.bat','uninstall.bat') {
    Remove-Item -LiteralPath (Join-Path $root $n) -Force -ErrorAction SilentlyContinue
}

# Compile the launcher EXEs with their icons embedded (framework csc, no SDK needed).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$launchers = Join-Path $PSScriptRoot 'launchers'
if (Test-Path -LiteralPath $csc) {
    if (Test-Path -LiteralPath (Join-Path $data 'help.ico')) {
        & $csc /nologo /target:winexe /win32icon:"$(Join-Path $data 'help.ico')" /out:"$(Join-Path $root 'help.exe')" "$(Join-Path $launchers 'help.cs')" | Out-Null
    }
    if (Test-Path -LiteralPath (Join-Path $data 'trash.ico')) {
        & $csc /nologo /target:winexe /win32icon:"$(Join-Path $data 'trash.ico')" /out:"$(Join-Path $root 'uninstall.exe')" "$(Join-Path $launchers 'uninstall.cs')" | Out-Null
    }
} else {
    Write-Host "[layout-release] WARNING: csc.exe not found - help.exe/uninstall.exe not built"
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
        $targets = @((Join-Path $root 'MihomoLauncher.exe'), (Join-Path $root 'help.exe'), (Join-Path $root 'uninstall.exe')) |
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

Set-FolderIcon $data     'data.ico'
Set-FolderIcon $dataUser '..\Data\user_data.ico'
Set-FolderIcon $root     'Data\fox.ico'

Write-Host "[layout-release] root = MihomoLauncher.exe + help.exe/uninstall.exe; Data\ = app; DataUser\ = user data"
