<#
.SYNOPSIS
    Soak-тест (PLAN.md, этап 10.3): DiskApp долго работает в трее с активной индексацией, память и хендлы не растут.

.DESCRIPTION
    Запускает портативную копию DiskApp в отдельной папке (рабочие данные пользователя не трогаются), индексирует
    сгенерированное дерево и затем, пока идёт прогон, непрерывно меняет его: создаёт, дописывает, переименовывает
    и удаляет файлы и папки. Параллельно ищет через API и время от времени открывает и закрывает окно быстрого поиска.
    Раз в минуту записывает частный рабочий набор (как «Память» в Диспетчере задач), хендлы, потоки и объекты
    GDI/USER. В конце ждёт, пока индексатор догонит изменения, и сверяет число записей индекса с диском.

    Итог — CSV с выборками и сводка в artifacts\soak\soak-<дата>.md: рост между первой и последней четвертями
    прогона (первая четверть — прогрев) и наклон за вторую половину. Порог по умолчанию: память — не больше
    15 МБ, хендлы — не больше 100, объекты GDI/USER — не больше 50.

    Полный прогон — 48 часов (-Hours 48); короткая проверка скрипта — -Minutes 10. Пока идёт прогон, компьютер
    не засыпает (экран может гаснуть).

.PARAMETER Hours
    Длительность прогона в часах.

.PARAMETER Minutes
    Длительность в минутах вместо часов — для короткой проверки.

.PARAMETER Exe
    Какой exe гонять (например, releases\diskapp_v0.5.0_alpha_singlefile.exe); по умолчанию текущий код
    публикуется в рабочую папку.

.PARAMETER Files
    Файлов в начальном дереве.

.PARAMETER ChangesPerMinute
    Изменений файлов в минуту.

.PARAMETER SearchesPerMinute
    Поисков через API в минуту.

.PARAMETER Keep
    Не удалять рабочую папку.

.EXAMPLE
    .\scripts\soak.ps1 -Minutes 10
    .\scripts\soak.ps1 -Hours 48
    .\scripts\soak.ps1 -Hours 8 -Exe .\releases\diskapp_v0.5.0_alpha_singlefile.exe
#>
param(
    [double]$Hours = 48,
    [int]$Minutes,
    [string]$Exe,
    [int]$Files = 20000,
    [int]$ChangesPerMinute = 120,
    [int]$SearchesPerMinute = 30,
    [int]$SampleSeconds = 60,
    [int]$PopupEveryMinutes = 10,
    [double]$MemoryGrowthLimitMb = 15,
    [int]$HandleGrowthLimit = 100,
    [int]$GuiObjectGrowthLimit = 50,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$duration = if ($Minutes -gt 0) { [TimeSpan]::FromMinutes($Minutes) } else { [TimeSpan]::FromHours($Hours) }

Add-Type -Namespace DiskAppSoak -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# --- Что гоняем -------------------------------------------------------------------------------------------------
$workspace = Join-Path ([IO.Path]::GetTempPath()) ("DiskApp.Soak\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$appDirectory = Join-Path $workspace 'app'
New-Item -ItemType Directory -Force $appDirectory | Out-Null
if ($Exe) {
    $subject = "сборка $(Split-Path -Leaf $Exe)"
    Copy-Item $Exe (Join-Path $appDirectory 'DiskApp.exe')
}
else {
    Write-Host 'Публикация текущего кода (self-contained, ReadyToRun)…'
    & dotnet publish (Join-Path $root 'src\DiskApp.Desktop') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $appDirectory --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Публикация не удалась' }
    $subject = 'текущий код, self-contained с ReadyToRun'
}

# Портативный режим: данные рядом с exe, свой одиночный экземпляр — работающий DiskApp пользователя не задет.
New-Item -ItemType File (Join-Path $appDirectory 'portable') -Force | Out-Null
$app = Join-Path $appDirectory 'DiskApp.exe'
$logs = Join-Path $appDirectory 'data\logs'

function Get-LogText {
    $file = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return '' }
    $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
}

function Invoke-Second([string[]]$arguments) {
    Start-Process -FilePath $app -ArgumentList (@('--portable') + $arguments) -Wait -WindowStyle Hidden | Out-Null
}

function Get-Status([string]$baseUrl) {
    try { Invoke-RestMethod "${baseUrl}api/v1/index/status" -TimeoutSec 30 } catch { $null }
}

function Close-Window([int]$processId, [string]$namePattern) {
    $automation = [Windows.Automation.AutomationElement]
    $condition = New-Object Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $processId)
    foreach ($window in $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)) {
        $pattern = $null
        if ($window.Current.Name -like $namePattern -and $window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $pattern.Close() }
    }
}

# --- Дерево файлов ----------------------------------------------------------------------------------------------
$tree = Join-Path $workspace 'tree'
Write-Host "Дерево из $Files файлов…"
$random = [Random]::new(42)
$words = 'report', 'invoice', 'budget', 'photo', 'plan', 'отчёт', 'счёт', 'договор', 'проект', 'заметки'
$extensions = 'pdf', 'docx', 'xlsx', 'jpg', 'png', 'txt', 'md', 'zip', 'cs', 'json'
$folders = [Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt 1500; $i++) {
    $folder = Join-Path $tree ('{0}-{1:D3}' -f $words[$i % 10], ($i % 150))
    if (-not $folders.Contains($folder)) { [IO.Directory]::CreateDirectory($folder) | Out-Null; $folders.Add($folder) }
}
for ($i = 0; $i -lt $Files; $i++) {
    $name = '{0} {1} {2:D6}.{3}' -f $words[$random.Next(10)], (2020 + $random.Next(6)), $i, $extensions[$random.Next(10)]
    [IO.File]::WriteAllBytes((Join-Path $folders[$i % $folders.Count] $name), [byte[]]::new(16))
}

# --- Запуск и первичная индексация ------------------------------------------------------------------------------
# Компьютер не засыпает, пока идёт прогон (ES_CONTINUOUS | ES_SYSTEM_REQUIRED).
[DiskAppSoak.Native]::SetThreadExecutionState([uint32]2147483649) | Out-Null
$process = Start-Process -FilePath $app -ArgumentList '--portable', '--minimized', '--add', $tree -PassThru
$baseUrl = $null
$deadline = (Get-Date).AddSeconds(60)
while (-not $baseUrl -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    $match = [regex]::Match((Get-LogText), 'Сервер слушает "(http://[^"]+)"')
    if ($match.Success) { $baseUrl = $match.Groups[1].Value }
}
if (-not $baseUrl) { throw 'Сервер не запустился за 60 с' }
Write-Host 'Первичная индексация…'
$deadline = (Get-Date).AddMinutes(10)
do { Start-Sleep -Milliseconds 500; $status = Get-Status $baseUrl } while (-not ($status -and $status.state -eq 'Idle' -and $status.totalEntries -ge $Files) -and (Get-Date) -lt $deadline)

# --- Прогон -----------------------------------------------------------------------------------------------------
# Первый запрос к счётчикам производительности WMI занимает десятки секунд — делается до начала отсчёта.
Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($process.Id)" | Out-Null
$queries = 'report', 'отчет', 'inv', '2024', 'ext:pdf', 'budget 2025', 'договор ext:docx', 'size:>1kb', 'plna', 'заметки'
$created = [Collections.Generic.List[string]]::new()
$samples = [Collections.Generic.List[object]]::new()
$started = Get-Date
$end = $started + $duration
$nextSample = $started
$nextPopup = $started.AddMinutes($PopupEveryMinutes)
$changeDelay = [int](60000 / [math]::Max(1, $ChangesPerMinute))
$searchEvery = [math]::Max(1, [int]($ChangesPerMinute / [math]::Max(1, $SearchesPerMinute)))
$operations = 0; $errors = 0; $counter = 0
Write-Host "Прогон до $($end.ToString('dd.MM HH:mm')) ($([math]::Round($duration.TotalHours, 2)) ч)…"

while ((Get-Date) -lt $end) {
    if ($process.HasExited) { throw "DiskApp завершился во время прогона, код $($process.ExitCode)" }

    if ((Get-Date) -ge $nextSample) {
        $process.Refresh()
        $private = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($process.Id)").WorkingSetPrivate / 1MB
        $sample = [pscustomobject]@{
            Minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
            PrivateMb = [math]::Round($private, 1)
            Handles = $process.HandleCount
            Threads = $process.Threads.Count
            Gdi = [DiskAppSoak.Native]::GetGuiResources($process.Handle, 0)
            User = [DiskAppSoak.Native]::GetGuiResources($process.Handle, 1)
            Operations = $operations
        }
        $samples.Add($sample)
        Write-Host ('{0,7:N1} мин  память {1,6:N1} МБ  хендлы {2,5}  потоки {3,3}  GDI {4,4}  USER {5,4}  изменений {6}' -f $sample.Minutes, $sample.PrivateMb, $sample.Handles, $sample.Threads, $sample.Gdi, $sample.User, $operations)
        $nextSample = (Get-Date).AddSeconds($SampleSeconds)
    }

    if ((Get-Date) -ge $nextPopup) {
        Invoke-Second @('--search', $queries[$random.Next($queries.Length)])
        Start-Sleep -Seconds 2
        Close-Window $process.Id '*быстрый поиск*'
        $nextPopup = $nextPopup.AddMinutes($PopupEveryMinutes)
    }

    try {
        $folder = $folders[$random.Next($folders.Count)]
        $roll = $random.Next(10)
        if ($roll -lt 4) {
            $path = Join-Path $folder ('soak {0} {1:D8}.{2}' -f $words[$random.Next(10)], $counter++, $extensions[$random.Next(10)])
            [IO.File]::WriteAllBytes($path, [byte[]]::new($random.Next(1, 4096)))
            $created.Add($path)
        }
        elseif ($roll -lt 6) {
            if ($created.Count -gt 0) { [IO.File]::AppendAllText($created[$random.Next($created.Count)], 'x') }
        }
        elseif ($roll -eq 6) {
            if ($created.Count -gt 0) {
                $index = $random.Next($created.Count)
                $from = $created[$index]
                $to = Join-Path $folders[$random.Next($folders.Count)] ('moved {0:D8}{1}' -f $counter++, [IO.Path]::GetExtension($from))
                [IO.File]::Move($from, $to)
                $created[$index] = $to
            }
        }
        elseif ($roll -eq 7) {
            $sub = Join-Path $folder ('soak-folder {0:D8}' -f $counter++)
            [IO.Directory]::CreateDirectory($sub) | Out-Null
            for ($k = 0; $k -lt 5; $k++) {
                $path = Join-Path $sub "item $k.txt"
                [IO.File]::WriteAllText($path, 'soak')
                $created.Add($path)
            }
        }
        elseif ($created.Count -gt 200) {
            # Удаление держит размер дерева примерно постоянным.
            $index = $random.Next($created.Count)
            $path = $created[$index]
            $created.RemoveAt($index)
            [IO.File]::Delete($path)
            $parent = Split-Path -Parent $path
            if ((Split-Path -Leaf $parent) -like 'soak-folder*' -and -not [IO.Directory]::EnumerateFileSystemEntries($parent).GetEnumerator().MoveNext()) {
                [IO.Directory]::Delete($parent)
            }
        }

        $operations++
        if ($operations % $searchEvery -eq 0) {
            $query = [uri]::EscapeDataString($queries[$random.Next($queries.Length)])
            Invoke-RestMethod "${baseUrl}api/v1/search?q=$query&limit=50" -TimeoutSec 30 | Out-Null
        }
    }
    catch {
        $errors++
        if ($errors -le 10) { Write-Warning "Ошибка шага прогона: $($_.Exception.Message)" }
    }

    Start-Sleep -Milliseconds $changeDelay
}

# --- Сверка индекса с диском ------------------------------------------------------------------------------------
Write-Host 'Ожидание, пока индексатор догонит изменения…'
$diskCount = (Get-ChildItem $tree -Recurse -Force | Measure-Object).Count
$deadline = (Get-Date).AddMinutes(5)
do {
    Start-Sleep -Seconds 2
    $status = Get-Status $baseUrl
} while (-not ($status -and $status.state -eq 'Idle' -and $status.totalEntries -eq $diskCount) -and (Get-Date) -lt $deadline)
$indexCount = if ($status) { [long]$status.totalEntries } else { -1 }

Invoke-Second @('--exit')
$exitedCleanly = $process.WaitForExit(30000)
if (-not $exitedCleanly) { $process.Kill(); Write-Warning 'DiskApp не завершился за 30 с' }
[DiskAppSoak.Native]::SetThreadExecutionState([uint32]2147483648) | Out-Null

# --- Итог -------------------------------------------------------------------------------------------------------
function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return [double]::NaN }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

# Наклон прямой наименьших квадратов, единиц в час.
function Get-Slope($points, [string]$property) {
    $points = @($points)
    if ($points.Count -lt 2) { return [double]::NaN }
    $mx = ($points | Measure-Object Minutes -Average).Average
    $my = ($points | Measure-Object $property -Average).Average
    $num = 0.0
    $den = 0.0
    foreach ($p in $points) {
        $num += ($p.Minutes - $mx) * ($p.$property - $my)
        $den += ($p.Minutes - $mx) * ($p.Minutes - $mx)
    }
    if ($den -eq 0) { return 0 }
    return $num / $den * 60
}

$quarter = [math]::Max(1, [int][math]::Floor($samples.Count / 4))
$early = @($samples | Select-Object -Skip $quarter -First $quarter)
$late = @($samples | Select-Object -Last $quarter)
$secondHalf = @($samples | Select-Object -Last ([math]::Max(2, [int][math]::Floor($samples.Count / 2))))
$metrics = 'PrivateMb', 'Handles', 'Threads', 'Gdi', 'User'
$growth = @{}
foreach ($metric in $metrics) {
    $growth[$metric] = [pscustomobject]@{
        Early = Get-Median ($early | ForEach-Object { [double]$_.$metric })
        Late = Get-Median ($late | ForEach-Object { [double]$_.$metric })
        Slope = Get-Slope $secondHalf $metric
    }
}
$memoryOk = ($growth.PrivateMb.Late - $growth.PrivateMb.Early) -le $MemoryGrowthLimitMb
$handlesOk = ($growth.Handles.Late - $growth.Handles.Early) -le $HandleGrowthLimit
$guiOk = (($growth.Gdi.Late - $growth.Gdi.Early) -le $GuiObjectGrowthLimit) -and (($growth.User.Late - $growth.User.Early) -le $GuiObjectGrowthLimit)
$indexOk = $indexCount -eq $diskCount
$passed = $memoryOk -and $handlesOk -and $guiOk -and $indexOk -and $exitedCleanly

$artifacts = Join-Path $root 'artifacts\soak'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$csv = Join-Path $artifacts "soak-$stamp.csv"
$samples | Export-Csv $csv -NoTypeInformation -Encoding utf8
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$memory = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$labels = @{ PrivateMb = 'Память (частный рабочий набор), МБ'; Handles = 'Хендлы'; Threads = 'Потоки'; Gdi = 'Объекты GDI'; User = 'Объекты USER' }
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Soak-тест DiskApp, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
$lines.Add('')
$lines.Add("Что: $subject. Машина: $cpu, $memory ГБ. Длительность: $([math]::Round($duration.TotalHours, 2)) ч, изменений файлов: $operations (ошибок шагов: $errors), выборок: $($samples.Count).")
$lines.Add('')
$lines.Add('| Показатель | Первая четверть (после прогрева) | Последняя четверть | Наклон за вторую половину, в час |')
$lines.Add('|---|---|---|---|')
foreach ($metric in $metrics) {
    $g = $growth[$metric]
    $lines.Add(('| {0} | {1:N1} | {2:N1} | {3:N2} |' -f $labels[$metric], $g.Early, $g.Late, $g.Slope))
}
$lines.Add('')
$lines.Add("Индекс после прогона: $indexCount записей, на диске $diskCount — $(if ($indexOk) { 'совпадает' } else { 'РАСХОДИТСЯ' }).")
$lines.Add("Порог роста: память ≤ $MemoryGrowthLimitMb МБ, хендлы ≤ $HandleGrowthLimit, GDI/USER ≤ $GuiObjectGrowthLimit.")
$lines.Add("Итог: $(if ($passed) { 'пройден' } else { 'НЕ ПРОЙДЕН' }). Выборки: $(Split-Path -Leaf $csv).")
$summary = Join-Path $artifacts "soak-$stamp.md"
$lines | Set-Content $summary -Encoding utf8
$lines | ForEach-Object { Write-Host $_ }

if (-not $Keep) { Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue }
if (-not $passed) { exit 1 }
