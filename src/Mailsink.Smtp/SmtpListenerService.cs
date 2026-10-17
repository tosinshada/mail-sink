using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Mailsink.Core.Accounts;
using Mailsink.Core.Chaos;
using Mailsink.Core.Delivery;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;

namespace Mailsink.Smtp;

/// <summary>Hosts the capture, submission and SMTPS endpoints.</summary>
public sealed class SmtpListenerService(
    IOptions<MailsinkOptions> options,
    CertificateProvider certificates,
    EndpointRegistry registry,
    SmtpServices services,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly List<TcpServer> _servers = [];
    private readonly ILogger _logger = loggerFactory.CreateLogger("Mailsink.Smtp");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var address = ListenerAddress.Parse(o.BindAddress);
        Start("smtp", SmtpEndpointKind.Capture, o.Smtp.Capture, address, implicitTls: false);
        Start("submission", SmtpEndpointKind.Submission, o.Smtp.Submission, address, implicitTls: false);
        Start("smtps", SmtpEndpointKind.Smtps, o.Smtp.Smtps, address, implicitTls: true);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _servers)
        {
            await server.StopAsync(TimeSpan.FromSeconds(5));
            registry.Remove(server.Definition.Name);
        }

        _servers.Clear();
    }

    private void Start(string name, SmtpEndpointKind kind, SmtpEndpointOptions endpoint, IPAddress address, bool implicitTls)
    {
        if (!endpoint.Enabled)
        {
            return;
        }

        var certificate = certificates.Certificate;
        if (implicitTls && certificate is null)
        {
            _logger.LogWarning("{Name} needs a TLS certificate; endpoint disabled", name);
            return;
        }

        var o = options.Value;
        var definition = new ListenerDefinition(name, "smtp", address, endpoint.Port, implicitTls, certificate);
        var server = new TcpServer(definition, (connection, ct) =>
            new SmtpSession(connection, kind, endpoint, o, services with { Logger = _logger }).RunAsync(ct), _logger);
        server.Start();
        _servers.Add(server);
        registry.Register(new EndpointInfo(name, "smtp", server.Port, implicitTls, address.ToString()));
    }
}

public static class SmtpServiceCollectionExtensions
{
    public static IServiceCollection AddMailsinkSmtp(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SmtpServices(
            sp.GetRequiredService<IMailStore>(),
            sp.GetRequiredService<LocalDeliveryAgent>(),
            sp.GetRequiredService<IAccountService>(),
            sp.GetRequiredService<IChaosEngine>(),
            sp.GetRequiredService<TranscriptStore>(),
            sp.GetRequiredService<MetricsCollector>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mailsink.Smtp")));
        services.AddHostedService<SmtpListenerService>();
        return services;
    }
}
