#requires -Version 7.0
param([string]$Version, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$arguments = @{}
if (-not [string]::IsNullOrWhiteSpace($Version)) { $arguments.Version = $Version }

# The first invocation runs tests unless the caller already verified them.
$normal = & (Join-Path $repoRoot 'build-portable.ps1') @arguments -Variant plain -SkipTests:$SkipTests
$r2r = & (Join-Path $repoRoot 'build-portable.ps1') @arguments -Variant r2r -ReadyToRun -SkipTests
if (-not $normal.Verified -or -not $r2r.Verified) { throw 'Both packages must pass verification.' }

$report = [pscustomobject]@{
    GeneratedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Scope = 'Release / win-x64 / framework-dependent / single-file'
    Note = 'Publish durations include restore and are affected by caches. These measurements do not measure GUI startup, frame latency, or native memory.'
    Variants = @($normal, $r2r)
}
$outputDirectory = Join-Path $repoRoot 'artifacts/verification'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$path = Join-Path $outputDirectory ("publish-comparison-{0}.json" -f [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$report.Variants | Select-Object ReadyToRun, PublishMilliseconds, PublishBytes, ExecutableBytes, ArchiveBytes | Format-Table | Out-Host
Write-Host "Comparison report: $path"
$report
