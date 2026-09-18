using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace SyncManager.Middleware;

/// <summary>
/// Middleware: извлекает аутентифицированного пользователя из заголовков,
/// переданных nginx после успешной аутентификации в LDAP/AD.
/// В режиме Development — debug-заглушка (пользователь "Denis", роль sync-admins).
/// </summary>
public class ForwardedUserMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ProxySettings _proxySettings;
    private readonly IWebHostEnvironment _env;

    public ForwardedUserMiddleware(RequestDelegate next, IOptions<ProxySettings> proxySettings, IWebHostEnvironment env)
    {
        _next = next;
        _proxySettings = proxySettings.Value;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string user;
        List<string> groups;

        if (_env.IsDevelopment())
        {
            // Debug-заглушка для локальной отладки: пользователь захардкожен,
            // роль sync-admins добавляется принудительно — кнопки работают без nginx.
            user = "Denis";
            var groupsRaw = context.Request.Headers[_proxySettings.GroupHeader].ToString();
            groups = groupsRaw
                .Split(_proxySettings.GroupSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .ToList();

            if (!groups.Contains("sync-admins"))
            {
                groups.Add("sync-admins");
            }
        }
        else
        {
            // Продакшен: пользователь и роли — из заголовков nginx (X-Remote-User, X-Remote-Group)
            user = context.Request.Headers[_proxySettings.UserHeader].ToString();
            if (string.IsNullOrEmpty(user))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Не аутентифицирован. Обратитесь к администратору.");
                return;
            }

            var groupsRaw = context.Request.Headers[_proxySettings.GroupHeader].ToString();
            groups = groupsRaw
                .Split(_proxySettings.GroupSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .ToList();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(ClaimTypes.NameIdentifier, user)
        };
        claims.AddRange(groups.Select(g => new Claim(ClaimTypes.Role, g)));

        var identity = new ClaimsIdentity(claims, "NginxLdap");
        context.User = new ClaimsPrincipal(identity);

        await _next(context);
    }
}