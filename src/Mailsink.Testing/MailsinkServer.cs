using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Mailsink.Api;
using Mailsink.Core.Accounts;
using Mailsink.Core.Chaos;
using Mailsink.Core.Delivery;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Search;
using Mailsink.Core.Storage;
using Mailsink.Hosting;

namespace Mailsink.Testing;

/// <summary>
/// A real SMTP/IMAP/POP3 mail server running inside the test process. Ports default to 0 (OS-assigned) so tests
/// can run in parallel; storage is in memory. Pass <c>http: true</c> to also host the REST API.
/// </summary>
/// <example>
/// <code>
/// await using var mail = await MailsinkServer.StartAsync(o => o.Auth.Mode = AuthMode.Registry);
/// mail.WithUser("bob@test.local", "pw");
/// // ... app sends to 127.0.0.1:mail.SmtpPort ...
/// var message = await mail.WaitForMessageAsync(m => m.Subject == "Reset your password");
/// </code>
/// </example>
public sealed class MailsinkServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly List<(string Address, string? Password)> _users = [];

    private MailsinkServer(IHost host, Uri? apiBaseUri)
    {
        _host = host;
        ApiBaseUri = apiBaseUri;
        var registry = Services.GetRequiredService<EndpointRegistry>();
        SmtpPort = registry.GetPort("smtp") ?? 0;
        SubmissionPort = registry.GetPort("submission") ?? 0;
        SmtpsPort = registry.GetPort("smtps") ?? 0;
        ImapPort = registry.GetPort("imap") ?? 0;
        ImapsPort = registry.GetPort("imaps") ?? 0;
        Pop3Port = registry.GetPort("pop3") ?? 0;
    }

    public string Host => "127.0.0.1";

    public int SmtpPort { get; }

    public int SubmissionPort { get; }

    public int SmtpsPort { get; }

    public int ImapPort { get; }

    public int ImapsPort { get; }

    public int Pop3Port { get; }

    /// <summary>Base address of the REST API (<c>http://127.0.0.1:port/</c>), or null when started without HTTP.</summary>
    public Uri? ApiBaseUri { get; }

    public IServiceProvider Services => _host.Services;

    public IMailStore Store => Services.GetRequiredService<IMailStore>();

    public IChaosEngine Chaos => Services.GetRequiredService<IChaosEngine>();

    /// <summary>Public certificate (PEM) used for STARTTLS and implicit TLS endpoints.</summary>
    public string? CertificatePem => Services.GetRequiredService<CertificateProvider>().ExportPublicPem();

    public static async Task<MailsinkServer> StartAsync(Action<MailsinkOptions>? configure = null, bool http = false, CancellationToken ct = default)
    {
        void Configure(MailsinkOptions o)
        {
            o.Storage = StorageKind.InMemory;
            o.BindAddress = "127.0.0.1";
            o.Smtp.Capture.Port = 0;
            o.Smtp.Submission.Port = 0;
            o.Smtp.Smtps.Port = 0;
            o.Imap.Port = 0;
            o.Imap.ImapsPort = 0;
            o.Pop3.Enabled = true;
            o.Pop3.Port = 0;
            o.Auth.FailureDelay = TimeSpan.Zero;
            o.Retention.MaxMessages = 0;
            o.Retention.MaxAge = null;
            o.Http.Urls = "http://127.0.0.1:0";
            configure?.Invoke(o);
        }

        IHost host;
        Uri? baseUri = null;
        if (http)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMailsink(configure: Configure);
            builder.Services.AddMailsinkApi();
            var app = builder.Build();
            app.MapMailsinkApi();
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            baseUri = new Uri(address.EndsWith('/') ? address : address + "/");
            host = app;
        }
        else
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddLogging();
            builder.Services.AddMailsink(configure: Configure);
            host = builder.Build();
            await host.StartAsync(ct);
        }

        return new MailsinkServer(host, baseUri);
    }

    /// <summary>Creates (or updates) an account with a password. Survives <see cref="ResetAsync"/>.</summary>
    public MailsinkServer WithUser(string address, string password)
    {
        WithUserAsync(address, password).GetAwaiter().GetResult();
        return this;
    }

    public async Task<MailsinkServer> WithUserAsync(string address, string? password, CancellationToken ct = default)
    {
        await Services.GetRequiredService<IAccountService>().CreateUserAsync(address, password, ct);
        _users.Add((address, password));
        return this;
    }

    /// <summary>Everything captured so far, newest first.</summary>
    public IReadOnlyList<ReceivedMessage> Messages => GetMessagesAsync().GetAwaiter().GetResult();

    public async Task<IReadOnlyList<ReceivedMessage>> GetMessagesAsync(string? query = null, CancellationToken ct = default)
    {
        var page = await Store.QueryCaptureAsync(new CaptureQuery(query, 0, 1000), ct);
        var result = new List<ReceivedMessage>(page.Items.Count);
        foreach (var item in page.Items)
        {
            result.Add(await LoadAsync(item, ct));
        }

        return result;
    }

    /// <summary>Waits until a captured message satisfies <paramref name="predicate"/>. Default timeout 10 s.</summary>
    public async Task<ReceivedMessage> WaitForMessageAsync(Func<ReceivedMessage, bool> predicate, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var all = await WaitAsync(list => list.Any(predicate), timeout, ct);
        return all.First(predicate);
    }

    /// <summary>Waits until at least <paramref name="count"/> messages have been captured. Default timeout 10 s.</summary>
    public async Task<IReadOnlyList<ReceivedMessage>> WaitForMessagesAsync(int count, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var all = await WaitAsync(list => list.Count >= count, timeout, ct);
        return [.. all.Take(count)];
    }

    /// <summary>Sends through the in-process submission path (capture, local delivery, Sent copy).</summary>
    public async Task<ReceivedMessage> SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        var submission = Services.GetRequiredService<SubmissionService>();
        var result = await submission.SubmitAsync(message, null, new SubmissionOptions(MessageSource.Api), ct);
        return await LoadAsync(result.Message, ct);
    }

    /// <summary>Purges messages, mailboxes and transcripts, resets chaos rules, and re-creates users added with <see cref="WithUser"/>.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await Store.PurgeAsync(ct);
        Services.GetRequiredService<TranscriptStore>().Clear();
        Chaos.Update(new ChaosSettings(true, []));
        var accounts = Services.GetRequiredService<IAccountService>();
        await accounts.SeedAsync(ct);
        foreach (var (address, password) in _users)
        {
            await accounts.CreateUserAsync(address, password, ct);
        }
    }

    public void AddChaosRule(ChaosRule rule) => Chaos.Update(Chaos.Settings with { Rules = [.. Chaos.Settings.Rules, rule] });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
    }

    private async Task<IReadOnlyList<ReceivedMessage>> WaitAsync(Func<IReadOnlyList<ReceivedMessage>, bool> done, TimeSpan? timeout, CancellationToken ct)
    {
        using var subscription = Services.GetRequiredService<IMailEventBus>().Subscribe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var messages = await GetMessagesAsync(null, cts.Token);
                if (done(messages))
                {
                    return messages;
                }

                await subscription.Reader.WaitToReadAsync(cts.Token);
                while (subscription.Reader.TryRead(out _))
                {
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var count = (await Store.GetCaptureStatsAsync(CancellationToken.None)).Total;
            throw new TimeoutException($"No matching message within {timeout ?? TimeSpan.FromSeconds(10)} ({count} message(s) captured).");
        }
    }

    private async Task<ReceivedMessage> LoadAsync(CapturedMessage captured, CancellationToken ct) =>
        new(captured, await Store.LoadParsedAsync(captured.Id, ct));
}
