using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SyncManager;
using SyncManager.Hubs;
using SyncManager.Middleware;
using SyncManager.Services;
using SyncCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// === Kestrel: слушаем только на внутреннем адресе (nginx → localhost) ===
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000);
});

// === Настройки ===
builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("Proxy"));
builder.Services.Configure<ReportUiSettings>(builder.Configuration.GetSection("Report"));

// === Аутентификация (заглушка: пользователь задаётся ForwardedUserMiddleware) ===
builder.Services.AddAuthentication("Default")
    .AddCookie("Default", options =>
    {
        options.Cookie.Name = ".SyncAuth";
        options.LoginPath = new PathString("/");
        options.Events.OnValidatePrincipal = context =>
        {
            // Пропускаем валидацию: пользователь уже установлен middleware'ом
            return Task.CompletedTask;
        };
    });

// === MVC ===
// UTC-даты сериализуем с суффиксом "Z", чтобы браузер (JS new Date)
// интерпретировал их как UTC, а не как локальное время (сдвиг на часовой пояс).
builder.Services.AddControllersWithViews().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
});

// === SignalR: realtime-обновления дашборда и раздела отчётов ===
builder.Services.AddSignalR();

// === LISTEN/NOTIFY: сигнал refresh клиентам при изменениях в БД (резерв — polling 30 с) ===
builder.Services.AddHostedService<DbChangeListener>();

// === Database context for sync bus ===
builder.Services.AddDbContext<SyncBusContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SyncBus")));

var app = builder.Build();

// === ForwardedHeaders: доверяем заголовкам от nginx ===
var fwdOptions = new ForwardedHeadersOptions();
fwdOptions.KnownProxies.Add(System.Net.IPAddress.Parse("127.0.0.1"));
app.UseForwardedHeaders(fwdOptions);

// === Статические файлы ===
app.UseStaticFiles();

// === Аутентификация из заголовков nginx ===
app.UseMiddleware<ForwardedUserMiddleware>();

// === Маршрутизация ===
app.UseRouting();
app.UseAuthorization();

// SignalR-хаб realtime-обновлений (WebSocket; аутентификация — та же, что для MVC:
// ForwardedUserMiddleware стоит до хабов в пайплайне)
app.MapHub<SyncHub>("/hubs/sync");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Sync}/{action=Dashboard}/{id?}");

app.Run();

/// <summary>
/// Сериализует DateTime с Kind=Utc в ISO-8601 с суффиксом "Z".
/// Без суффикса JS new Date() трактует строку как локальное время —
/// в колонках дат и таймлайне получался сдвиг на часовой пояс.
/// </summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture));
        }
    }
}
