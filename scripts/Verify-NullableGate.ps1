#requires -Version 7.0
# Prove the repository's nullable gate without editing production sources or weakening other warnings.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path $PSScriptRoot -Parent
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $repoRoot "artifacts/verification/gate-$id"
[IO.Directory]::CreateDirectory($root) | Out-Null
$project = Join-Path $root "GateProbe$id.csproj"
$source = Join-Path $root 'Probe.cs'
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>
'@, $utf8)

[IO.File]::WriteAllText($source, 'internal static class Probe { internal static string Value() => null; }', $utf8)
$nullableOutput = & dotnet build $project -c Release --nologo 2>&1
$nullableExit = $LASTEXITCODE
$nullableText = $nullableOutput -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $root 'nullable-build.log'), $nullableText, $utf8)
if ($nullableExit -eq 0 -or $nullableText -notmatch 'CS8603') {
    throw "Nullable diagnostics did not fail the build as expected. See $root"
}

[IO.File]::WriteAllText($source, 'internal static class Probe { internal static int Value() { int unused; return 1; } }', $utf8)
$ordinaryOutput = & dotnet build $project -c Release --no-restore --nologo 2>&1
$ordinaryExit = $LASTEXITCODE
$ordinaryText = $ordinaryOutput -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $root 'ordinary-build.log'), $ordinaryText, $utf8)
if ($ordinaryExit -ne 0 -or $ordinaryText -notmatch 'CS0168') {
    throw "Ordinary warnings were hidden or promoted unexpectedly. See $root"
}

$result = [pscustomobject]@{
    NullableGateVerified = $true
    NullableExitCode = $nullableExit
    NullableDiagnostic = 'CS8603'
    OrdinaryExitCode = $ordinaryExit
    OrdinaryDiagnostic = 'CS0168'
    LogsDirectory = $root
}
[IO.File]::WriteAllText((Join-Path $root 'result.json'), ($result | ConvertTo-Json), $utf8)
$result
