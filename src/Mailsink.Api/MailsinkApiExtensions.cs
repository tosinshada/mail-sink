using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Mailsink.Api.Endpoints;
using Mailsink.Core.Domain;
using Mailsink.Core.Options;
using Scalar.AspNetCore;

namespace Mailsink.Api;

public static class MailsinkApiExtensions
{
    /// <summary>JSON (source-generated, with reflection fallback), SignalR, OpenAPI, CORS and the hub event forwarder.</summary>
    public static IServiceCollection AddMailsinkApi(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        services.AddSignalR().AddJsonProtocol(o =>
        {
            o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
            o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        services.AddOpenApi("v1", o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Mailsink API";
            document.Info.Description = "Capture, inspect and send test email and SMS. Real-time events: SignalR hub at /hubs/mail.";
            document.Info.Version = AdminEndpoints.Version;
            return Task.CompletedTask;
        }));
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<MailsinkOptions>>((cors, mailsink) =>
            {
                var origins = mailsink.Value.Http.CorsOrigins;
                if (origins.Count > 0)
                {
                    cors.AddPolicy("mailsink", policy => policy.WithOrigins([.. origins]).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
                }
            });
        services.AddHostedService<HubEventForwarder>();
        return services;
    }

    /// <summary>Maps <c>/api/v1</c>, <c>/hubs/mail</c>, the OpenAPI document and the Scalar reference at <c>/api/docs</c>.</summary>
    public static WebApplication MapMailsinkApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<MailsinkOptions>>().Value;
        if (!string.IsNullOrEmpty(options.Http.UiAuthFile))
        {
            app.UseMiddleware<BasicAuthMiddleware>();
        }

        if (options.Http.CorsOrigins.Count > 0)
        {
            app.UseCors("mailsink");
        }

        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            context.Response.StatusCode = error switch
            {
                MailStoreException => StatusCodes.Status400BadRequest,
                BadHttpRequestException bad => bad.StatusCode,
                System.Text.Json.JsonException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError,
            };
            await context.Response.WriteAsJsonAsync(new ErrorResponse(error?.Message ?? "Unexpected error"), ApiJsonContext.Default.ErrorResponse);
        }));

        var api = app.MapGroup("/api/v1");
        MessageEndpoints.Map(api);
        SendEndpoints.Map(api);
        AccountEndpoints.Map(api);
        AdminEndpoints.Map(api);
        SmsEndpoints.Map(api);
        app.MapFallback("/api/{**rest}", () => Results.NotFound(new ErrorResponse("No such API endpoint")));

        app.MapHub<MailHub>("/hubs/mail");
        app.MapOpenApi("/api/openapi/{documentName}.json");
        app.MapScalarApiReference("/api/docs", o => o
            .WithTitle("Mailsink API")
            .WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));
        return app;
    }

    /// <summary>Serves the SPA from <c>wwwroot</c> with client-side routing fallback.</summary>
    public static WebApplication UseMailsinkUi(this WebApplication app)
    {
        var env = app.Services.GetRequiredService<IWebHostEnvironment>();
        var index = env.WebRootFileProvider.GetFileInfo("index.html");
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
                }
                else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
                {
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
            },
        });

        if (index.Exists)
        {
            // The SPA is built with relative asset URLs. A <base href> pointing at the (possibly non-root) PathBase makes
            // deep links such as /emails/{id} and hosting under /mail/ both resolve ./assets/… correctly.
            string html;
            using (var reader = new StreamReader(index.CreateReadStream()))
            {
                html = reader.ReadToEnd();
            }

            app.MapFallback((HttpContext http) =>
            {
                var basePath = http.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
                var page = html.Replace("<head>", $"<head><base href=\"{basePath}/\">", StringComparison.OrdinalIgnoreCase);
                http.Response.Headers.CacheControl = "no-cache";
                return Results.Content(page, "text/html; charset=utf-8");
            });
        }
        else
        {
            app.MapFallback(() => Results.Content(
                """
                <!doctype html><html><head><title>Mailsink</title><meta name="color-scheme" content="dark light"></head>
                <body style="font-family:system-ui;padding:40px;max-width:640px">
                <h1>Mailsink is running</h1>
                <p>The web UI has not been built. Build it with <code>npm ci &amp;&amp; npm run build</code> in <code>src/Mailsink.Web</code>,
                or run <code>dotnet build -p:BuildWeb=true</code>.</p>
                <p>The API is available: <a href="/api/docs">API reference</a> · <a href="/api/v1/info">/api/v1/info</a></p>
                </body></html>
                """, "text/html"));
        }

        return app;
    }
}

/// <summary>
/// Optional HTTP basic auth for the UI and API (<c>Http:UiAuthFile</c>). Lines are <c>user:password</c>,
/// <c>user:{SHA}base64-sha1</c> (htpasswd -s) or <c>user:{SHA256}base64</c>. <c>/api/v1/health</c> stays open.
/// </summary>
internal sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Dictionary<string, string> _users;

    public BasicAuthMiddleware(RequestDelegate next, IOptions<MailsinkOptions> options)
    {
        _next = next;
        _users = File.ReadAllLines(options.Value.Http.UiAuthFile!)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains(':'))
            .Select(l => (User: l[..l.IndexOf(':')], Secret: l[(l.IndexOf(':') + 1)..]))
            .ToDictionary(x => x.User, x => x.Secret, StringComparer.Ordinal);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/health") || IsAuthorized(context.Request.Headers.Authorization.ToString()))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Mailsink\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }

    private bool IsAuthorized(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0 || !_users.TryGetValue(decoded[..colon], out var secret))
        {
            return false;
        }

        var password = decoded[(colon + 1)..];
        var expected = secret switch
        {
            _ when secret.StartsWith("{SHA}", StringComparison.Ordinal) => (secret[5..], Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(password)))),
            _ when secret.StartsWith("{SHA256}", StringComparison.Ordinal) => (secret[8..], Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)))),
            _ => (secret, password),
        };
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected.Item1), Encoding.UTF8.GetBytes(expected.Item2));
    }
}
