$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixtureBase = [IO.Path]::GetFullPath((Join-Path (Split-Path $repo -Parent) '.verify/setup-sound-check'))
$fixture = Join-Path $fixtureBase ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$appData = Join-Path $fixture 'appdata'
$localData = Join-Path $fixture 'localdata'
$ml = Join-Path $fixture 'ml'
$stub = Join-Path $fixture 'uv.cmd'
$script = Join-Path $repo 'installer/setup-sound.ps1'
$originalAppData = $env:APPDATA
$originalLocalData = $env:LOCALAPPDATA
try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localData
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -MlDirectory $ml -CheckOnly -ReportFile (Join-Path $fixture 'space-error.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Setup free space preflight failed.' }
    if (Test-Path -LiteralPath (Join-Path $localData 'RecipeSong-download-cache')) { throw 'Preflight created a cache.' }
    $command = "@if not exist `"%UV_CACHE_DIR%`" mkdir `"%UV_CACHE_DIR%`"`r`n@echo package>`"%UV_CACHE_DIR%\fixture.bin`"`r`n@echo temporary>`"%TEMP%\fixture.tmp`"`r`n@exit /b 0`r`n"
    [IO.File]::WriteAllText($stub, $command, [Text.Encoding]::ASCII)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -MlDirectory $ml -Uv $stub
    if ($LASTEXITCODE -ne 0) { throw 'Setup stub failed.' }
    $cache = Join-Path $localData 'RecipeSong-download-cache'
    if (-not (Test-Path -LiteralPath (Join-Path $cache 'uv/fixture.bin'))) { throw 'Reusable package cache was removed.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $cache 'temp') -Force).Count -ne 0) { throw 'Installer temporary files remained.' }
    [IO.File]::WriteAllText($stub, $command.Replace('exit /b 0', 'exit /b 7'), [Text.Encoding]::ASCII)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -MlDirectory $ml -Uv $stub
    if ($LASTEXITCODE -eq 0) { throw 'Setup failure was hidden.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $cache 'temp') -Force).Count -ne 0) { throw 'Failed setup left temporary files.' }
    Write-Host 'Setup preflight, reusable package cache and success/failure temporary cleanup passed.'
} finally {
    $env:APPDATA = $originalAppData
    $env:LOCALAPPDATA = $originalLocalData
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $fixtureBase) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
