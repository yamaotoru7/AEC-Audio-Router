<#
.SYNOPSIS
  AEC Audio Router のインストーラを作る → .work/installer/AecAudioRouter-<版>-setup.exe と .sha256
.DESCRIPTION
  1. self-contained で publish する（win-x64、Release → .work/publish。AEC3Native.dll もビルドして入る）
  2. Inno Setup 6 の ISCC で installer/AecAudioRouter.iss をコンパイルする（版は Directory.Build.props の <Version>）
  3. 更新（Updater.cs）が照合する .sha256 を書く。リリースにはインストーラと一緒に添付する
.EXAMPLE
  pwsh tools/installer/build-installer.ps1
#>
param([string]$Iscc)
$ErrorActionPreference = 'Stop'

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$publish = Join-Path $root '.work/publish'
$output = Join-Path $root '.work/installer'

if (-not $Iscc) {
  $Iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Pass -Iscc <path>.' }

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'src/AecAudioRouter') -c Release -r win-x64 --self-contained -o $publish -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
foreach ($f in 'AecAudioRouter.exe', 'Aec3Native.dll', 'THIRD-PARTY-NOTICES.txt') {
  if (-not (Test-Path (Join-Path $publish $f))) { throw "$f is missing in $publish" }
}

& $Iscc /Q "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$output" (Join-Path $root 'installer/AecAudioRouter.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Join-Path $output "AecAudioRouter-$version-setup.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  $(Split-Path $setup -Leaf)" -NoNewline
Write-Host "installer: $setup"
Write-Host "sha256:    $hash"
