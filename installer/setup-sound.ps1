param(
    [Parameter(Mandatory=$true)][string]$MlDirectory,
    [string]$Uv,
    [string]$Language = 'ko',
    [switch]$CheckOnly,
    [string]$ReportFile,
    [switch]$PauseOnError
)
$ErrorActionPreference = 'Stop'
$english = $Language -in @('en', 'english')
$lease = $null
$scratch = $null
try {
    if (-not $Uv) { $Uv = Join-Path $PSScriptRoot 'uv.exe' }
    $settingsPath = Join-Path $env:APPDATA 'RecipeSong/settings.json'
    $settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { $null }
    $root = if ($settings -and $settings.DownloadCacheDirectory) { [IO.Path]::GetFullPath($settings.DownloadCacheDirectory) }
        elseif (Test-Path -LiteralPath (Join-Path $MlDirectory '../.git')) { [IO.Path]::GetFullPath((Join-Path $MlDirectory '.download-cache')) }
        else { Join-Path $env:LOCALAPPDATA 'RecipeSong-download-cache' }
    $root = [IO.Path]::GetFullPath($root).TrimEnd('\')
    if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'A drive root cannot be used as the cache.' }
    $fresh = -not ((Test-Path -LiteralPath (Join-Path $MlDirectory '.venv/pyvenv.cfg')) -and
        (Test-Path -LiteralPath (Join-Path $MlDirectory '.venv/Lib/site-packages/torch/__init__.py')) -and
        (Test-Path -LiteralPath (Join-Path $MlDirectory '.venv/Lib/site-packages/transformers/__init__.py')))
    $required = @{}
    foreach ($part in @(@($MlDirectory, $(if ($fresh) { 6GB } else { 256MB })), @($root, $(if ($fresh) { 8GB } else { 1GB })))) {
        $drive = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($part[0]))
        $required[$drive] = [long]$required[$drive] + [long]$part[1]
    }
    foreach ($drive in $required.Keys) {
        if ([IO.DriveInfo]::new($drive).AvailableFreeSpace -lt $required[$drive]) {
            if ($english) { throw ('Sound setup needs about {0:N1} GiB free on {1}.' -f ($required[$drive]/1GB), $drive) }
            throw ('소리 분석 환경 설치에 {0} 드라이브 여유 공간 약 {1:N1}GiB가 필요합니다.' -f $drive, ($required[$drive]/1GB))
        }
    }
    if ($CheckOnly) { exit 0 }
    # 공용 캐시는 비우지 않는다. 앱 전용 빈 폴더에만 소유 표식을 만든다.
    for ($folder = [IO.DirectoryInfo]::new($root); $null -ne $folder; $folder = $folder.Parent) {
        if ($folder.Exists -and ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked cache directories are not supported.' }
    }
    $marker = Join-Path $root '.recipesong-download-cache'
    $owner = 'RecipeSong downloadable cache v1'
    if (Test-Path -LiteralPath $marker) {
        if ([IO.File]::ReadAllText($marker) -ne $owner) { throw 'Not a RecipeSong download cache.' }
    } else {
        if ((Test-Path -LiteralPath $root) -and @(Get-ChildItem -LiteralPath $root -Force).Count -gt 0) { throw 'Choose an empty cache directory.' }
        [IO.Directory]::CreateDirectory($root) | Out-Null
        [IO.File]::WriteAllText($marker, $owner)
    }
    $lock = Join-Path $root '.in-use'
    if (-not (Test-Path -LiteralPath $lock)) { [IO.File]::WriteAllText($lock, '') }
    $lease = [IO.FileStream]::new($lock, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $env:UV_CACHE_DIR = Join-Path $root 'uv'
    $env:HF_HUB_CACHE = Join-Path $root 'models'
    $env:HF_HOME = Join-Path $root 'hf'
    # 설치 한 번의 임시 작업만 지운다. 패키지·모델 캐시는 다음 설치·개발 때 재사용한다.
    $tempRoot = Join-Path $root 'temp'
    for ($folder = [IO.DirectoryInfo]::new($tempRoot); $null -ne $folder; $folder = $folder.Parent) {
        if ($folder.Exists -and ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked temporary directories are not supported.' }
    }
    $scratch = Join-Path $tempRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $env:TEMP = $scratch
    $env:TMP = $scratch
    & $Uv sync --no-dev --directory $MlDirectory
    if ($LASTEXITCODE -ne 0) { throw "uv sync failed ($LASTEXITCODE)." }
} catch {
    $message = $_.Exception.Message
    if ($ReportFile) { [IO.File]::WriteAllText($ReportFile, $message, [Text.UTF8Encoding]::new($false)) }
    Write-Host $message
    if ($PauseOnError) { Read-Host $(if ($english) { 'Press Enter to close' } else { 'Enter를 누르면 닫습니다' }) | Out-Null }
    exit 1
} finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch)) {
        $resolved = [IO.Path]::GetFullPath($scratch)
        if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath((Join-Path $root 'temp')) -and [IO.Path]::GetFileName($resolved) -match '^[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($lease) { $lease.Dispose() }
}
