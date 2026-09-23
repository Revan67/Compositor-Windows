param(
    [ValidateSet("Debug", "Release")] [string] $Configuration = "Release",
    [switch] $Publish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    & dotnet restore Compositor.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    & dotnet build Compositor.slnx --configuration $Configuration --no-restore -p:KernelsArch=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    & dotnet test Compositor.slnx --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

    if ($Publish) {
        & (Join-Path $PSScriptRoot "publish-windows.ps1") -Arch x64 -Configuration $Configuration
    }
}
finally {
    Pop-Location
}
