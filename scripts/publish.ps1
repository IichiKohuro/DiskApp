<#
.SYNOPSIS
    Собирает DiskApp для запуска без установленного .NET: в releases\ копятся все сборки, в release\ — только последняя.

.DESCRIPTION
    Решение 10 плана: self-contained, ReadyToRun. Имя результата:
        diskapp_v{версия}_{стадия}[_singlefile].exe
    Версия берётся из VersionPrefix в Directory.Build.props; стадия — alpha, beta, rc или release и для
    всех стадий, кроме release, попадает в версию сборки как суффикс SemVer (0.1.0-alpha), поэтому
    в настройках приложения показывается та же стадия.

    Single-file (по умолчанию): один exe с упакованными нативными библиотеками (Skia, HarfBuzz, ANGLE),
    они распаковываются при первом запуске; в имени есть _singlefile. Обычная сборка (-SingleFile:$false) —
    папка releases\diskapp_v{версия}_{стадия}\ с exe и библиотеками рядом, без суффикса в имени.

    Та же сборка с тем же именем копируется в release\, прежнее содержимое release\ удаляется: там всегда одна,
    последняя сборка. Папки releases\ и release\ в .gitignore. Установщик и автообновление (Velopack) появятся в этапе 11.

.PARAMETER Stage
    Стадия зрелости: alpha (ранняя, не всё готово), beta (функции есть, идёт проверка), rc (кандидат
    в релиз) или release (стабильная). По умолчанию alpha.

.PARAMETER Runtime
    Идентификатор платформы: win-x64 (по умолчанию) или win-arm64.

.PARAMETER SingleFile
    Собирать в один exe. По умолчанию $true.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Stage beta
    .\scripts\publish.ps1 -Stage release -SingleFile:$false
#>
param(
    [ValidateSet('alpha', 'beta', 'rc', 'release')]
    [string]$Stage = 'alpha',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [bool]$SingleFile = $true
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releases = Join-Path $root 'releases'
$latest = Join-Path $root 'release'
$project = Join-Path $root 'src\DiskApp.Desktop'

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') { throw 'VersionPrefix не найден в Directory.Build.props' }
$version = $Matches[1].Trim()

$baseName = "diskapp_v${version}_${Stage}"
if ($SingleFile) { $baseName += '_singlefile' }
$exeName = "$baseName.exe"

$publishDirectory = if ($SingleFile) { Join-Path $releases ".publish-$Runtime" } else { Join-Path $releases $baseName }
if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())",
    "-p:IncludeNativeLibrariesForSelfExtract=$($SingleFile.ToString().ToLowerInvariant())",
    '-p:PublishReadyToRun=true',
    '-p:DebugType=embedded',
    '-o', $publishDirectory,
    '--nologo'
)
if ($Stage -ne 'release') { $arguments += "-p:VersionSuffix=$Stage" }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

# Рядом с exe не нужны XML-документация сборок и отладочные символы нативных библиотек (100 МБ).
Get-ChildItem $publishDirectory -Include *.xml, *.pdb -Recurse | Remove-Item -Force

if ($SingleFile) {
    $target = Join-Path $releases $exeName
    Move-Item (Join-Path $publishDirectory 'DiskApp.exe') $target -Force
    Remove-Item $publishDirectory -Recurse -Force
}
else {
    # Переименование apphost безопасно: имя DiskApp.dll зашито в exe при сборке.
    Rename-Item (Join-Path $publishDirectory 'DiskApp.exe') $exeName
    $target = Join-Path $publishDirectory $exeName
}

# release\ — только последняя сборка. Запущенный оттуда DiskApp держит exe, и очистка не пройдёт.
try {
    if (Test-Path $latest) { Get-ChildItem $latest -Force | Remove-Item -Recurse -Force }
}
catch {
    throw "Не получилось очистить release\: закройте DiskApp, запущенный оттуда, и повторите. $($_.Exception.Message)"
}
New-Item -ItemType Directory $latest -Force | Out-Null
Copy-Item $(if ($SingleFile) { $target } else { $publishDirectory }) $latest -Recurse

$size = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Готово: $target ($size МБ, $Runtime, версия $version, стадия $Stage$(if ($SingleFile) { ', single-file' }))"
Write-Host "Последняя сборка: $(Join-Path $latest $(if ($SingleFile) { $exeName } else { $baseName }))"
