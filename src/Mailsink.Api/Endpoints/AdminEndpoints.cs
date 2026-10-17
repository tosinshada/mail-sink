using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Mailsink.Core.Chaos;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;
using Mailsink.Core.Templates;

namespace Mailsink.Api.Endpoints;

internal static class AdminEndpoints
{
    public static readonly string Version =
        typeof(AdminEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/health", (MetricsCollector metrics) => Results.Ok(new HealthResponse("ok", (DateTimeOffset.UtcNow - metrics.StartedAt).TotalSeconds)))
            .WithTags("Server").WithSummary("Liveness/readiness probe");
        api.MapGet("/info", InfoAsync).WithTags("Server").WithSummary("Version, ports, counts, uptime and effective configuration");
        api.MapGet("/metrics", MetricsAsync).WithTags("Server").WithSummary("Messages per minute, active sessions, store size");
        api.MapGet("/tls/cert", (CertificateProvider certificates) =>
                certificates.ExportPublicPem() is { } pem
                    ? Results.File(Encoding.ASCII.GetBytes(pem), "application/x-pem-file", "mailsink.crt")
                    : Results.NotFound(new ErrorResponse("TLS is not configured")))
            .WithTags("Server").WithSummary("Public TLS certificate (PEM) so tests can trust STARTTLS/IMAPS");

        var chaos = api.MapGroup("/chaos").WithTags("Chaos");
        chaos.MapGet("", (IChaosEngine engine) => Results.Ok(engine.Settings)).WithSummary("Fault-injection rules");
        chaos.MapPut("", (ChaosSettings settings, IChaosEngine engine) =>
        {
            var rules = settings.Rules.Select(r => r.Id == Guid.Empty ? r with { Id = Guid.NewGuid() } : r).ToList();
            var invalid = rules.FirstOrDefault(r => r.Probability is < 0 or > 1 || r.Response is { Code: < 200 or > 599 });
            if (invalid is not null)
            {
                return Results.BadRequest(new ErrorResponse($"Invalid rule {invalid.Id}: probability must be 0..1 and response code 200..599"));
            }

            engine.Update(new ChaosSettings(settings.Enabled, rules));
            return Results.Ok(engine.Settings);
        }).WithSummary("Replace the fault-injection rules");

        var logs = api.MapGroup("/logs").WithTags("Logs");
        logs.MapGet("", (TranscriptStore transcripts, string? protocol) =>
                Results.Ok(transcripts.List().Where(t => protocol is null || t.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase)).Select(t => t.ToSummary()).ToList()))
            .WithSummary("Recent SMTP/IMAP/POP3 sessions");
        logs.MapGet("/{id}", (string id, TranscriptStore transcripts) =>
                transcripts.Find(id) is { } t ? Results.Ok(ToDto(t)) : Results.NotFound(new ErrorResponse("Session not found")))
            .WithSummary("Full C:/S: transcript of a session");
        logs.MapDelete("", (TranscriptStore transcripts) =>
        {
            transcripts.Clear();
            return Results.NoContent();
        }).WithSummary("Clear transcripts");

        var templates = api.MapGroup("/templates").WithTags("Templates");
        templates.MapGet("", (TemplateService service) => Results.Ok(service.List())).WithSummary("Saved send templates");
        templates.MapPost("", (TemplateInput input, TemplateService service) =>
            string.IsNullOrWhiteSpace(input.Name)
                ? Results.BadRequest(new ErrorResponse("name is required"))
                : Results.Created("/api/v1/templates", service.Save(null, input))).WithSummary("Create a template");
        templates.MapPut("/{id:guid}", (Guid id, TemplateInput input, TemplateService service) =>
            service.Find(id) is null ? Results.NotFound(new ErrorResponse("Template not found")) : Results.Ok(service.Save(id, input))).WithSummary("Update a template");
        templates.MapDelete("/{id:guid}", (Guid id, TemplateService service) =>
            service.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse("Template not found"))).WithSummary("Delete a template");
    }

    public static TranscriptDto ToDto(SessionTranscript transcript) =>
        new(transcript.ToSummary(), [.. transcript.Lines.Select(l => new TranscriptLineDto(l.At, l.Direction.ToString(), l.Text))]);

    private static async Task<IResult> InfoAsync(IMailStore store, EndpointRegistry endpoints, MetricsCollector metrics, CertificateProvider certificates,
        IOptions<MailsinkOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        var stats = await store.GetCaptureStatsAsync(ct);
        var accounts = await store.ListAccountsAsync(ct);
        var all = endpoints.All.Select(e => new EndpointDto(e.Name, e.Protocol, e.Port, e.ImplicitTls, e.Address)).ToList();
        return Results.Ok(new InfoResponse(
            Version,
            o.Hostname,
            o.DefaultDomain,
            metrics.StartedAt,
            (DateTimeOffset.UtcNow - metrics.StartedAt).TotalSeconds,
            all,
            stats.Total,
            stats.Unread,
            stats.TotalSize,
            accounts.Count,
            o.Storage.ToString(),
            o.Storage == StorageKind.Sqlite ? Path.GetFullPath(o.DataDir) : "(memory)",
            o.Auth.Mode.ToString(),
            o.Auth.RequireTls,
            certificates.Certificate is not null,
            new RetentionDto(o.Retention.MaxMessages, o.Retention.MaxAge?.TotalHours, o.Retention.MaxTotalSize, o.Retention.AppliesToMailboxes),
            o.Smtp.MaxMessageSize,
            !string.IsNullOrEmpty(o.Http.UiAuthFile)));
    }

    private static async Task<IResult> MetricsAsync(IMailStore store, MetricsCollector metrics, CancellationToken ct)
    {
        var stats = await store.GetCaptureStatsAsync(ct);
        var accounts = await store.ListAccountsAsync(ct);
        return Results.Ok(new MetricsResponse(metrics.Snapshot(), stats.Total, stats.Unread, stats.TotalSize, accounts.Count));
    }
}
