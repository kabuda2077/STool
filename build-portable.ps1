#requires -Version 7.0
# Build into a fresh directory on every invocation; never delete an existing Data folder or publish stage.
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$SkipTests,
    [switch]$ReadyToRun,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,48}$')]
    [string]$Variant = 'default'
)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Get-SolutionProject([string]$name) {
    $solution = [IO.File]::ReadAllText((Join-Path $repoRoot 'STool.sln'))
    $pattern = '=\s*"' + [regex]::Escape($name) + '",\s*"([^"]+\.csproj)"'
    $match = [regex]::Match($solution, $pattern)
    if (-not $match.Success) { throw "Project not found in solution: $name" }
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $match.Groups[1].Value))
    if (-not $path.StartsWith($repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project path must remain inside the repository: $name"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Project file does not exist: $path" }
    return $path
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = [IO.File]::ReadAllText((Join-Path $repoRoot 'Directory.Build.props'))
    $Version = $props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw 'A valid product version is required.'
}
$mainProject = Get-SolutionProject 'STool'
$testProject = Get-SolutionProject 'STool.Tests'
if ($ReadyToRun -and $Variant -eq 'default') { $Variant = 'r2r' }
$buildId = "$Version-$(Get-Date -Format yyyyMMdd-HHmmss)-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$publishDir = Join-Path $repoRoot "artifacts/publish/$Variant/$buildId"
$releaseDir = Join-Path $repoRoot "artifacts/releases/$Variant/$buildId"
$zipPath = Join-Path $releaseDir "STool_v${Version}_Portable.zip"

Write-Host "STool $Version ($Variant): framework-dependent win-x64" -ForegroundColor Cyan
if (-not $SkipTests) {
    dotnet test $testProject -c Release "-p:Version=$Version" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; packaging stopped.' }
}

$publishArgs = @(
    $mainProject, '-c', 'Release', '--runtime', 'win-x64', '--self-contained', 'false',
    "-p:Version=$Version", '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:PublishReadyToRun=$($ReadyToRun.IsPresent.ToString().ToLowerInvariant())", '-o', $publishDir
)
$timer = [Diagnostics.Stopwatch]::StartNew()
dotnet publish @publishArgs | Out-Host
$timer.Stop()
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no package was created.' }

$utf8 = [Text.UTF8Encoding]::new($false)
$utf8Bom = [Text.UTF8Encoding]::new($true)
$template = [IO.File]::ReadAllText((Join-Path $repoRoot 'docs/portable-readme.txt'), $utf8)
[IO.File]::WriteAllText((Join-Path $publishDir 'README.txt'), $template.Replace('{{VERSION}}', $Version), $utf8Bom)
[IO.Directory]::CreateDirectory($releaseDir) | Out-Null
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

$checks = & (Join-Path $repoRoot 'scripts/Verify-PortablePackage.ps1') `
    -PublishDirectory $publishDir -ArchivePath $zipPath -ExpectedVersion $Version
$checks | Add-Member -NotePropertyName PublishMilliseconds -NotePropertyValue ([math]::Round($timer.Elapsed.TotalMilliseconds, 1))
$checks | Add-Member -NotePropertyName ReadyToRun -NotePropertyValue $ReadyToRun.IsPresent
$checks | Add-Member -NotePropertyName PublishDirectory -NotePropertyValue $publishDir
[IO.File]::WriteAllText((Join-Path $releaseDir 'verification.json'), ($checks | ConvertTo-Json -Depth 6), $utf8)
[IO.File]::WriteAllText("$zipPath.sha256", "$($checks.ArchiveSha256)  $([IO.Path]::GetFileName($zipPath))`n", $utf8)
Write-Host "Verified package: $zipPath" -ForegroundColor Green
$checks
