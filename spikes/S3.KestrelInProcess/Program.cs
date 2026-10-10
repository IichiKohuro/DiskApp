// Спайк S3: Kestrel + Minimal API + SignalR внутри процесса на 127.0.0.1:0 (решение 1 плана).
// Вопросы: время старта, память, задержка HTTP и SignalR по loopback, общий DI-контейнер.
// Avalonia здесь не поднимается: её собственный старт измеряет прототип (S1/S2); Kestrel от этого не зависит.

using System.Diagnostics;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;
var process = Process.GetCurrentProcess();
long Working() { process.Refresh(); return process.WorkingSet64; }
long Managed() => GC.GetTotalMemory(forceFullCollection: true);

var report = new StringBuilder();
void Line(string text) { Console.WriteLine(text); report.AppendLine(text); }

Line($"S3: .NET {Environment.Version}, {Environment.ProcessorCount} CPU, {Environment.MachineName}");
Line($"До старта: рабочий набор {Working() / 1048576.0:F1} МБ, управляемая память {Managed() / 1048576.0:F1} МБ");

foreach (var slim in new[] { true, false })
{
    var before = Working();
    var startWatch = Stopwatch.StartNew();
    var builder = slim ? WebApplication.CreateSlimBuilder() : WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<Counter>();

    var app = builder.Build();
    app.MapGet("/ping", static (Counter counter) => { counter.Hits++; return "pong"; });
    app.MapHub<PingHub>("/hub");
    await app.StartAsync();
    startWatch.Stop();
    var address = app.Urls.Single();
    var afterStart = Working();
    Line($"[{(slim ? "CreateSlimBuilder" : "CreateBuilder")}] старт Kestrel + SignalR: {startWatch.Elapsed.TotalMilliseconds:F0} мс; адрес {address}; рабочий набор +{(afterStart - before) / 1048576.0:F1} МБ (итого {afterStart / 1048576.0:F1} МБ)");

    using var http = new HttpClient { BaseAddress = new Uri(address) };
    var firstWatch = Stopwatch.StartNew();
    await http.GetStringAsync("/ping");
    firstWatch.Stop();
    var httpTimes = new List<double>();
    for (var i = 0; i < 200; i++)
    {
        var w = Stopwatch.StartNew();
        await http.GetStringAsync("/ping");
        httpTimes.Add(w.Elapsed.TotalMilliseconds);
    }

    httpTimes.Sort();
    Line($"  HTTP GET /ping: первый {firstWatch.Elapsed.TotalMilliseconds:F1} мс, затем медиана {httpTimes[100]:F2} мс, p95 {httpTimes[190]:F2} мс");

    var connectWatch = Stopwatch.StartNew();
    await using var hub = new HubConnectionBuilder().WithUrl(address + "/hub").Build();
    var pongs = new List<double>();
    hub.On<long>("Pong", sent => pongs.Add((Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency));
    await hub.StartAsync();
    connectWatch.Stop();

    var invokeTimes = new List<double>();
    for (var i = 0; i < 200; i++)
    {
        var w = Stopwatch.StartNew();
        await hub.InvokeAsync<string>("Ping");
        invokeTimes.Add(w.Elapsed.TotalMilliseconds);
    }

    invokeTimes.Sort();
    for (var i = 0; i < 200; i++) await hub.InvokeAsync("Push", Stopwatch.GetTimestamp());
    await Task.Delay(200);
    pongs.Sort();
    Line($"  SignalR: подключение {connectWatch.Elapsed.TotalMilliseconds:F0} мс; Invoke медиана {invokeTimes[100]:F2} мс, p95 {invokeTimes[190]:F2} мс; push сервер→клиент медиана {pongs[pongs.Count / 2]:F2} мс ({pongs.Count} из 200 получено)");

    var counter = app.Services.GetRequiredService<Counter>();
    var sameInstance = ReferenceEquals(counter, PingHub.LastCounter);
    Line($"  Общий DI: Counter из app.Services и из хаба — {(sameInstance ? "один экземпляр" : "РАЗНЫЕ экземпляры")}, обращений к /ping: {counter.Hits}");

    var afterLoad = Working();
    Line($"  После нагрузки: рабочий набор {afterLoad / 1048576.0:F1} МБ, управляемая {Managed() / 1048576.0:F1} МБ");

    var stopWatch = Stopwatch.StartNew();
    await app.StopAsync();
    await app.DisposeAsync();
    Line($"  Остановка: {stopWatch.Elapsed.TotalMilliseconds:F0} мс");
    Line("");
}

var resultFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.md"));
File.WriteAllText(resultFile, report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Отчёт: {resultFile}");

sealed class Counter
{
    public int Hits { get; set; }
}

sealed class PingHub(Counter counter) : Hub
{
    public static Counter? LastCounter { get; private set; }

    public string Ping()
    {
        LastCounter = counter;
        return "pong";
    }

    public Task Push(long timestamp) => Clients.Caller.SendAsync("Pong", timestamp);
}
