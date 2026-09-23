param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [ValidateSet("x64", "arm64")] [string] $ExpectedArch = "x64",
    [switch] $SkipLoad
)

$ErrorActionPreference = "Stop"
$resolved = (Resolve-Path -LiteralPath $PublishDirectory).Path
$app = Join-Path $resolved "Compositor.App.exe"
$kernels = Join-Path $resolved "compositor_kernels.dll"

if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    throw "Published application is missing: $app"
}

if (-not (Test-Path -LiteralPath $kernels -PathType Leaf)) {
    throw "Published native kernels are missing: $kernels"
}

function Get-PeMachine([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Not a PE image: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
        throw "Invalid PE header: $Path"
    }

    return [BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

$expectedMachine = if ($ExpectedArch -eq "arm64") { 0xAA64 } else { 0x8664 }
foreach ($image in @($app, $kernels)) {
    $actual = Get-PeMachine $image
    if ($actual -ne $expectedMachine) {
        throw "Wrong PE architecture for $image (expected 0x$($expectedMachine.ToString('X4')), found 0x$($actual.ToString('X4')))"
    }
}

if ($SkipLoad) {
    Write-Host "Publish structure and $ExpectedArch PE architecture passed: $resolved"
    exit 0
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class CompositorNativeSmoke
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr module);
}
"@

$handle = [CompositorNativeSmoke]::LoadLibrary($kernels)
if ($handle -eq [IntPtr]::Zero) {
    throw "Native kernels could not be loaded (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())): $kernels"
}

try {
    foreach ($export in @("levels_apply", "brush_alpha_bounds", "wand_mask")) {
        if ([CompositorNativeSmoke]::GetProcAddress($handle, $export) -eq [IntPtr]::Zero) {
            throw "Native kernel export is missing: $export"
        }
    }
}
finally {
    [void] [CompositorNativeSmoke]::FreeLibrary($handle)
}

Write-Host "Publish structure, $ExpectedArch PE architecture and native exports passed: $resolved"
