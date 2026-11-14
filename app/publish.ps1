# YtAuto distributivi: self-contained (foydalanuvchidan .NET talab qilinmaydi), win-x64.
# Natija: dist\YtAuto\ (YtAuto.exe - boshqaruv oynasi, YtAuto.Worker.exe - oynasiz worker) va dist\YtAuto-win-x64.zip
param(
  [string]$Dotnet = $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'D:\dotnet-sdk\dotnet.exe' }),
  [string]$Out = "$PSScriptRoot\dist\YtAuto",
  # CI: har build uchun versiya (masalan 1.0.42). Bo'sh bo'lsa Directory.Build.props dagi versiya.
  [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
if ($Dotnet -like 'D:\dotnet-sdk*') { $env:DOTNET_ROOT = 'D:\dotnet-sdk'; $env:NUGET_PACKAGES = 'D:\dotnet-sdk\nuget-packages' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-p:SatelliteResourceLanguages=en', '-o', $Out)
if ($Version) { $common += "-p:Version=$Version" }
& $Dotnet publish "$PSScriptRoot\src\YtAuto.Worker\YtAuto.Worker.csproj" @common
if ($LASTEXITCODE -ne 0) { throw "Worker publish xato" }
& $Dotnet publish "$PSScriptRoot\src\YtAuto.Desktop\YtAuto.Desktop.csproj" @common
if ($LASTEXITCODE -ne 0) { throw "Desktop publish xato" }
Copy-Item "$PSScriptRoot\README.md" $Out -Force
$zip = "$PSScriptRoot\dist\YtAuto-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$Out\*" -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-ChildItem $Out -Recurse | Measure-Object Length -Sum).Sum / 1MB
"Tayyor: $Out ({0:N0} MB), zip: $zip ({1:N0} MB)" -f $size, ((Get-Item $zip).Length / 1MB)
