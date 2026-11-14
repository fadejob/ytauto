# YtAuto desktop ilova saqlagan online baza (Supabase) ulanish satrini bot/.env.local ga ko'chiradi.
# Satr faqat shu Windows foydalanuvchisi ochadigan DPAPI shifrida — shu kompyuterda, shu foydalanuvchi ishga tushirsin.
# Qiymat ekranga chiqarilmaydi. Ishlatish (repo ildizidan):
#   powershell -NoProfile -ExecutionPolicy Bypass -File bot\scripts\import-db-from-app.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$data = if ($env:YTAUTO_DATA) { $env:YTAUTO_DATA } else { Join-Path $env:LOCALAPPDATA 'YtAuto' }
$sha = [System.Security.Cryptography.SHA256]::Create()
$name = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('postgres_connection'))) -replace '-', '').Substring(0, 24).ToLower()
$file = Join-Path $data "secrets\$name.bin"
if (-not (Test-Path $file)) { throw "Ilovada online baza ulanmagan: Sozlamalar -> Online baza." }

$bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
  [IO.File]::ReadAllBytes($file), [Text.Encoding]::UTF8.GetBytes('YtAuto.v1.secret-store'), 'CurrentUser')
$conn = [Text.Encoding]::UTF8.GetString($bytes).Replace('"', '\"')

$envFile = Join-Path $PSScriptRoot '..\.env.local'
$lines = if (Test-Path $envFile) { [IO.File]::ReadAllLines($envFile) } else { @() }
$out = New-Object System.Collections.Generic.List[string]
$done = $false
foreach ($l in $lines) {
  if ($l -match '^DATABASE_URL=') { $out.Add("DATABASE_URL=""$conn"""); $done = $true }
  elseif ($l -match '^DEMO_MODE=') { $out.Add('DEMO_MODE="0"') }
  else { $out.Add($l) }
}
if (-not $done) { $out.Add("DATABASE_URL=""$conn""") }
[IO.File]::WriteAllLines((Resolve-Path -LiteralPath (Split-Path $envFile)).Path + '\.env.local', $out, (New-Object Text.UTF8Encoding $false))
Write-Host "Tayyor: DATABASE_URL bot/.env.local ga yozildi, DEMO_MODE=0. (Qiymat ko'rsatilmaydi.)"
