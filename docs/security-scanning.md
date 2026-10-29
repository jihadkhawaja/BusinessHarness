# Security scanning

Passing local checks does not establish zero risk or predict a Veracode or
SonarQube quality gate. Scanner versions, enabled rules, analysis scope and server
policy affect results. Keep security hotspots and dependency findings visible;
do not add exclusions or suppressions just to obtain a passing result.

## Local checks

From the repository root, with the SDK selected by `global.json`:

```powershell
./scripts/Verify.ps1
./scripts/Smoke-Test.ps1
```

The build enables all built-in .NET security analyzer rules through
`AnalysisModeSecurity=All`, retains warnings as errors, and audits direct and
transitive NuGet dependencies at low severity. Verification includes tests,
formatting and packaging. These checks do not replace a dedicated secret scanner,
container vulnerability scan, or either hosted SAST engine.

For container changes, also run:

```powershell
docker build --pull --tag businessharness:test .
./scripts/Smoke-Test.ps1 -ContainerImage businessharness:test
```

The Docker frontend and .NET base images are pinned by digest. Review digest
updates deliberately. The image also installs operating-system packages during
its build; those packages require a vulnerability scan of the resulting image.

## Veracode

Use a framework-dependent Debug publish of the CLI, including its referenced
assemblies, portable symbols and dependency metadata. Exclude test binaries.
Veracode documents .NET 10 support and these requirements in its
[.NET packaging guide](https://docs.veracode.com/r/compilation_net).

The following creates a new output directory on each run, avoiding stale files:

```powershell
$scanRoot = Join-Path (Get-Location) "artifacts/security/$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $scanRoot 'veracode'
dotnet restore BusinessHarness.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore or audit failed.' }
dotnet publish src/BusinessHarness.Cli/BusinessHarness.Cli.csproj -c Debug --no-restore `
    --self-contained false -p:UseAppHost=false -p:DebugType=portable -p:Optimize=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Scan packaging failed.' }
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath (Join-Path $scanRoot 'veracode.zip')
```

Inspect the archive for the CLI, Core, Application, Infrastructure and MagiCore
DLLs and PDBs, plus `BusinessHarness.Cli.deps.json` and runtime configuration.
Upload it to the intended Veracode application using your approved integration.
Confirm prescan module/dependency completeness, execute the scan, and review the
actual policy result. Building this ZIP alone is not a Veracode scan.

## SonarQube

Use an approved, pinned SonarScanner for .NET version compatible with your server
and SDK. Follow the official
[scanner usage guide](https://docs.sonarsource.com/sonarqube-server/analyzing-source-code/scanners/dotnet/using).
Configure the server URL, project identity and a narrowly scoped analysis token
through your protected CI integration. Keep tokens out of source and logs.

The required order is scanner `begin`, a fresh solution build and tests, then
scanner `end`. An ordinary build performed before `begin` cannot substitute for
the instrumented build. Use `--no-incremental` for that build and the repository's
Microsoft.Testing.Platform test command:

```powershell
# Run the configured scanner begin step first.
dotnet restore BusinessHarness.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore or audit failed.' }
dotnet build BusinessHarness.slnx -c Release --no-restore --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Analysis build failed.' }
dotnet test --solution BusinessHarness.slnx -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
# Run the configured scanner end step, then wait for the server's quality gate.
```

Keep `Verify.ps1` as an independent required check: the scanner's begin step can
change analyzer configuration and turn off warnings as errors. Account explicitly
for the pinned MAGICore source dependency when interpreting analysis scope.
Review security hotspots and confirm that the server has finished processing;
a successful upload is not a passing quality gate.
