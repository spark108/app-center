param(
    [Parameter(Mandatory = $true)]
    [string]$Package,
    [string]$Origin = 'https://app.godish.ru',
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$Sha256,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $Package).ProviderPath
$originUri = [Uri]::new($Origin)
if (($originUri.Scheme -ne 'https' -and !($originUri.IsLoopback -and $originUri.Scheme -eq 'http')) -or
    $originUri.AbsolutePath -ne '/' -or $originUri.Query -or $originUri.Fragment -or $originUri.UserInfo) {
    throw 'Origin must be an HTTPS origin, or HTTP on loopback.'
}
if ($Sha256 -and (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $Sha256) {
    throw 'Package checksum does not match.'
}
$installPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Spark108/AppCenter'))
if ($env:SPARK108_APP_CENTER_HOME) {
    $installPath = [IO.Path]::GetFullPath($env:SPARK108_APP_CENTER_HOME).TrimEnd('\', '/')
}
$clientPath = Join-Path $installPath 'client'
if ((Test-Path -LiteralPath $clientPath) -or (Test-Path -LiteralPath (Join-Path $installPath 'client.previous'))) {
    throw 'Центр приложений уже установлен. Обновите его через каталог.'
}
$stagePath = Join-Path $installPath ('.install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
try {
    Expand-Archive -LiteralPath $packagePath -DestinationPath $stagePath
    foreach ($name in 'Spark108.Desktop.exe', 'Spark108.Updater.exe', 'spark-release.txt') {
        if (!(Test-Path -LiteralPath (Join-Path $stagePath $name) -PathType Leaf)) {
            throw 'Используйте ZIP Центра приложений, созданный скриптом Build-Release.ps1.'
        }
    }
    $options = @{ origin = $originUri.AbsoluteUri } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $installPath 'client.json'), $options)
    [IO.Directory]::Move($stagePath, $clientPath)
    $exePath = Join-Path $clientPath 'Spark108.Desktop.exe'
    if (!$env:SPARK108_APP_CENTER_HOME) {
        $programs = [Environment]::GetFolderPath('Programs')
        $linkPath = Join-Path $programs 'Центр приложений.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($linkPath)
        $link.TargetPath = $exePath
        $link.WorkingDirectory = $clientPath
        $link.Save()
    }
    if (!$NoLaunch) { Start-Process -FilePath $exePath -WorkingDirectory $clientPath }
    Get-Item -LiteralPath $exePath
}
finally {
    $resolved = (Resolve-Path -LiteralPath $stagePath -ErrorAction SilentlyContinue).ProviderPath
    if ($resolved -and [IO.Path]::GetDirectoryName($resolved) -eq $installPath) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
