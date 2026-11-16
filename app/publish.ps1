# YtAuto distributivi: BITTA exe (self-contained single-file, win-x64). Foydalanuvchidan .NET talab qilinmaydi.
# Natija: dist\YtAuto.exe - oyna (sozlamalar) ham, oynasiz worker ham shu exe ichida:
#   YtAuto.exe              -> sozlamalar oynasi
#   YtAuto.exe --poll       -> yengil worker (Task Scheduler, har 10 daqiqa)
#   YtAuto.exe --scheduled  -> kunlik ish (ish oynasi 01:00-04:00)
#   YtAuto.exe --batch-now  -> botdan "Hozir ishga tushirish"
param(
  [string]$Dotnet = $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'D:\dotnet-sdk\dotnet.exe' }),
  [string]$Out = "$PSScriptRoot\dist",
  # CI: har build uchun versiya (masalan 1.0.42). Bo'sh bo'lsa Directory.Build.props dagi versiya.
  [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
if ($Dotnet -like 'D:\dotnet-sdk*') { $env:DOTNET_ROOT = 'D:\dotnet-sdk'; $env:NUGET_PACKAGES = 'D:\dotnet-sdk\nuget-packages' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$tmp = Join-Path $Out 'publish-tmp'
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null

$publishArgs = @(
  'publish', "$PSScriptRoot\src\YtAuto.Desktop\YtAuto.Desktop.csproj",
  '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  # WPF va SQLite'ning native kutubxonalari ham exe ichiga (birinchi ishga tushishda vaqtinchalik papkaga chiqariladi).
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none', '-p:SatelliteResourceLanguages=en',
  '-o', $tmp
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
& $Dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish xato" }

# Faqat bitta exe chiqariladi (align_words.py exe ichida ham bor, kerak bo'lsa o'zi diskka chiqaradi).
Copy-Item (Join-Path $tmp 'YtAuto.exe') (Join-Path $Out 'YtAuto.exe') -Force
$extra = Get-ChildItem $tmp -Recurse -File | Where-Object { $_.Name -ne 'YtAuto.exe' } | ForEach-Object { $_.FullName.Substring($tmp.Length + 1) }
Remove-Item $tmp -Recurse -Force
if ($extra) { "Eslatma: exe'dan tashqari fayllar tashlab yuborildi: $($extra -join ', ')" }
"Tayyor: $Out\YtAuto.exe ({0:N0} MB)" -f ((Get-Item (Join-Path $Out 'YtAuto.exe')).Length / 1MB)
