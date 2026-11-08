[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet restore BusinessHarness.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore or NuGet security audit failed.' }
    & dotnet build BusinessHarness.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet test --solution BusinessHarness.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & dotnet format BusinessHarness.slnx --verify-no-changes --no-restore --exclude external/MAGICore external/AgentConnector external/AgentSpace
    if ($LASTEXITCODE -ne 0) { throw 'Formatting or analyzer check failed.' }
    $auditText = & dotnet package list --project BusinessHarness.slnx --vulnerable --include-transitive --format json --output-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Vulnerability query failed.' }
    $audit = ($auditText -join "`n") | ConvertFrom-Json
    $packages = @($audit.projects | ForEach-Object { $_.frameworks } | ForEach-Object {
        $_.topLevelPackages
        $_.transitivePackages
    } | Where-Object { $_ })
    if ($packages.Count -gt 0) { throw 'Vulnerable packages found. Run dotnet package list --vulnerable --include-transitive.' }
    & dotnet pack src/BusinessHarness.Cli/BusinessHarness.Cli.csproj -c Release --no-build --output artifacts/packages
    if ($LASTEXITCODE -ne 0) { throw 'Tool packaging failed.' }
    Write-Output 'Build, tests, formatting, vulnerability audit, and packaging passed.'
}
finally {
    Pop-Location
}
