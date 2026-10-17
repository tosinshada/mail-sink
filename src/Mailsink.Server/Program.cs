using Mailsink.Api;
using Mailsink.Core.Options;
using Mailsink.Hosting;

// `mailsink health [url]` is the Docker HEALTHCHECK: exit 0 when /api/v1/health answers.
if (args is ["health", ..])
{
    var url = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("MAILSINK_HEALTH_URL") ?? "http://127.0.0.1:8025/api/v1/health";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"unhealthy: {ex.Message}");
        return 1;
    }
}

// Short CLI flags; any setting also works as --Mailsink:Section:Key=value or MAILSINK__Section__Key.
var switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--data-dir"] = "Mailsink:DataDir",
    ["--storage"] = "Mailsink:Storage",
    ["--bind"] = "Mailsink:BindAddress",
    ["--hostname"] = "Mailsink:Hostname",
    ["--domain"] = "Mailsink:DefaultDomain",
    ["--auth-mode"] = "Mailsink:Auth:Mode",
    ["--smtp-port"] = "Mailsink:Smtp:Capture:Port",
    ["--submission-port"] = "Mailsink:Smtp:Submission:Port",
    ["--smtps-port"] = "Mailsink:Smtp:Smtps:Port",
    ["--imap-port"] = "Mailsink:Imap:Port",
    ["--imaps-port"] = "Mailsink:Imap:ImapsPort",
    ["--pop3-port"] = "Mailsink:Pop3:Port",
    ["--http"] = "Mailsink:Http:Urls",
    ["--ui-auth-file"] = "Mailsink:Http:UiAuthFile",
    ["--max-messages"] = "Mailsink:Retention:MaxMessages",
};

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
builder.Configuration.AddCommandLine(args, switches);

var options = builder.Configuration.GetSection(MailsinkOptions.SectionName).Get<MailsinkOptions>() ?? new MailsinkOptions();
builder.WebHost.UseUrls(options.Http.Urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.Smtp.MaxMessageSize * 2);

builder.Services.AddMailsink(builder.Configuration);
builder.Services.AddMailsinkApi();

var app = builder.Build();
if (options.Http.BasePath is { Length: > 1 } basePath)
{
    app.UsePathBase(basePath.TrimEnd('/'));
}

app.MapMailsinkApi();
app.UseMailsinkUi();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var registry = app.Services.GetRequiredService<Mailsink.Core.Networking.EndpointRegistry>();
    var ports = string.Join("  ", registry.All.Select(e => $"{e.Name.ToUpperInvariant()} {e.Port}"));
    app.Logger.LogInformation("Mailsink ready. {Ports}  HTTP {Urls}", ports, options.Http.Urls);
});

await app.RunAsync();
return 0;
