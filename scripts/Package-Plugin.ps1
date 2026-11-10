param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repoRoot "bin/$Configuration/net8.0-windows7.0"
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'plugin.json') -Raw | ConvertFrom-Json
$dll = Join-Path $output $manifest.AssemblyName
if (!(Test-Path -LiteralPath $dll)) { throw 'Build the Release configuration before packaging.' }
$builtVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
if ($builtVersion -ne $manifest.Version) { throw "DLL version $builtVersion differs from manifest $($manifest.Version). Rebuild first." }
$artifactDir = Join-Path $repoRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Zip($zipPath, $entries) {
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.Source,
                $entry.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}

# NINA and its native dependencies are supplied by the host. Include plugin dependencies.
$entries = @(Get-ChildItem -LiteralPath $output -Filter '*.dll' | Where-Object {
    $_.Name -eq $manifest.AssemblyName -or
    ($_.Name -notlike 'NINA.*' -and $_.Name -notlike 'Microsoft.Web.*' -and $_.Name -notlike 'Microsoft.Extensions.*')
} | ForEach-Object { @{ Source = $_.FullName; Name = $_.Name } })
foreach ($name in @('plugin.json', 'LICENSE.txt')) {
    $entries += @{ Source = Join-Path $repoRoot $name; Name = $name }
}
$releaseNotesName = "Release-$($manifest.Version).md"
$entries += @{ Source = Join-Path $repoRoot "Docs/$releaseNotesName"; Name = $releaseNotesName }
$binaryZip = Join-Path $artifactDir "GuidingAnalyzer-v$($manifest.Version)-preview.zip"
Write-Zip $binaryZip $entries

# Explicit source inventory also works from a downloaded source ZIP, without Git.
# Never distribute build output, audit snapshots, local guide logs, or private files.
$sourceFiles = @(Get-ChildItem -LiteralPath $repoRoot -File | Where-Object {
    $_.Extension -in @('.cs', '.xaml', '.csproj', '.sln') -or
    $_.Name -in @('README.md', 'LICENSE.txt', 'plugin.json', '.gitignore', '.gitattributes')
})
foreach ($folder in @('Services', 'Models', 'Properties', 'Tests', 'Docs', 'scripts')) {
    $folderPath = Join-Path $repoRoot $folder
    if (Test-Path -LiteralPath $folderPath) {
        $sourceFiles += @(Get-ChildItem -LiteralPath $folderPath -Recurse -File | Where-Object {
            $_.Extension -in @('.cs', '.xaml', '.csproj', '.md', '.ps1', '.json') -and
            [IO.Path]::GetRelativePath($folderPath, $_.FullName) -notmatch '(^|[\\/])(bin|obj|artifacts)([\\/]|$)'
        })
    }
}
$sourceEntries = @($sourceFiles | Sort-Object FullName -Unique | ForEach-Object {
    @{ Source = $_.FullName; Name = [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/') }
})
$sourceZip = Join-Path $artifactDir "GuidingAnalyzer-v$($manifest.Version)-sources.zip"
Write-Zip $sourceZip $sourceEntries
Get-FileHash -Algorithm SHA256 -LiteralPath $binaryZip, $sourceZip |
    Select-Object @{Name='File'; Expression={ [IO.Path]::GetFileName($_.Path) }}, Hash |
    Format-Table -AutoSize
