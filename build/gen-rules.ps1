# Regenerate the shipped rule-template catalog: <repo>\templates.json, a verbatim copy of
# https://api.byfox.dev/rule/templates.json (blackmatrix7 mirror + byfox templates, titles,
# descriptions, list paths). It ships in Data\ as the offline baseline; the app refreshes
# DataUser\templates.json daily on its own. The download is checked against the sha256 in
# rule/manifest.json before it replaces anything.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File build\gen-rules.ps1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$outPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\templates.json"))
$base = "https://api.byfox.dev/rule/"
$ua = "MihomoLauncher/build"

$manifest = Invoke-RestMethod -Uri ($base + "manifest.json") -UserAgent $ua
$want = $manifest.files.'templates.json'.sha256

$tmp = $outPath + ".new"
Invoke-WebRequest -Uri ($base + "templates.json") -OutFile $tmp -UseBasicParsing -UserAgent $ua
$got = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
if ($want -and $got -ne $want.ToLowerInvariant()) {
    Remove-Item -LiteralPath $tmp -Force
    throw "templates.json sha256 mismatch (manifest $want, got $got) - kept the old file"
}
Move-Item -LiteralPath $tmp -Destination $outPath -Force
$j = Get-Content -LiteralPath $outPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "templates.json written: $($j.count) templates ($($j.hidden) hidden), date $($j.date)"
