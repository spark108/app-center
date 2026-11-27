param(
    [ValidatePattern('^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$artifactsPath = [IO.Path]::GetFullPath((Join-Path $repoPath 'artifacts'))
$artifactPath = Join-Path $artifactsPath "Spark108-AppCenter-$Version-win-x64.zip"
$buildPath = Join-Path $artifactsPath ([Guid]::NewGuid().ToString('N'))
$clientPath = Join-Path $buildPath 'client'
$updaterPath = Join-Path $buildPath 'updater'
if (Test-Path -LiteralPath $artifactPath) { throw "Already exists: $artifactPath" }
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
try {
    $desktopProject = Join-Path $repoPath 'src/Spark108.Desktop/Spark108.Desktop.csproj'
    $updaterProject = Join-Path $repoPath 'src/Spark108.Updater/Spark108.Updater.csproj'
    dotnet publish $desktopProject -c Release -r win-x64 --self-contained true -o $clientPath "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    dotnet publish $updaterProject -c Release -r win-x64 --self-contained true -o $updaterPath "/p:Version=$Version" /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
    Copy-Item -LiteralPath (Join-Path $updaterPath 'Spark108.Updater.exe') -Destination $clientPath
    [IO.File]::WriteAllText((Join-Path $clientPath 'spark-release.txt'), $Version)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tempArchive = Join-Path $buildPath 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($clientPath, $tempArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $tempArchive -Destination $artifactPath
    $checksum = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$artifactPath.sha256", "$checksum  $([IO.Path]::GetFileName($artifactPath))")
    Get-Item -LiteralPath $artifactPath
}
finally {
    $resolved = (Resolve-Path -LiteralPath $buildPath -ErrorAction SilentlyContinue).ProviderPath
    if ($resolved -and [IO.Path]::GetDirectoryName($resolved) -eq $artifactsPath) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
