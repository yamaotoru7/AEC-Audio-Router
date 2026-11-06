$ErrorActionPreference = 'Stop'
$buildDir = Join-Path $PSScriptRoot 'build'
& cmake -S $PSScriptRoot -B $buildDir -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'AEC3 CMake configure failed.' }
& cmake --build $buildDir --config Release --parallel 8
if ($LASTEXITCODE -ne 0) { throw 'AEC3 native build failed.' }
& (Join-Path $PSScriptRoot 'build-deepfilternet.ps1')
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet build failed.' }
