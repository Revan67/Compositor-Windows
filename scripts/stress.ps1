param(
    [ValidateSet("quick", "full")] [string] $Profile = "quick",
    [string] $Output
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$arguments = @("run", "--project", (Join-Path $repoRoot "tools\Compositor.Stress\Compositor.Stress.csproj"), "--configuration", "Release", "--")
if ($Profile -eq "full") { $arguments += "--full" }
if ($Output) { $arguments += @("--output", $Output) }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Stress run failed with exit code $LASTEXITCODE" }
