using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Scalar.AspNetCore;

namespace DiskApp.Server;

/// <summary>
/// Сервер внутри процесса (PLAN.md, этап 4.1; ADR-0004): <see cref="WebApplication"/> на Kestrel, по умолчанию
/// <c>http://127.0.0.1:0</c>. Контейнер общий: десктоп добавляет свои сервисы через <c>configureServices</c>
/// и берёт их из <see cref="Services"/>. Создание быстрое и не открывает базу; база, индексатор и порт — в <see cref="StartAsync"/>.
/// </summary>
public sealed class DiskAppServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DiskAppServerHost(WebApplication app)
    {
        _app = app;
    }

    /// <summary>Общий контейнер сервисов.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Фактический адрес после <see cref="StartAsync"/>; до старта — <c>null</c>.</summary>
    public Uri? BaseAddress { get; private set; }

    /// <summary>
    /// Собирает сервер: сервисы Core и Infrastructure, промежуточные слои, маршруты.
    /// </summary>
    /// <param name="options">Параметры сервера.</param>
    /// <param name="configureServices">Регистрация сервисов оболочки и журнала в общем контейнере.</param>
    /// <param name="configureApp">Дополнительные маршруты и слои (тесты, отладка); вызывается до маршрутов API.</param>
    public static DiskAppServerHost Create(
        DiskAppServerOptions options,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DiskAppServerHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = options.EnvironmentName,

            // Конфигурационных файлов у сервера нет: не заводить наблюдателя за папкой приложения.
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        });

        // Журнал задаёт оболочка (Serilog у десктопа); без неё сервер молчит.
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(static kestrel => kestrel.AddServerHeader = false);
        builder.Services.AddDiskAppServer(options);
        var development = builder.Environment.IsDevelopment();
        if (development)
        {
            builder.Services.AddOpenApi(static openApi => openApi.AddDocumentTransformer(static (document, _, _) =>
            {
                document.Info.Title = "DiskApp API";
                document.Info.Description = "Локальный API DiskApp: корни и индекс, поиск, записи и действия, избранное, теги, коллекции, настройки.";
                return Task.CompletedTask;
            }));
        }

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseDiskAppServer();
        configureApp?.Invoke(app);
        app.MapDiskAppApi();
        if (development)
        {
            // Документ /openapi/v1.json и Scalar UI /scalar — только при разработке.
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return new DiskAppServerHost(app);
    }

    /// <summary>
    /// Открывает базу, запускает индексатор и Kestrel; после завершения известен <see cref="BaseAddress"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        BaseAddress = addresses?.FirstOrDefault() is { } address ? new Uri(address) : null;
    }

    /// <summary>
    /// Перестаёт принимать запросы, останавливает индексатор.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _app.StopAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
