param(
    [ValidateSet("x64", "arm64")] [string] $Arch = "x64",
    [ValidateSet("Release", "Debug")] [string] $Configuration = "Release",
    [switch] $SkipSmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-$Arch"
$output = Join-Path $repoRoot "dist\$runtime"

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

& dotnet publish (Join-Path $repoRoot "src\Compositor.App\Compositor.App.csproj") `
    --configuration $Configuration `
    --runtime $runtime `
    --self-contained true `
    -p:KernelsArch=$Arch `
    --output $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Tester packages do not need managed/native symbols; logs still retain exception stacks and build identity.
Get-ChildItem -LiteralPath $output -Filter "*.pdb" -File | Remove-Item -Force

$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$skipNativeLoad = $SkipSmokeTest -or $hostArch -ne $Arch
& (Join-Path $PSScriptRoot "test-publish.ps1") -PublishDirectory $output -ExpectedArch $Arch -SkipLoad:$skipNativeLoad

$betaGuide = Join-Path $repoRoot "docs\beta-testing.md"
if (Test-Path -LiteralPath $betaGuide) {
    Copy-Item -LiteralPath $betaGuide -Destination (Join-Path $output "BETA-TESTING.md") -Force
}

$zip = Join-Path $repoRoot "dist\Compositor-0.9.0-beta.1-$runtime.zip"
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -Force
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
Set-Content -LiteralPath "$zip.sha256" -Value "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))"
Write-Host "Published $output"
Write-Host "Packaged  $zip"
Write-Host "Checksum  $zip.sha256"
