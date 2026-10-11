<#
.SYNOPSIS
    Замеры бюджетов производительности (PLAN.md, раздел 8; этап 10.1) и сравнение с ними.

.DESCRIPTION
    Запускает портативную копию DiskApp в отдельной папке (рабочие данные пользователя не трогаются) и снимает:
      - старт до трея: первый запуск копии (ближе всего к холодному) и тёплые запуски, по журналу приложения;
      - показ окна быстрого поиска: от вызова до первого кадра, по журналу; окно вызывается аргументом --search;
      - первичную индексацию сгенерированного дерева файлов внутри приложения;
      - память и CPU в простое: после показа и скрытия главного окна, выборка раз в 5 с;
      - сканирование 100 тыс. файлов, поиск p95 на 1 млн записей (сервис и API) — проверками бюджета из
        tests/DiskApp.Benchmarks (scan-budget, search-budget, api-budget).
    Итог — таблица «бюджет / замер» в консоли и в artifacts\perf\perf-<дата>.md вместе с конфигурацией машины.

    По умолчанию текущий код публикуется в рабочую папку так же, как для пользователя: self-contained, ReadyToRun.

.PARAMETER Exe
    Какой exe замерять (например, releases\diskapp_v0.5.0_alpha_singlefile.exe); по умолчанию текущий код
    публикуется в рабочую папку.

.PARAMETER Quick
    Короткий прогон для проверки скрипта: 3 000 файлов, 3 старта, 5 показов, 60 с простоя, бенчмарки на 20 тыс.
    файлов и 100 тыс. записей.

.PARAMETER Files
    Файлов в дереве для индексации внутри приложения.

.PARAMETER IdleSeconds
    Длительность простоя для памяти и CPU (бюджет — после 10 минут).

.PARAMETER SkipBenchmarks
    Не запускать scan-budget, search-budget и api-budget.

.PARAMETER SearchDatabase
    База на 1 млн записей для search-budget и api-budget; по умолчанию %TEMP%\DiskApp.Benchmarks\search-1m.db
    (создаётся при первом запуске, около 10 минут).

.PARAMETER Keep
    Не удалять рабочую папку замера.

.EXAMPLE
    .\scripts\measure.ps1 -Quick
    .\scripts\measure.ps1
    .\scripts\measure.ps1 -Exe .\releases\diskapp_v0.5.0_alpha_singlefile.exe -SkipBenchmarks
#>
param(
    [string]$Exe,
    [switch]$Quick,
    [int]$Files = 20000,
    [int]$StartRuns = 5,
    [int]$PopupShows = 10,
    [int]$IdleSeconds = 600,
    [switch]$SkipBenchmarks,
    [int]$ScanFiles = 100000,
    [int]$SearchRecords = 1000000,
    [string]$SearchDatabase,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$culture = [Globalization.CultureInfo]::InvariantCulture

if ($Quick) {
    $Files = 3000; $StartRuns = 3; $PopupShows = 5; $IdleSeconds = 60; $ScanFiles = 20000; $SearchRecords = 100000
}

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') { throw 'VersionPrefix не найден в Directory.Build.props' }
$version = $Matches[1].Trim()

# --- Что замеряем ---------------------------------------------------------------------------------------------
$workspace = Join-Path ([IO.Path]::GetTempPath()) ("DiskApp.Measure\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$appDirectory = Join-Path $workspace 'app'
New-Item -ItemType Directory -Force $appDirectory | Out-Null

if ($Exe) {
    $subject = "сборка $(Split-Path -Leaf $Exe)"
    Copy-Item $Exe (Join-Path $appDirectory 'DiskApp.exe')
}
else {
    # Текущий код публикуется так же, как для пользователя (scripts/publish.ps1): self-contained, ReadyToRun.
    Write-Host 'Публикация текущего кода (self-contained, ReadyToRun)…'
    & dotnet publish (Join-Path $root 'src\DiskApp.Desktop') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $appDirectory --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Публикация не удалась' }
    $subject = 'текущий код, self-contained с ReadyToRun (как scripts/publish.ps1, без single-file)'
}

# Портативный режим: данные рядом с exe, свой одиночный экземпляр — работающий DiskApp пользователя не задет.
New-Item -ItemType File (Join-Path $appDirectory 'portable') -Force | Out-Null
$app = Join-Path $appDirectory 'DiskApp.exe'
$logs = Join-Path $appDirectory 'data\logs'

# --- Помощники --------------------------------------------------------------------------------------------------
function Get-LogText {
    $file = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return '' }
    $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
}

function Wait-Log([string]$pattern, [int]$after, [int]$timeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $text = Get-LogText
        if ($text.Length -gt $after) {
            $match = [regex]::Match($text.Substring($after), $pattern)
            if ($match.Success) { return $match }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "В журнале не появилось: $pattern"
}

function Invoke-Second([string[]]$arguments) {
    Start-Process -FilePath $app -ArgumentList (@('--portable') + $arguments) -Wait -WindowStyle Hidden | Out-Null
}

function Stop-App([Diagnostics.Process]$process) {
    Invoke-Second @('--exit')
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'DiskApp не завершился за 30 с' }
}

function Start-App([string[]]$arguments) {
    $offset = (Get-LogText).Length
    $process = Start-Process -FilePath $app -ArgumentList (@('--portable') + $arguments) -PassThru
    $tray = Wait-Log 'Трей показан через (\d+) мс' $offset
    $server = Wait-Log 'Сервер слушает "(http://[^"]+)": готов через (\d+) мс' $offset
    [pscustomobject]@{
        Process = $process
        TrayMs = [int]$tray.Groups[1].Value
        ServerMs = [int]$server.Groups[2].Value
        BaseUrl = $server.Groups[1].Value
    }
}

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    if ($sorted.Count -eq 0) { return [double]::NaN }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

function Format-Number([double]$value, [string]$format = 'N0') { $value.ToString($format, [Globalization.CultureInfo]::GetCultureInfo('ru-RU')) }

$results = [Collections.Generic.List[object]]::new()
function Add-Result([string]$metric, [string]$budget, [string]$measured, [Nullable[bool]]$ok) {
    $results.Add([pscustomobject]@{ Metric = $metric; Budget = $budget; Measured = $measured; Ok = $ok })
}

# --- Дерево файлов ----------------------------------------------------------------------------------------------
$tree = Join-Path $workspace 'tree'
Write-Host "Дерево из $(Format-Number $Files) файлов…"
$random = [Random]::new(42)
$extensions = 'pdf', 'docx', 'xlsx', 'jpg', 'png', 'txt', 'md', 'zip', 'cs', 'json'
for ($i = 0; $i -lt $Files; $i++) {
    $folder = Join-Path $tree ("d{0:D3}\s{1:D2}" -f ($i % 200), ($i % 17))
    if ($i -lt 200 * 17) { [IO.Directory]::CreateDirectory($folder) | Out-Null }
    [IO.File]::WriteAllBytes((Join-Path $folder ("file-{0:D6}.{1}" -f $i, $extensions[$random.Next($extensions.Length)])), [byte[]]::new(16))
}

# --- 1. Первый запуск: корень и первичная индексация ------------------------------------------------------------
Write-Host 'Первый запуск копии и индексация…'
$first = Start-App @('--minimized', '--add', $tree)
$indexStart = Get-Date
$indexed = $null
$deadline = (Get-Date).AddMinutes(10)
do {
    Start-Sleep -Milliseconds 500
    try { $status = Invoke-RestMethod "$($first.BaseUrl)api/v1/index/status" } catch { $status = $null }
    if ($status -and $status.roots.Count -gt 0 -and $status.state -eq 'Idle' -and $status.totalEntries -ge $Files) { $indexed = (Get-Date) - $indexStart }
} while (-not $indexed -and (Get-Date) -lt $deadline)
Stop-App $first.Process
Add-Result 'Старт до трея, первый запуск копии' '< 2 с (холодный)' "$(Format-Number $first.TrayMs) мс" ($first.TrayMs -lt 2000)
if ($indexed) {
    $rate = $Files / $indexed.TotalSeconds
    Add-Result "Первичная индексация в приложении, $(Format-Number $Files) файлов" '≥ 2 000 файлов/с' "$(Format-Number $rate) файлов/с ($(Format-Number $indexed.TotalSeconds 'N1') с, с запуском корня)" ($rate -ge 2000)
}
else {
    Add-Result 'Первичная индексация в приложении' '≥ 2 000 файлов/с' 'не закончилась за 10 минут' $false
}

# --- 2. Тёплые запуски ------------------------------------------------------------------------------------------
$tray = @(); $server = @()
for ($run = 1; $run -le $StartRuns; $run++) {
    Write-Host "Тёплый запуск $run из $StartRuns…"
    $started = Start-App @('--minimized')
    $tray += $started.TrayMs; $server += $started.ServerMs
    Start-Sleep -Seconds 2
    Stop-App $started.Process
}
$warm = Get-Median $tray
Add-Result 'Старт до трея, тёплый (медиана)' '< 1 с' "$(Format-Number $warm) мс (от $(Format-Number ($tray | Measure-Object -Minimum).Minimum) до $(Format-Number ($tray | Measure-Object -Maximum).Maximum))" ($warm -lt 1000)
Add-Result 'Сервер внутри процесса готов (медиана)' '—' "$(Format-Number (Get-Median $server)) мс" $null

# --- 3. Окно поиска и простой -----------------------------------------------------------------------------------
Write-Host 'Окно быстрого поиска…'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$automation = [Windows.Automation.AutomationElement]
$session = Start-App @('--minimized')
$byProcess = New-Object Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $session.Process.Id)

# Перед каждым показом окно прячется (закрытие окна поиска лишь скрывает его): иначе «показ» видимого окна — 0 мс.
function Hide-QuickSearch {
    foreach ($window in $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
        $pattern = $null
        if ($window.Current.Name -like '*быстрый поиск*' -and $window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $pattern.Close() }
    }
}

Start-Sleep -Seconds 3
$offset = (Get-LogText).Length
for ($show = 1; $show -le $PopupShows; $show++) {
    Invoke-Second @('--search')
    Start-Sleep -Milliseconds 800
    Hide-QuickSearch
    Start-Sleep -Milliseconds 400
}
$shows = [regex]::Matches((Get-LogText).Substring($offset), 'Окно поиска: показ №(\d+) за (\d+) мс') | ForEach-Object { [int]$_.Groups[2].Value }
if ($shows.Count -gt 0) {
    $later = if ($shows.Count -gt 1) { Get-Median ($shows | Select-Object -Skip 1) } else { [double]::NaN }
    Add-Result 'Показ окна поиска, первый' '< 100 мс' "$(Format-Number $shows[0]) мс" ($shows[0] -lt 100)
    Add-Result 'Показ окна поиска, следующие (медиана)' '< 100 мс' "$(Format-Number $later) мс" ($later -lt 100)
}

# Память, пока приложение живёт только в трее и окне поиска.
Start-Sleep -Seconds 15
$session.Process.Refresh()
$trayWorkingSet = $session.Process.WorkingSet64 / 1MB
$trayPrivate = $session.Process.PrivateMemorySize64 / 1MB
Add-Result 'Память: трей и окно поиска, без главного окна' '< 150 МБ' "$(Format-Number $trayWorkingSet) МБ рабочий набор, выделено $(Format-Number $trayPrivate)" ($trayWorkingSet -lt 150)

# Главное окно показывается и закрывается в трей, как после обычной работы; потом — простой.
Invoke-Second @('--show')
Start-Sleep -Seconds 4
foreach ($window in $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
    $pattern = $null
    if ($window.Current.Name -eq 'DiskApp' -and $window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $pattern.Close() }
}

Write-Host "Простой $IdleSeconds с…"
Start-Sleep -Seconds 15
$session.Process.Refresh()
$cores = [Environment]::ProcessorCount
$startCpu = $session.Process.TotalProcessorTime
$startTime = Get-Date
$workingSets = @(); $privates = @()
$end = $startTime.AddSeconds($IdleSeconds)
while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds 5
    $session.Process.Refresh()
    $workingSets += $session.Process.WorkingSet64
    $privates += $session.Process.PrivateMemorySize64
}
$session.Process.Refresh()
$cpu = ($session.Process.TotalProcessorTime - $startCpu).TotalSeconds / ((Get-Date) - $startTime).TotalSeconds / $cores * 100
$finalWorkingSet = $workingSets[-1] / 1MB
$maxWorkingSet = ($workingSets | Measure-Object -Maximum).Maximum / 1MB
$finalPrivate = $privates[-1] / 1MB
$handles = $session.Process.HandleCount
Stop-App $session.Process
Add-Result "Память в простое через $IdleSeconds с, рабочий набор" '< 150 МБ' "$(Format-Number $finalWorkingSet) МБ (макс. $(Format-Number $maxWorkingSet), выделено $(Format-Number $finalPrivate))" ($finalWorkingSet -lt 150)
Add-Result 'CPU в простое' '≈ 0 %' "$(Format-Number $cpu 'N2') % (дескрипторов $(Format-Number $handles))" ($cpu -lt 0.5)

# --- 4. Проверки бюджета ядра -----------------------------------------------------------------------------------
if (-not $SkipBenchmarks) {
    $benchmarks = Join-Path $root 'tests\DiskApp.Benchmarks'
    Write-Host 'Сборка бенчмарков…'
    & dotnet build $benchmarks -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Сборка бенчмарков не удалась' }

    function Invoke-Benchmark([string[]]$arguments) {
        $output = & dotnet run -c Release --no-build --project $benchmarks -- @arguments 2>&1 | ForEach-Object { "$_" }
        $output | Out-Host
        return $output
    }

    function Get-MaxP95([string[]]$output) {
        $values = $output | ForEach-Object {
            $match = [regex]::Match($_, '^(?<query>.{28})\s+(?<p50>\d+\.\d)\s+(?<p95>\d+\.\d)\s+(?<max>\d+\.\d)\s')
            if ($match.Success) { [double]::Parse($match.Groups['p95'].Value, $culture) }
        }
        if (-not $values) { return [double]::NaN }
        return ($values | Measure-Object -Maximum).Maximum
    }

    Write-Host "scan-budget $ScanFiles…"
    $scan = Invoke-Benchmark @('scan-budget', "$ScanFiles")
    $scanOk = $LASTEXITCODE -eq 0
    $scanLine = $scan | Where-Object { $_ -match 'записей/с' } | Select-Object -First 1
    $scanText = if ("$scanLine" -match ': (?<seconds>[\d.]+) с, найдено .*?(?<rate>[\d,]+) записей/с') {
        "$(Format-Number ([double]::Parse($Matches['seconds'], $culture)) 'N1') с, $($Matches['rate'] -replace ',', ' ') записей/с"
    } else { "$scanLine".Trim() }
    Add-Result "Сканирование $(Format-Number $ScanFiles) файлов (scan-budget)" '≥ 2 000 файлов/с; 100 тыс. ≤ 60 с' $scanText $scanOk

    if (-not $SearchDatabase) {
        $benchData = Join-Path ([IO.Path]::GetTempPath()) 'DiskApp.Benchmarks'
        $SearchDatabase = if ($SearchRecords -ge 1000000 -and (Test-Path (Join-Path $benchData 'search-1m.db'))) { Join-Path $benchData 'search-1m.db' } else { Join-Path $benchData "search-$SearchRecords.db" }
    }

    Write-Host "search-budget $SearchRecords…"
    $search = Invoke-Benchmark @('search-budget', "$SearchRecords", $SearchDatabase)
    $searchP95 = Get-MaxP95 $search
    Add-Result "Поиск p95, сервис, $(Format-Number $SearchRecords) записей (худший из 20 запросов)" '< 50 мс' "$(Format-Number $searchP95 'N1') мс" ($searchP95 -lt 50)
    $typing = $search | Where-Object { $_ -match 'по буквам' } | Select-Object -First 1
    if ($typing -match ':\s*(.+) мс') {
        $worst = ($Matches[1] -split '\s*/\s*' | ForEach-Object { [double]::Parse($_, $culture) } | Measure-Object -Maximum).Maximum
        Add-Result 'Обновление результатов после ввода: задержка 60 мс + поиск по букве' '≤ 100 мс' "$(Format-Number (60 + $worst) 'N0') мс в худшем шаге" ((60 + $worst) -le 100)
    }

    Write-Host 'api-budget…'
    $api = Invoke-Benchmark @('api-budget', $SearchDatabase)
    $apiP95 = Get-MaxP95 $api
    Add-Result 'Поиск p95 через API (худший из 20 запросов)' '< 60 мс' "$(Format-Number $apiP95 'N1') мс" ($apiP95 -lt 60)
    $parallel = $api | Where-Object { $_ -match '^Нагрузка:' } | Select-Object -First 1
    if ($parallel) { Add-Result '100 одновременных поисков через API' 'p95 < 60 мс' "$parallel".Trim() $null }
    $size = (Get-Item $SearchDatabase).Length / 1MB
    Add-Result "Размер БД на $(Format-Number $SearchRecords) записей" '≤ 250 МБ на 1 млн' "$(Format-Number $size) МБ" ($SearchRecords -lt 1000000 -or $size -le 250)
}

# --- Отчёт ------------------------------------------------------------------------------------------------------
$cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$memory = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$os = Get-CimInstance Win32_OperatingSystem
$disk = (Get-PhysicalDisk | Where-Object { $_.DeviceId -eq '0' } | Select-Object -First 1)
$dotnet = (& dotnet --version)
$battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
$power = if (-not $battery) { 'питание от сети' } elseif ($battery.BatteryStatus -eq 2) { 'ноутбук от сети' } else { "ноутбук от батареи ($($battery.EstimatedChargeRemaining) %)" }
$scheme = if ((powercfg /getactivescheme) -match '\((.+)\)') { $Matches[1] } else { 'схема питания неизвестна' }
$machine = "$cpuName, $([Environment]::ProcessorCount) потоков, $memory ГБ ОЗУ, $($os.Caption) $($os.BuildNumber), диск $($disk.MediaType) $($disk.BusType), .NET SDK $dotnet, $power, схема «$scheme»"

$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Замер бюджетов DiskApp $version — $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$lines.Add('')
$lines.Add("Машина: $machine.")
$lines.Add('')
$lines.Add("Что замерялось: $subject; дерево $(Format-Number $Files) файлов; запусков $StartRuns; показов окна поиска $PopupShows; простой $IdleSeconds с$(if ($Quick) { '; короткий прогон -Quick' }).")
$lines.Add('')
$lines.Add('| Метрика | Бюджет | Замер | Итог |')
$lines.Add('|---|---|---|---|')
foreach ($result in $results) {
    $mark = if ($null -eq $result.Ok) { '—' } elseif ($result.Ok) { '✅' } else { '⚠️' }
    $lines.Add("| $($result.Metric) | $($result.Budget) | $($result.Measured -replace '\|', '/') | $mark |")
}

$report = Join-Path $root ("artifacts\perf\perf-" + (Get-Date -Format 'yyyyMMdd-HHmm') + ".md")
New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
$lines | Set-Content -Path $report -Encoding utf8
Write-Host ''
$lines | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Отчёт: $report"

if (-not $Keep) { Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue }
