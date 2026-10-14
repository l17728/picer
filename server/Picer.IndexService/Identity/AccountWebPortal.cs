using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;
using System.Threading.RateLimiting;

namespace Picer.IndexService.Identity;

/// <summary>Server-rendered account portal. Device pairing never exports a user's password or a service-wide token.</summary>
public static partial class AccountWebPortal
{
    public const string CookieScheme = "Picer.Account";

    public static void AddServices(WebApplicationBuilder builder, AccountStore store)
    {
        builder.Services.AddSingleton(store);
        var protection = builder.Services.AddDataProtection().SetApplicationName("Picer.Account." + store.ServiceId)
            .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(store.Root, "secrets", "web-keys")));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-Picer-Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddAuthentication(CookieScheme).AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "__Host-Picer-Account";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.LoginPath = "/account/login";
            options.Events.OnValidatePrincipal = context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (id is null || store.SecurityEpoch(id)?.ToString(System.Globalization.CultureInfo.InvariantCulture) != context.Principal?.FindFirstValue("security_epoch")) context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.Method == "POST" &&
                    context.Request.Path is var path && (path == "/account/login" || path == "/account/register" || path == "/account/password")
                    ? RateLimitPartition.GetFixedWindowLimiter("password-work", _ => new FixedWindowRateLimiterOptions
                        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other")),
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.Method == "POST" &&
                    context.Request.Path is var path && (path == "/account/login" || path == "/account/register" || path == "/account/password")
                    ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other")));
            options.AddConcurrencyLimiter("account-work", limiter =>
            { limiter.PermitLimit = 16; limiter.QueueLimit = 32; limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; });
            options.AddFixedWindowLimiter("enrollment-work", limiter =>
            { limiter.PermitLimit = 120; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
        });
    }

    public static void Map(WebApplication app, AccountStore store, string publicOrigin, string spkiPin, ECDsa signingKey,
        bool allowRegistration)
    {
        publicOrigin = DeviceAuthorization.NormalizeOrigin(publicOrigin);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/account") && !context.Request.Path.StartsWithSegments("/identity"))
            { await next(); return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            if (context.Request.Method == "POST")
            {
                var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = DeviceAuthorization.MaximumFileBytes;
            }
            if (!string.Equals(context.Request.Scheme + "://" + context.Request.Host, publicOrigin, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; return; }
            try
            {
                var subject = Subject(context);
                if (subject is not null && store.RequiresPasswordChange(subject) &&
                    context.Request.Path != "/account/password" && context.Request.Path != "/account/logout")
                { context.Response.Redirect("/account/password"); return; }
                await next();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or UnauthorizedAccessException or
                System.Text.Json.JsonException or CryptographicException or AntiforgeryValidationException or SqliteException or KeyNotFoundException)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = exception is UnauthorizedAccessException ? 403 : 400;
                await context.Response.WriteAsync("请求无效、已过期或没有权限。请返回重试。");
            }
        });

        app.MapGet("/account/login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var request = RequestId(context.Request.Query["request"].ToString());
            return Page("登录照片空间", "<p>登录后可为当前电脑下载设备认证文件。</p>" +
                Form(context, antiforgery, "/account/login", request, "<label>账号<input name=login autocomplete=username maxlength=80 required></label>" +
                "<label>密码<input name=password type=password autocomplete=current-password maxlength=128 required></label><button>登录</button>") +
                (allowRegistration ? "<p><a href='/account/register?request=" + E(request) + "'>创建账号和照片空间</a></p>" : "<p>此服务暂不开放注册，请联系管理员开户。</p>"));
        });
        app.MapPost("/account/login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var account = store.Authenticate(login, password);
            if (account is null)
            {
                Core.Logging.AppLogger.Warn($"Account portal login rejected; accountMatch={store.ContainsLogin(login)}; loginFieldPresent={form.ContainsKey("login")}; loginLength={login.Length}; passwordFieldPresent={form.ContainsKey("password")}; passwordLength={password.Length}");
                return Page("登录未成功", "<p>账号或密码不正确，或账号暂时锁定。请稍后重试。</p><a href='/account/login'>返回登录</a>", 401);
            }
            await SignIn(context, store, account);
            if (store.RequiresPasswordChange(account)) return Results.Redirect("/account/password");
            return Results.Redirect(Destination(form["request"].ToString()));
        }).RequireRateLimiting("account-work");

        app.MapGet("/account/register", (HttpContext context, IAntiforgery antiforgery) =>
            !allowRegistration ? Results.NotFound() : Page("创建账号", Form(context, antiforgery, "/account/register",
                RequestId(context.Request.Query["request"].ToString()),
                "<label>账号<input name=login autocomplete=username minlength=3 maxlength=80 required></label>" +
                "<label>密码（至少 12 个字符）<input name=password type=password autocomplete=new-password minlength=12 maxlength=128 required></label>" +
                "<label>确认密码<input name=confirm type=password autocomplete=new-password minlength=12 maxlength=128 required></label>" +
                "<label>照片空间名称<input name=space value='我的照片空间' maxlength=80 required></label><p>同一账号的多台电脑可以将索引上传到这个空间。原始照片和视频仍留在来源电脑。</p><button>创建账号和空间</button>")));
        app.MapPost("/account/register", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!allowRegistration) return Results.NotFound();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["password"] != form["confirm"]) return Page("无法注册", "<p>两次输入的密码不一致。</p><a href='/account/register'>返回注册</a>", 400);
            string account;
            try { account = store.Register(form["login"].ToString(), form["password"].ToString(), form["space"].ToString()); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            { return Page("无法注册", "<p>无法使用这个账号，请更换账号或登录已有账号。</p><a href='/account/login'>登录</a>", 400); }
            await SignIn(context, store, account);
            return Results.Redirect(Destination(form["request"].ToString()));
        }).RequireRateLimiting("account-work");

        app.MapGet("/account", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var account = Subject(context);
            if (account is null) return Results.Redirect("/account/login");
            var body = new StringBuilder("<h2>照片空间</h2><ul>");
            foreach (var space in store.ListSpaces(account))
            {
                body.Append("<li>").Append(E(space.Name)).Append("（").Append(E(space.Role)).Append("）");
                if (space.Role == "owner") body.Append(" <a href='/account/sources?space=").Append(E(space.Id)).Append("'>管理来源设备</a>");
                body.Append("</li>");
            }
            body.Append("</ul><h2>连接另一台电脑</h2><p>请先在目标电脑的 Picer 中选择登录照片空间，生成设备配对请求。打开它提供的网页链接后，即可批准并下载仅适用于该设备的认证文件。</p>");
            body.Append("<p>没有配对请求时，可以下载服务地址文件；其中不包含登录凭据。</p><a href='/account/service-file'>下载服务地址文件</a><h2>设备</h2>");
            foreach (var device in store.ListDevices(account))
                body.Append("<article><b>").Append(E(device.Name)).Append("</b><p>").Append(E(device.Kind)).Append(" · ").Append(E(device.State)).Append("</p>")
                    .Append(Form(context, antiforgery, "/account/revoke", "", "<input type=hidden name=grant value='" + E(device.Id) + "'><button>撤销此设备</button>"))
                    .Append("</article>");
            body.Append("<p><a href='/account/password'>修改密码</a></p>");
            body.Append(Form(context, antiforgery, "/account/logout", "", "<button>退出登录</button>"));
            return Page("我的照片空间", body.ToString());
        });
        app.MapGet("/account/service-file", (HttpContext context) => Subject(context) is null ? Results.Redirect("/account/login") :
            Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = 1, serviceId = store.ServiceId, origin = publicOrigin, spkiPin }),
                "application/json", "Picer.picer-service"));
        app.MapGet("/account/approve", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var requestId = RequestId(context.Request.Query["request"].ToString());
            var account = Subject(context);
            if (account is null) return Results.Redirect("/account/login?request=" + requestId);
            var enrollment = store.GetEnrollment(requestId);
            if (enrollment is null) return Page("请求已失效", "<p>请在目标电脑重新开始连接。</p>", 410);
            using var recipient = DeviceAuthorization.ReadPublicKey(enrollment.PublicKey);
            var body = new StringBuilder("<p>请确认目标电脑显示相同的设备指纹，且确实属于你。</p><p>设备：")
                .Append(E(enrollment.DeviceName)).Append("（").Append(E(enrollment.ClientKind)).Append("）</p><code>")
                .Append(E(DeviceAuthorization.KeyThumbprint(recipient))).Append("</code><label>照片空间<select name=space>");
            foreach (var space in store.ListSpaces(account)) body.Append("<option value='").Append(E(space.Id)).Append("'>").Append(E(space.Name)).Append("</option>");
            body.Append("</select></label>");
            if (enrollment.ClientKind == "gui") body.Append("<label><input type=checkbox name=editing value=yes>允许上传和修改索引</label><label><input type=checkbox name=media value=yes>允许读取原始照片和视频</label><label><input type=checkbox name=analysis value=yes>允许调用 AI 分析和问答（可能产生计算费用）</label>");
            else body.Append("<p>将允许此驻留程序提供本机明确授权的来源目录。请在来源电脑中核对目录列表。</p>");
            body.Append("<p>认证文件有效期 15 分钟，只能由该目标设备兑换。</p><button>批准并下载认证文件</button>");
            return Page("授权自己的设备", Form(context, antiforgery, "/account/approve", requestId, body.ToString()));
        });
        app.MapPost("/account/approve", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var account = Subject(context) ?? throw new UnauthorizedAccessException();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var document = store.ApproveEnrollment(account, RequestId(form["request"].ToString()), form["space"].ToString(),
                publicOrigin, spkiPin, signingKey, form["editing"] == "yes", form["media"] == "yes", form["analysis"] == "yes");
            return Results.File(DeviceAuthorization.WriteFileDocument(document), "application/json", "Picer-device.picer-auth");
        }).RequireRateLimiting("account-work");
        app.MapPost("/account/revoke", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var account = Subject(context) ?? throw new UnauthorizedAccessException();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            store.Revoke(account, form["grant"].ToString());
            return Results.Redirect("/account");
        });
        app.MapPost("/account/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        { await antiforgery.ValidateRequestAsync(context); await context.SignOutAsync(CookieScheme); return Results.Redirect("/account/login"); });
        app.MapGet("/account/password", (HttpContext context, IAntiforgery antiforgery) => Subject(context) is null ? Results.Redirect("/account/login") :
            Page("修改密码", Form(context, antiforgery, "/account/password", "", "<p>修改密码后，所有设备需要重新登录。首次使用管理员代建账号时必须设置自己的密码。</p>" +
                "<label>当前密码<input type=password name=current autocomplete=current-password maxlength=128 required></label>" +
                "<label>新密码<input type=password name=password autocomplete=new-password minlength=12 maxlength=128 required></label>" +
                "<label>确认新密码<input type=password name=confirm autocomplete=new-password minlength=12 maxlength=128 required></label><button>保存新密码</button>")));
        app.MapPost("/account/password", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var account = Subject(context) ?? throw new UnauthorizedAccessException();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["password"] != form["confirm"]) return Page("密码未修改", "<p>两次输入的新密码不一致。</p><a href='/account/password'>返回</a>", 400);
            store.ChangePassword(account, form["current"].ToString(), form["password"].ToString());
            await context.SignOutAsync(CookieScheme);
            return Results.Redirect("/account/login");
        }).RequireRateLimiting("account-work");
        MapSources(app, store);
    }

    private static Task SignIn(HttpContext context, AccountStore store, string account) => context.SignInAsync(CookieScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account),
            new Claim("security_epoch", store.SecurityEpoch(account)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? throw new UnauthorizedAccessException())], CookieScheme)));
    private static string? Subject(HttpContext context) => context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
    private static string Destination(string request) => RequestId(request) is { Length: > 0 } id ? "/account/approve?request=" + id : "/account";
    private static string RequestId(string value) => Guid.TryParseExact(value, "N", out _) ? value : "";
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Form(HttpContext context, IAntiforgery antiforgery, string action, string request, string content)
    {
        var token = antiforgery.GetAndStoreTokens(context);
        return "<form method=post action='" + E(action) + "'><input type=hidden name='" + E(token.FormFieldName) + "' value='" + E(token.RequestToken) +
            "'><input type=hidden name=request value='" + E(request) + "'>" + content + "</form>";
    }
    private static IResult Page(string title, string body, int status = 200) => Results.Content(
        "<!doctype html><html lang=zh-CN><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>" + E(title) +
        " · Picer</title><style>body{font:16px system-ui;max-width:720px;margin:40px auto;padding:20px;background:#f5f7fa;color:#182433}label{display:block;margin:18px 0}input:not([type=checkbox]),select{display:block;box-sizing:border-box;width:100%;padding:10px}button{padding:10px 20px;cursor:pointer}article{background:white;padding:16px;margin:12px 0}code{overflow-wrap:anywhere}</style><main><h1>" + E(title) + "</h1>" + body + "</main></html>",
        "text/html; charset=utf-8", Encoding.UTF8, status);
}
