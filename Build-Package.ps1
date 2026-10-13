$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$project = Join-Path $PSScriptRoot 'ContactsLoading\ContactsLoading.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Releaseビルドに失敗しました。' }

$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$dll = Join-Path $PSScriptRoot 'ContactsLoading\bin\Release\net10.0\ContactsLoading.dll'
$outputDir = Join-Path $PSScriptRoot 'dist'
[IO.Directory]::CreateDirectory($outputDir) | Out-Null
$output = Join-Path $outputDir "ContactsLoading-$version.zip"
$temporary = "$output.tmp"
$dependencies = @(
    'ResoniteModding-BepisLoader-1.7.0'
    'ResoniteModding-BepInExResoniteShim-0.9.3'
    'ResoniteModding-BepisResoniteWrapper-1.0.2'
    'ResoniteModding-BepisModSettings-1.6.6'
)
$manifest = [ordered]@{
    name = 'ContactsLoading'
    author = 'markn2000'
    version_number = $version
    description = 'Speeds up Contacts loading and reduces freezing when opening the tab.'
    website_url = 'https://github.com/MarkN2000/ContactsLoading'
    dependencies = $dependencies
} | ConvertTo-Json
$documents = @('README.md', 'LICENSE', 'icon.png')

# 検査を通るまでは既存の完成済みZIPを置き換えない。
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Create)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dll, 'plugins/ContactsLoading.dll') | Out-Null
        foreach ($file in $documents) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot $file), $file) | Out-Null
        }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('manifest.json').Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($manifest) } finally { $writer.Dispose() }
    } finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }

    $zip = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        $expected = $documents + @('manifest.json', 'plugins/ContactsLoading.dll')
        if (Compare-Object $expected @($zip.Entries.FullName)) { throw 'ZIPのファイル構成が一致しません。' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.name -cne 'ContactsLoading' -or $metadata.author -cne 'markn2000' -or
            $metadata.version_number -ne $version -or $metadata.description.Length -gt 250 -or
            $metadata.website_url -cne 'https://github.com/MarkN2000/ContactsLoading' -or
            (@($metadata.dependencies) -join ',') -cne ($dependencies -join ',')) {
            throw 'manifestの内容が一致しません。'
        }
        $entryStream = $zip.GetEntry('plugins/ContactsLoading.dll').Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '') }
        finally { $entryStream.Dispose(); $sha.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) { throw 'ZIP内DLLとビルド成果物が一致しません。' }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $output -Force
    Write-Output "ZIPを作成・検査しました: $output"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
