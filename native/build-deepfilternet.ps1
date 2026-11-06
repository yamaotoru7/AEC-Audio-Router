#requires -Version 5
# Optional: builds the C API of DeepFilterNet (libDF, Rust; MIT / Apache-2.0) for the
# experimental "DFN3" noise suppression mode. Without the pinned Rust toolchain the build is
# skipped with a warning; the app then disables that mode and everything else still builds.
#   rustup toolchain install 1.91.0 --profile minimal
$ErrorActionPreference = 'Stop'
$commit = '7b14b895fe7fc42cab72b4a4c1e49c982bd6404b'
$toolchain = '1.91.0'
$source = Join-Path $PSScriptRoot 'build/deepfilternet'
$output = Join-Path $PSScriptRoot 'out/Release/deepfilter.dll'
$stamp = "$output.commit"
$licenses = Join-Path $PSScriptRoot 'out/Release/deepfilter-licenses.txt'

if ((Test-Path $output) -and (Test-Path $licenses) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $commit)) { exit 0 }

$rustup = Get-Command rustup -ErrorAction SilentlyContinue
$installed = if ($rustup) { & rustup toolchain list 2>$null } else { @() }
if (-not ($installed -match "^$([regex]::Escape($toolchain))-")) {
    Write-Warning "DeepFilterNet3 (DFN3) is skipped: Rust toolchain $toolchain is not installed (rustup toolchain install $toolchain --profile minimal)."
    exit 0
}

if (-not (Test-Path (Join-Path $source '.git'))) {
    New-Item -ItemType Directory -Force $source | Out-Null
    & git -C $source init -q
    & git -C $source remote add origin https://github.com/Rikorose/DeepFilterNet.git
}
& git -C $source fetch -q --depth 1 origin $commit
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet source fetch failed.' }
& git -C $source checkout -q --force FETCH_HEAD
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet source checkout failed.' }
# The upstream lock file is stale for current crates.io; this one pins what was tested.
Copy-Item (Join-Path $PSScriptRoot 'deepfilternet/Cargo.lock') (Join-Path $source 'Cargo.lock') -Force

& cargo "+$toolchain" build --release --locked --no-default-features --features capi `
    --manifest-path (Join-Path $source 'libDF/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet (libDF) build failed.' }

New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
Copy-Item (Join-Path $source 'target/release/df.dll') $output -Force
& (Join-Path $PSScriptRoot 'deepfilternet/collect-licenses.ps1') -Source $source -Toolchain $toolchain -Output $licenses
Set-Content $stamp $commit
