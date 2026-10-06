#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$archiveFile = (Resolve-Path -LiteralPath $ArchivePath).Path

function Assert-PackagePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or $path -match '[:\x00-\x1f]') {
        throw "Unsafe package path: $path"
    }
    $parts = $path.TrimEnd('/').Split('/')
    if ($parts | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -eq '' }) {
        throw "Unsafe package path: $path"
    }
    if ($path -match '(?i)(^|/)(Data|Tests?|TestResults|\.git|\.claude)(/|$)' -or
        $path -match '(?i)\.(cs|xaml|csproj|props|targets|pdb|db|log|key|trx)$' -or
        $path -match '(?i)(^|/)(config\.json|lan-devices\.json|lan-transfer-history\.json|testhost[^/]*|xunit[^/]*|Microsoft\.TestPlatform[^/]*|STool\.Tests[^/]*)$') {
        throw "Private data, test output or source code must not be packaged: $path"
    }
}

$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$expected = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
    Assert-PackagePath $relative
    $expected.Add($relative, $file)
}
foreach ($required in @('STool.exe', 'README.txt', 'e_sqlite3.dll')) {
    if (-not $expected.ContainsKey($required) -or $expected[$required].Length -eq 0) {
        throw "Missing required package file: $required"
    }
}

$exePath = $expected['STool.exe'].FullName
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($version.ProductVersion.Split('+')[0] -ne $ExpectedVersion.Split('+')[0]) {
    throw "Product version mismatch: $($version.ProductVersion), expected $ExpectedVersion"
}
$readme = [IO.File]::ReadAllText($expected['README.txt'].FullName)
if (-not $readme.Contains("STool v$ExpectedVersion") -or $readme.Contains('{{VERSION}}')) {
    throw 'README version was not rendered correctly.'
}

$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exePath))
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Executable is not a PE file.' }
    $reader.BaseStream.Position = 0x3C
    $peOffset = $reader.ReadUInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
        throw 'Executable is not Windows x64.'
    }
} finally { $reader.Dispose() }

# LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: inspect resources without executing the app.
if (-not ('STool.BuildChecks.NativeResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace STool.BuildChecks {
    public static class NativeResources {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
        public static byte[] ReadManifest(string path) {
            var module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
                if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var length = checked((int)SizeofResource(module, resource));
                var pointer = LockResource(LoadResource(module, resource));
                if (length == 0 || pointer == IntPtr.Zero) throw new InvalidOperationException("Missing manifest bytes.");
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            } finally { FreeLibrary(module); }
        }
    }
}
'@
}
$manifestBytes = [STool.BuildChecks.NativeResources]::ReadManifest($exePath)
$manifestStream = [IO.MemoryStream]::new($manifestBytes)
try {
    $manifest = [Xml.XmlDocument]::new()
    $manifest.XmlResolver = $null
    $manifest.Load($manifestStream)
} finally { $manifestStream.Dispose() }
$dpi = $manifest.SelectSingleNode("//*[local-name()='dpiAwareness']")
$longPaths = $manifest.SelectSingleNode("//*[local-name()='longPathAware']")
if ($null -eq $dpi -or $dpi.InnerText -notmatch 'PerMonitorV2') { throw 'PerMonitorV2 is missing from the executable manifest.' }
if ($null -eq $longPaths -or $longPaths.InnerText -ne 'true') { throw 'Long-path support is missing from the executable manifest.' }

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$zip = [IO.Compression.ZipFile]::OpenRead($archiveFile)
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        Assert-PackagePath $name
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
            throw "Symbolic links are not allowed: $name"
        }
        if ($name.EndsWith('/')) { continue }
        if (-not $seen.Add($name)) { throw "Duplicate ZIP entry: $name" }
        if (-not $expected.ContainsKey($name) -or $expected[$name].Length -ne $entry.Length) {
            throw "ZIP does not match the publish directory: $name"
        }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            $actualHash = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '')
        } finally {
            $hash.Dispose()
            $stream.Dispose()
        }
        $expectedHash = (Get-FileHash -LiteralPath $expected[$name].FullName -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) { throw "ZIP file content mismatch: $name" }
    }
} finally { $zip.Dispose() }
if ($seen.Count -ne $expected.Count) { throw 'ZIP is missing published files.' }

[pscustomobject]@{
    Verified = $true
    ProductVersion = $version.ProductVersion
    Architecture = 'x64'
    DpiAwareness = $dpi.InnerText
    FileCount = $expected.Count
    PublishBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    ExecutableBytes = $expected['STool.exe'].Length
    ArchiveBytes = (Get-Item -LiteralPath $archiveFile).Length
    ArchiveSha256 = (Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash
    ArchivePath = $archiveFile
}
