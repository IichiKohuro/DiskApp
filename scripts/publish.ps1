<#
.SYNOPSIS
    Собирает DiskApp в один самодостаточный exe для запуска без установленного .NET.

.DESCRIPTION
    Решение 10 плана: self-contained, single-file, ReadyToRun. Результат — releases\DiskApp.exe
    (папка в .gitignore). Нативные библиотеки (Skia, HarfBuzz, ANGLE) упакованы внутрь и распаковываются
    при первом запуске. Установщик и автообновление (Velopack) появятся в этапе 11; этот скрипт — для
    ручного запуска и ежедневного использования до него.

.PARAMETER Runtime
    Идентификатор платформы: win-x64 (по умолчанию) или win-arm64.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'releases'

dotnet publish (Join-Path $root 'src\DiskApp.Desktop') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=embedded `
    -o $output `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

# Рядом с exe не нужны XML-документация сборок и отладочные символы нативных библиотек (100 МБ).
Get-ChildItem $output -Include *.xml, *.pdb -Recurse | Remove-Item -Force

$exe = Join-Path $output 'DiskApp.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Готово: $exe ($size МБ, $Runtime)"
