# Builds src/Compositor.Kernels/native/*.c into compositor_kernels.dll with the clang that
# ships in Visual Studio. No CMake, no vcxproj. Invoked by Compositor.Kernels.csproj before
# build; can also be run by hand.
#
#   scripts/build-kernels.ps1 [-Arch x64|arm64] [-Configuration Release|Debug]
param(
    [ValidateSet("x64", "arm64")] [string] $Arch = "x64",
    [ValidateSet("Release", "Debug")] [string] $Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root "src\Compositor.Kernels\native"
$outDir = Join-Path $root "src\Compositor.Kernels\bin\native\$Arch"
$dll = Join-Path $outDir "compositor_kernels.dll"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; install Visual Studio 2022 with the C++ Clang tools." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Llvm.Clang -property installationPath
if (-not $vs) { throw "No Visual Studio with the 'C++ Clang tools for Windows' component was found." }
$clang = Join-Path $vs "VC\Tools\Llvm\x64\bin\clang.exe"
if (-not (Test-Path $clang)) { throw "clang.exe not found under $vs" }

$sources = Get-ChildItem (Join-Path $native "*.c") | ForEach-Object { $_.FullName }
$def = Join-Path $native "compositor_kernels.def"

# Skip when the DLL is newer than every input.
if (Test-Path $dll) {
    $dllTime = (Get-Item $dll).LastWriteTimeUtc
    $newer = @($sources + @($def, $PSCommandPath)) | Where-Object { (Get-Item $_).LastWriteTimeUtc -gt $dllTime }
    if ($newer.Count -eq 0) { Write-Host "compositor_kernels.dll ($Arch) is up to date."; exit 0 }
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$target = if ($Arch -eq "arm64") { "aarch64-pc-windows-msvc" } else { "x86_64-pc-windows-msvc" }
$opt = if ($Configuration -eq "Debug") { @("-O0", "-g") } else { @("-O2") }

# The pixel kernels are hot loops and must stay optimized even in Debug app builds; the
# Configuration switch here is for debugging the C itself.
$args = @("--target=$target", "-shared", "-std=c11", "-D_USE_MATH_DEFINES", "-D_CRT_SECURE_NO_WARNINGS", "-Wall", "-Wno-unused-function", "-fuse-ld=lld") + $opt +
        @("-o", $dll, "-Wl,/DEF:$def") + $sources
Write-Host "clang $($args -join ' ')"
& $clang @args
if ($LASTEXITCODE -ne 0) { throw "clang failed with exit code $LASTEXITCODE" }
Write-Host "Built $dll"
