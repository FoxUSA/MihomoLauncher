# Regenerate the shipped DataUser\rules baseline: the merged rule-template catalog built from
#   * blackmatrix7  https://byfox.dev/data/maxmind/blackmatrix7+www.txt   (Name|CheckUrl)
#   * byfox folders https://byfox.dev/data/awg-panel/rules/folders.txt    (folder = name = site)
# Output format matches Services\BlackMatrixCatalog.BuildMergedContent: a leading <!--date -->
# comment block then "Name|CheckUrl|ClashUrl" per line (empty ClashUrl => derived from the name).
# Writes <repo>\rules. Run from the repo root:  powershell -ExecutionPolicy Bypass -File build\gen-rules.ps1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$outPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\rules"))

$bm = (Invoke-WebRequest -Uri "https://byfox.dev/data/maxmind/blackmatrix7+www.txt" -UseBasicParsing).Content
$foldersRaw = (Invoke-WebRequest -Uri "https://byfox.dev/data/awg-panel/rules/folders.txt" -UseBasicParsing).Content

$rows = [System.Collections.Generic.List[object]]::new()
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$date = (Get-Date).ToString("dd.MM.yyyy")
$inComment = $false
foreach ($raw in ($bm -replace "`r`n","`n").Split("`n")) {
  $line = $raw.Trim()
  if ($line -eq "") { continue }
  if (-not $inComment -and $line.StartsWith("<!--")) {
    $m = [regex]::Match($line, '(\d{2}\.\d{2}\.\d{4})'); if ($m.Success) { $date = $m.Value }
    if ($line -notmatch '-->') { $inComment = $true }; continue
  }
  if ($inComment) { if ($line -match '-->') { $inComment = $false }; continue }
  if ($line.StartsWith('#')) { continue }
  $cols = $line.Split('|')
  $name = $cols[0].Trim(); $check = if ($cols.Length -gt 1) { $cols[1].Trim() } else { "" }
  if ($name -eq "" -or -not $seen.Add($name)) { continue }
  $rows.Add([pscustomobject]@{ Name=$name; Check=$check; Clash="" })   # blackmatrix clash derived => empty
}
$bmCount = $rows.Count

foreach ($raw in ($foldersRaw -replace "`r`n","`n").Split("`n")) {
  $f = $raw.Trim()
  if ($f -eq "" -or $f.StartsWith('#') -or $f.StartsWith('<!--') -or $f.StartsWith('-->')) { continue }
  if (-not $seen.Add($f)) { continue }
  $check = if ($f.IndexOf(' ') -lt 0 -and $f.IndexOf('.') -ge 0) { "https://" + $f.ToLower() + "/" } else { "" }
  $clash = "https://byfox.dev/data/awg-panel/rules/" + [uri]::EscapeDataString($f) + "/clash/rules.yaml"
  $rows.Add([pscustomobject]@{ Name=$f; Check=$check; Clash=$clash })
}
$byfoxCount = $rows.Count - $bmCount

$sorted = $rows | Sort-Object -Property @{Expression={$_.Name.ToLower()}}
$sb = [System.Text.StringBuilder]::new()
[void]$sb.Append("<!--$date`n")
[void]$sb.Append("# Merged rule catalog - blackmatrix7 (byfox.dev/data/maxmind/blackmatrix7+www.txt)`n")
[void]$sb.Append("# + byfox awg-panel folders (byfox.dev/data/awg-panel/rules/folders.txt)`n")
[void]$sb.Append("# Format: Name|CheckUrl|ClashUrl  (ClashUrl empty => derived from name)`n")
[void]$sb.Append("-->`n")
foreach ($r in $sorted) { [void]$sb.Append("$($r.Name)|$($r.Check)|$($r.Clash)`n") }

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8)
Write-Host "rules written: $($sorted.Count) rows ($bmCount blackmatrix + $byfoxCount byfox), date $date"
