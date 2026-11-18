using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WirelessCable.Server.WebUi;

/// <summary>
/// 新版界面的静态资源（/app）。
///
/// 两种来源，按优先级：
/// <list type="number">
/// <item><c>BaseDirectory/wwwroot</c> 存在时读磁盘 —— 开发时把 <c>vite build</c> 的产物丢进去就能热改；</item>
/// <item>否则读**嵌进程序集的资源** —— 发布出去仍然是一个 exe，不用在旁边摆一个 wwwroot 目录。</item>
/// </list>
///
/// 只有 <c>index.html</c> 会做令牌注入（且只在 loopback 请求上），静态资源一律不带令牌。
/// </summary>
public static class LocalUiStatic
{
    private const string ResourcePrefix = "webui/";

    private static Dictionary<string, string>? _resources;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".map"] = "application/json; charset=utf-8",
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/app/{*rest}", (HttpContext context, string? rest) =>
        {
            var path = Normalize(string.IsNullOrWhiteSpace(rest) ? "index.html" : rest!);

            // 深链接（/app/transfers）回 index.html，由前端自己的状态接管路由
            if (!TryOpen(path, out var stream, out var contentType))
            {
                path = "index.html";
                if (!TryOpen(path, out stream, out contentType))
                {
                    return Results.NotFound();
                }
            }

            if (string.Equals(path, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                string html;
                using (stream)
                using (var reader = new StreamReader(stream))
                {
                    html = reader.ReadToEnd();
                }

                if (LocalUiSession.IsLoopback(context))
                {
                    var session = context.RequestServices.GetRequiredService<LocalUiSession>();
                    html = html.Replace(
                        "</head>",
                        $"<script>window.__WC_LOCAL__='{session.Token}'</script></head>",
                        StringComparison.OrdinalIgnoreCase);

                    // 带令牌的页面不能进任何缓存
                    context.Response.Headers.CacheControl = "no-store";
                }

                return Results.Content(html, contentType);
            }

            // 资源不要在这里 using 掉：结果对象在管道里才真正读流，
            // 提前释放会得到 ObjectDisposedException（真机踩过）。交给框架释放。
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Stream(stream, contentType);
        });
    }

    /// <summary>先在 wwwroot 里找，再在嵌入资源里找。</summary>
    private static bool TryOpen(string path, out Stream stream, out string contentType)
    {
        contentType = ContentTypes.TryGetValue(Path.GetExtension(path), out var found)
            ? found
            : "application/octet-stream";

        var physical = Path.Combine(AppContext.BaseDirectory, "wwwroot", path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(physical))
        {
            stream = File.OpenRead(physical);
            return true;
        }

        _resources ??= BuildResourceMap();
        if (_resources.TryGetValue(path, out var resourceName))
        {
            var opened = typeof(LocalUiStatic).Assembly.GetManifestResourceStream(resourceName);
            if (opened is not null)
            {
                stream = opened;
                return true;
            }
        }

        stream = Stream.Null;
        return false;
    }

    /// <summary>
    /// 资源名 → 请求路径。
    ///
    /// MSBuild 的 <c>%(RecursiveDir)</c> 在 Windows 上带反斜杠，这里统一换成 '/'，
    /// 并且把逻辑名里的 <c>webui/</c> 前缀切掉 —— 请求路径就是 <c>assets/index.js</c>。
    /// </summary>
    private static Dictionary<string, string> BuildResourceMap()
    {
        var assembly = typeof(LocalUiStatic).Assembly;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[normalized[ResourcePrefix.Length..]] = name;
        }

        return map;
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimStart('/');

        // 只允许目录内相对路径：../ 之类一律当根，不让它去碰别的资源
        if (trimmed.Length == 0 || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return "index.html";
        }

        return trimmed;
    }
}
