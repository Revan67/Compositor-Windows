param(
    [ValidateSet("x64", "arm64")] [string] $Arch = "x64",
    [ValidateSet("Release", "Debug")] [string] $Configuration = "Release",
    [switch] $SkipSmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-$Arch"
$output = Join-Path $repoRoot "dist\$runtime"

& dotnet publish (Join-Path $repoRoot "src\Compositor.App\Compositor.App.csproj") `
    --configuration $Configuration `
    --runtime $runtime `
    --self-contained true `
    -p:KernelsArch=$Arch `
    --output $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$skipNativeLoad = $SkipSmokeTest -or $hostArch -ne $Arch
& (Join-Path $PSScriptRoot "test-publish.ps1") -PublishDirectory $output -ExpectedArch $Arch -SkipLoad:$skipNativeLoad

$alphaGuide = Join-Path $repoRoot "docs\alpha-testing.md"
if (Test-Path -LiteralPath $alphaGuide) {
    Copy-Item -LiteralPath $alphaGuide -Destination (Join-Path $output "ALPHA-TESTING.md") -Force
}

$zip = Join-Path $repoRoot "dist\Compositor-$runtime.zip"
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -Force
Write-Host "Published $output"
Write-Host "Packaged  $zip"
