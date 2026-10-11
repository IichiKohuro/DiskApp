<#
.SYNOPSIS
    Профилирование горячих путей DiskApp (PLAN.md, этап 10.1): старт, индексация, поиск, прокрутка, память.

.DESCRIPTION
    Публикует текущий код как для пользователя (self-contained, ReadyToRun) в отдельную папку и запускает его в
    портативном режиме — рабочие данные пользователя не трогаются. Инструменты — локальные из dotnet-tools.json
    (dotnet tool restore): dotnet-trace, dotnet-gcdump, dotnet-counters.

    Сценарии:
      startup  — трасса от запуска процесса до иконки в трее и ещё 3 с;
      indexing — трасса первичной индексации сгенерированного дерева;
      search   — трасса серии запросов к API поиска (те же 20 запросов, что в бюджетах);
      scroll   — трасса прокрутки списка результатов на странице «Поиск» главного окна;
      memory   — снимок управляемой кучи (gcdump) и счётчики среды после показа и скрытия главного окна.
    Для трасс печатается топ методов по исключительному и включительному времени (dotnet-trace report topN).
    Трассы открываются в Visual Studio, PerfView или speedscope (dotnet-trace convert --format speedscope).

.PARAMETER Scenario
    Какие сценарии снять; по умолчанию все.

.PARAMETER Files
    Файлов в дереве для индексации и поиска.

.PARAMETER Top
    Сколько методов показывать в топах.

.EXAMPLE
    .\scripts\profile.ps1
    .\scripts\profile.ps1 -Scenario startup,memory
#>
param(
    [ValidateSet('startup', 'indexing', 'search', 'scroll', 'memory')]
    [string[]]$Scenario = @('startup', 'indexing', 'search', 'scroll', 'memory'),
    [int]$Files = 50000,
    [int]$Top = 25,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore не удался' }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $output = Join-Path $root "artifacts\perf\profile-$stamp"
    $workspace = Join-Path ([IO.Path]::GetTempPath()) "DiskApp.Profile\$stamp"
    $appDirectory = Join-Path $workspace 'app'
    New-Item -ItemType Directory -Force $output, $appDirectory | Out-Null

    Write-Host 'Публикация текущего кода (self-contained, ReadyToRun)…'
    & dotnet publish (Join-Path $root 'src\DiskApp.Desktop') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $appDirectory --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Публикация не удалась' }
    New-Item -ItemType File (Join-Path $appDirectory 'portable') -Force | Out-Null
    $app = Join-Path $appDirectory 'DiskApp.exe'
    $logs = Join-Path $appDirectory 'data\logs'

    function Get-LogText {
        $file = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $file) { return '' }
        $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
        try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
    }

    function Wait-Log([string]$pattern, [int]$after, [int]$timeoutSeconds = 120) {
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

    function Start-App([string[]]$arguments) {
        $offset = (Get-LogText).Length
        $process = Start-Process -FilePath $app -ArgumentList (@('--portable') + $arguments) -PassThru
        $server = Wait-Log 'Сервер слушает "(http://[^"]+)"' $offset
        [pscustomobject]@{ Process = $process; BaseUrl = $server.Groups[1].Value }
    }

    function Stop-App([Diagnostics.Process]$process) {
        Invoke-Second @('--exit')
        if (-not $process.WaitForExit(30000)) { $process.Kill() }
    }

    function Wait-Indexed([string]$baseUrl, [int]$minimum) {
        $deadline = (Get-Date).AddMinutes(10)
        do {
            Start-Sleep -Milliseconds 500
            try { $status = Invoke-RestMethod "${baseUrl}api/v1/index/status" } catch { $status = $null }
        } while (-not ($status -and $status.state -eq 'Idle' -and $status.totalEntries -ge $minimum) -and (Get-Date) -lt $deadline)
    }

    function Start-Trace([int]$processId, [string]$name, [string]$duration) {
        $file = Join-Path $output "$name.nettrace"
        $arguments = @('tool', 'run', 'dotnet-trace', '--', 'collect', '-p', "$processId", '--profile', 'dotnet-sampled-thread-time', '--duration', $duration, '-o', $file)
        $tracer = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output "$name.trace-out.txt")
        Start-Sleep -Seconds 2
        [pscustomobject]@{ Process = $tracer; File = $file }
    }

    # Профиль dotnet-sampled-thread-time считает время всех потоков, в том числе ждущих: в сыром топе — одни ожидания.
    # Полный отчёт сохраняется, а на экран идут кадры без ожиданий и служебных кадров пула потоков.
    $idleFrames = 'WaitForSignal|GetQueuedCompletionStatus|WaitOne|Monitor\.Wait|IOCompletionPoller|RunLoop|WaitAny|InternalWaitCore|' +
        'ManualResetEventSlim|SpinWait|SpinThenBlockingWait|PollGC|ThreadPool|WorkerThread|ExecutionContext|TimerQueue|WaitHandle|' +
        'Semaphore|StartCallback|Thread\.Start|UnmanagedThreadStart|\(Non-Activities\)|^\d+\.\s+Threads\s'

    function Write-Top([string]$name, [string]$file) {
        foreach ($kind in @('exclusive', 'inclusive')) {
            $arguments = @('report', $file, 'topN', '-n', "$($Top * 4)")
            if ($kind -eq 'inclusive') { $arguments += '--inclusive' }
            $text = & dotnet tool run dotnet-trace -- @arguments 2>&1 | ForEach-Object { "$_" }
            $text | Set-Content -Path (Join-Path $output "$name-top-$kind.txt") -Encoding utf8
            Write-Host "== $name, $kind, без ожиданий"
            $text | Where-Object { $_ -notmatch $idleFrames } | Select-Object -First ($Top + 1) | ForEach-Object { Write-Host $_ }
        }
    }

    $tree = Join-Path $workspace 'tree'
    Write-Host "Дерево из $Files файлов…"
    $random = [Random]::new(42)
    $words = 'report', 'invoice', 'budget', 'photo', 'plan', 'draft', 'отчет', 'счет', 'договор', 'проект'
    $extensions = 'pdf', 'docx', 'xlsx', 'jpg', 'png', 'txt', 'md', 'zip', 'cs', 'json'
    for ($i = 0; $i -lt $Files; $i++) {
        $folder = Join-Path $tree ("{0}-{1:D3}\{2:D2}" -f $words[$i % $words.Length], ($i % 300), ($i % 23))
        if ($i -lt 300 * 23) { [IO.Directory]::CreateDirectory($folder) | Out-Null }
        $name = "{0} {1} {2:D5}.{3}" -f $words[$random.Next($words.Length)], (2020 + $random.Next(6)), $i, $extensions[$random.Next($extensions.Length)]
        [IO.File]::WriteAllBytes((Join-Path $folder $name), [byte[]]::new(16))
    }

    # Индексация — на пустых данных, поэтому первой; остальные сценарии работают с проиндексированным деревом.
    $session = Start-App @('--minimized')
    if ($Scenario -contains 'indexing') {
        Write-Host 'Сценарий indexing…'
        $trace = Start-Trace $session.Process.Id 'indexing' '00:00:00:40'
        Invoke-Second @('--add', $tree)
        Wait-Indexed $session.BaseUrl $Files
        $trace.Process.WaitForExit()
        Write-Top 'indexing' $trace.File
    }
    else {
        Invoke-Second @('--add', $tree)
        Wait-Indexed $session.BaseUrl $Files
    }

    if ($Scenario -contains 'search') {
        Write-Host 'Сценарий search…'
        $queries = '', 're', 'от', 'rep', 'отч', '202', 'repor', 'отчет', 'invoi', 'report 2025', 'отчет q3', 'rep ext:pdf',
            'report modified:year', 'in:2025 report', 'in:2025', 'type:image vacation', 'ext:pdf', 'size:>100mb', 'reprot', 'budget -draft ext:xlsx'
        $trace = Start-Trace $session.Process.Id 'search' '00:00:00:20'
        $until = (Get-Date).AddSeconds(16)
        while ((Get-Date) -lt $until) {
            foreach ($query in $queries) {
                Invoke-RestMethod "$($session.BaseUrl)api/v1/search?q=$([uri]::EscapeDataString($query))&limit=50" | Out-Null
            }
        }
        $trace.Process.WaitForExit()
        Write-Top 'search' $trace.File
    }

    if ($Scenario -contains 'scroll') {
        Write-Host 'Сценарий scroll…'
        Invoke-Second @('--search', 'report')
        Invoke-Second @('--page', 'search')
        Start-Sleep -Seconds 3
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $automation = [Windows.Automation.AutomationElement]
        $byProcess = New-Object Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $session.Process.Id)
        $byName = New-Object Windows.Automation.PropertyCondition($automation::NameProperty, 'Результаты поиска')
        $list = $null
        foreach ($window in $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
            if ($window.Current.Name -eq 'DiskApp') { $list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $byName) }
        }
        $pattern = $null
        if ($list -and $list.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
            $trace = Start-Trace $session.Process.Id 'scroll' '00:00:00:15'
            $until = (Get-Date).AddSeconds(12)
            $percent = 0
            while ((Get-Date) -lt $until) {
                $percent = ($percent + 7) % 100
                $pattern.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $percent)
                Start-Sleep -Milliseconds 30
            }
            $trace.Process.WaitForExit()
            Write-Top 'scroll' $trace.File
        }
        else {
            Write-Warning 'Список результатов не найден или не прокручивается — сценарий scroll пропущен.'
        }
    }

    if ($Scenario -contains 'memory') {
        Write-Host 'Сценарий memory…'
        Invoke-Second @('--show')
        Start-Sleep -Seconds 3
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $automation = [Windows.Automation.AutomationElement]
        $byProcess = New-Object Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $session.Process.Id)
        foreach ($window in $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
            $close = $null
            if ($window.Current.Name -eq 'DiskApp' -and $window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$close)) { $close.Close() }
        }
        Start-Sleep -Seconds 20
        $session.Process.Refresh()
        Write-Host ("Рабочий набор {0:N0} МБ, выделено {1:N0} МБ, дескрипторов {2}, потоков {3}" -f ($session.Process.WorkingSet64 / 1MB), ($session.Process.PrivateMemorySize64 / 1MB), $session.Process.HandleCount, $session.Process.Threads.Count)
        $dump = Join-Path $output 'memory.gcdump'
        & dotnet tool run dotnet-gcdump -- collect -p $session.Process.Id -o $dump | Out-Null
        $heap = & dotnet tool run dotnet-gcdump -- report $dump 2>&1 | ForEach-Object { "$_" }
        $heap | Set-Content -Path (Join-Path $output 'memory-heap.txt') -Encoding utf8
        Write-Host '== Управляемая куча (gcdump), крупнейшие типы'
        $heap | Select-Object -First ($Top + 6) | ForEach-Object { Write-Host $_ }
        $counters = Join-Path $output 'counters.csv'
        & dotnet tool run dotnet-counters -- collect -p $session.Process.Id --counters System.Runtime --duration 00:00:00:10 --format csv -o $counters | Out-Null
        Write-Host "Счётчики среды: $counters"
    }

    Stop-App $session.Process

    if ($Scenario -contains 'startup') {
        # Трасса с самого запуска: dotnet-trace запускает процесс и пишет до его завершения.
        Write-Host 'Сценарий startup…'
        $file = Join-Path $output 'startup.nettrace'
        $offset = (Get-LogText).Length
        $arguments = @('tool', 'run', 'dotnet-trace', '--', 'collect', '--profile', 'dotnet-sampled-thread-time', '-o', $file, '--', $app, '--portable', '--minimized')
        $tracer = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'startup.trace-out.txt')
        Wait-Log 'Трей показан через (\d+) мс' $offset | Out-Null
        Wait-Log 'Сервер слушает' $offset | Out-Null
        Start-Sleep -Seconds 3
        Invoke-Second @('--exit')
        $tracer.WaitForExit(60000) | Out-Null
        Write-Top 'startup' $file
    }

    Write-Host ''
    Write-Host "Трассы и отчёты: $output"
}
finally {
    Pop-Location
    if (-not $Keep -and $workspace) { Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue }
}
