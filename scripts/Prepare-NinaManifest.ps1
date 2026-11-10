param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$InstallerUrl,
    [Parameter(Mandatory)][string]$PreviousManifest,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    $pluginEntry = $archive.GetEntry('plugin.json')
    if (!$pluginEntry) { throw 'Archive must contain plugin.json at its root.' }
    $reader = [IO.StreamReader]::new($pluginEntry.Open())
    try { $plugin = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $dllEntry = $archive.GetEntry($plugin.AssemblyName)
    if (!$dllEntry) { throw 'Main plugin DLL missing from archive.' }
    $memory = [IO.MemoryStream]::new()
    $dllStream = $dllEntry.Open()
    try { $dllStream.CopyTo($memory) } finally { $dllStream.Dispose() }
    $memory.Position = 0
    # Inspect PE metadata without loading or executing the assembly.
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($memory)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $metadata.GetAssemblyDefinition()
        $values = @{}
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
            $ctor = $metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($ctor.Parent.Kind -ne 'TypeReference') { continue }
            $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$ctor.Parent)
            $name = $metadata.GetString($type.Name)
            $blob = $metadata.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly attribute prolog.' }
            if ($name -eq 'AssemblyMetadataAttribute') {
                $key = $blob.ReadSerializedString()
                $value = $blob.ReadSerializedString()
                $values[$key] = $value
            } elseif ($name -in @('GuidAttribute', 'AssemblyTitleAttribute', 'AssemblyFileVersionAttribute',
                'AssemblyCompanyAttribute', 'AssemblyDescriptionAttribute')) {
                $values[$name] = $blob.ReadSerializedString()
            }
        }
        $assemblyVersion = $assembly.Version.ToString()
    } finally { $pe.Dispose(); $memory.Dispose() }
} finally { $archive.Dispose() }

$previous = Get-Content -LiteralPath $PreviousManifest -Raw | ConvertFrom-Json
if ([guid]$values['GuidAttribute'] -ne [guid]$previous.Identifier) {
    throw 'Permanent plugin identifier differs from the catalog. Do not publish or silently change it.'
}
if ($values['AssemblyTitleAttribute'] -ne $previous.Name -or $plugin.Name -ne $previous.Name) {
    throw 'Permanent plugin name differs from the catalog.'
}
if ($plugin.Version -ne $assemblyVersion -or $values['AssemblyFileVersionAttribute'] -ne $assemblyVersion) {
    throw 'Project manifest and DLL versions are inconsistent.'
}
$url = [uri]$InstallerUrl
if (!$url.IsAbsoluteUri -or $url.Scheme -ne 'https') { throw 'Installer URL must be an absolute HTTPS URL.' }
if ([uri]::UnescapeDataString([IO.Path]::GetFileName($url.AbsolutePath)) -ne [IO.Path]::GetFileName($ArchivePath)) {
    throw 'Installer URL filename does not match the archive being hashed.'
}
function Convert-Version([string]$value) {
    $v = [version]$value
    if ($v.Build -lt 0 -or $v.Revision -lt 0) { throw 'A four-part version is required.' }
    return [ordered]@{ Major = "$($v.Major)"; Minor = "$($v.Minor)"; Patch = "$($v.Build)"; Build = "$($v.Revision)" }
}
$version = Convert-Version $assemblyVersion
$previousVersion = [version](('{0}.{1}.{2}.{3}' -f $previous.Version.Major, $previous.Version.Minor,
    $previous.Version.Patch, $previous.Version.Build))
if ([version]$assemblyVersion -le $previousVersion) { throw 'The release version must be newer than the catalog version.' }
$manifest = [ordered]@{
    Name = $values['AssemblyTitleAttribute']; Identifier = $values['GuidAttribute']; Version = $version
    Author = $values['AssemblyCompanyAttribute']; Homepage = $values['Homepage']; Repository = $values['Repository']
    License = $values['License']; LicenseURL = $values['LicenseURL']; ChangelogURL = $values['ChangelogURL']
    Tags = @($values['Tags'].Split(','))
    MinimumApplicationVersion = (Convert-Version $values['MinimumApplicationVersion'])
    Descriptions = [ordered]@{
        ShortDescription = $values['AssemblyDescriptionAttribute']; LongDescription = $values['LongDescription']
        FeaturedImageURL = $values['FeaturedImageURL']; ScreenshotURL = $values['ScreenshotURL']; AltScreenshotURL = $values['AltScreenshotURL']
    }
    Installer = [ordered]@{
        URL = $InstallerUrl; Type = 'ARCHIVE'
        Checksum = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash; ChecksumType = 'SHA256'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination = Join-Path $OutputDirectory "NINA.Plugin.GuidingAnalyzer.$assemblyVersion.manifest.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output $destination
