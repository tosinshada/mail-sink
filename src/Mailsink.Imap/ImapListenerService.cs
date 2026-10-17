using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Imap.Commands;
using Mailsink.Imap.Fetch;
using Mailsink.Imap.Session;

namespace Mailsink.Imap;

/// <summary>Hosts the IMAP (STARTTLS) and IMAPS (implicit TLS) endpoints.</summary>
public sealed class ImapListenerService(
    IOptions<MailsinkOptions> options,
    CertificateProvider certificates,
    EndpointRegistry registry,
    ImapServices services,
    ILoggerFactory loggerFactory) : IHostedService
{
    private static readonly FrozenDictionary<string, IImapCommandHandler> Handlers = BuildHandlers();

    private readonly List<TcpServer> _servers = [];
    private readonly ILogger _logger = loggerFactory.CreateLogger("Mailsink.Imap");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Imap.Enabled)
        {
            return Task.CompletedTask;
        }

        var address = ListenerAddress.Parse(o.BindAddress);
        Start("imap", address, o.Imap.Port, implicitTls: false);
        if (o.Imap.ImapsEnabled)
        {
            Start("imaps", address, o.Imap.ImapsPort, implicitTls: true);
        }

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

    private void Start(string name, System.Net.IPAddress address, int port, bool implicitTls)
    {
        var certificate = certificates.Certificate;
        if (implicitTls && certificate is null)
        {
            _logger.LogWarning("{Name} needs a TLS certificate; endpoint disabled", name);
            return;
        }

        var o = options.Value;
        var definition = new ListenerDefinition(name, "imap", address, port, implicitTls, certificate, o.Imap.MaxConnections);
        var server = new TcpServer(definition, async (connection, ct) =>
        {
            await using var session = new ImapSession(connection, services, o, Handlers, _logger);
            await session.RunAsync(ct);
        }, _logger);
        server.Start();
        _servers.Add(server);
        registry.Register(new EndpointInfo(name, "imap", server.Port, implicitTls, address.ToString()));
    }

    private static FrozenDictionary<string, IImapCommandHandler> BuildHandlers()
    {
        IImapCommandHandler[] handlers =
        [
            new CapabilityCommand(),
            new NoopCommand("NOOP"),
            new NoopCommand("CHECK"),
            new LogoutCommand(),
            new IdCommand(),
            new EnableCommand(),
            new StartTlsCommand(),
            new LoginCommand(),
            new AuthenticateCommand(),
            new SelectCommand(readOnly: false),
            new SelectCommand(readOnly: true),
            new CreateCommand(),
            new DeleteCommand(),
            new RenameCommand(),
            new SubscribeCommand(subscribe: true),
            new SubscribeCommand(subscribe: false),
            new ListCommand(lsub: false),
            new ListCommand(lsub: true),
            new NamespaceCommand(),
            new StatusCommand(),
            new AppendCommand(),
            new IdleCommand(),
            new UnselectCommand("CLOSE"),
            new UnselectCommand("UNSELECT"),
            new FetchCommand(),
            new StoreCommand(),
            new SearchCommand(),
            new CopyCommand(move: false),
            new CopyCommand(move: true),
            new ExpungeCommand(),
        ];
        return handlers.ToFrozenDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
    }
}

public static class ImapServiceCollectionExtensions
{
    public static IServiceCollection AddMailsinkImap(this IServiceCollection services)
    {
        services.AddSingleton<ImapMessageCache>();
        services.AddSingleton<ImapServices>();
        services.AddHostedService<ImapListenerService>();
        return services;
    }
}
