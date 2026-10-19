using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Accounts;
using Mailsink.Core.Chaos;
using Mailsink.Core.Delivery;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;
using Mailsink.Core.Sms;
using Mailsink.Core.Templates;
using Mailsink.Imap;
using Mailsink.Pop3;
using Mailsink.Smtp;
using Mailsink.Storage.InMemory;
using Mailsink.Storage.Sqlite;

namespace Mailsink.Hosting;

public static class MailsinkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store, event bus, delivery, chaos, diagnostics and the SMTP/IMAP/POP3 listeners.
    /// Options bind from the <c>Mailsink</c> configuration section; <paramref name="configure"/> runs after binding.
    /// </summary>
    public static IServiceCollection AddMailsink(this IServiceCollection services, IConfiguration? configuration = null, Action<MailsinkOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<MailsinkOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(MailsinkOptions.SectionName));
        }

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IMailEventBus, MailEventBus>();
        services.AddSingleton<ParsedMessageCache>();
        services.AddSingleton<IBlobStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MailsinkOptions>>().Value;
            if (o.Storage == StorageKind.InMemory)
            {
                return new InMemoryBlobStore();
            }

            return o.BlobStorage == BlobStorageKind.Database
                ? new SqliteBlobStore(sp.GetRequiredService<SqliteDatabase>())
                : new FileBlobStore(Path.Combine(Path.GetFullPath(o.DataDir), "blobs"));
        });
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MailsinkOptions>>().Value;
            return new SqliteDatabase(Path.Combine(Path.GetFullPath(o.DataDir), "mailsink.db"));
        });
        services.AddSingleton<IMailStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MailsinkOptions>>().Value;
            var bus = sp.GetRequiredService<IMailEventBus>();
            var blobs = sp.GetRequiredService<IBlobStore>();
            var cache = sp.GetRequiredService<ParsedMessageCache>();
            return o.Storage == StorageKind.InMemory
                ? new InMemoryMailStore(blobs, bus, cache)
                : new SqliteMailStore(sp.GetRequiredService<SqliteDatabase>(), blobs, bus, cache);
        });

        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<LocalDeliveryAgent>();
        services.AddSingleton<SubmissionService>();
        services.AddSingleton<IChaosEngine, ChaosEngine>();
        services.AddSingleton<TranscriptStore>();
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton<EndpointRegistry>();
        services.AddSingleton<CertificateProvider>();
        services.AddSingleton<TemplateService>();
        services.AddSingleton<SmsService>();

        // Order matters: the store is ready before any listener accepts connections.
        services.AddHostedService<StoreInitializer>();
        services.AddMailsinkSmtp();
        services.AddMailsinkImap();
        services.AddMailsinkPop3();
        services.AddHostedService<RetentionService>();
        services.AddHostedService<MetricsEventListener>();
        return services;
    }
}

/// <summary>Migrates the database, seeds configured users and warns about exposed listeners.</summary>
internal sealed class StoreInitializer(
    IMailStore store,
    IAccountService accounts,
    IOptions<MailsinkOptions> options,
    ILogger<StoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.Storage == StorageKind.Sqlite)
        {
            Directory.CreateDirectory(Path.GetFullPath(o.DataDir));
        }

        await store.InitializeAsync(cancellationToken);
        await accounts.SeedAsync(cancellationToken);
        logger.LogInformation("Mailsink storage: {Storage} ({DataDir}), auth mode {Mode}", o.Storage,
            o.Storage == StorageKind.Sqlite ? Path.GetFullPath(o.DataDir) : "memory", o.Auth.Mode);

        if (o.BindAddress is "0.0.0.0" or "*" or "::")
        {
            logger.LogWarning("Mail listeners are bound to all interfaces ({Address})", o.BindAddress);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Enforces <c>MaxMessages</c>, <c>MaxAge</c> and <c>MaxTotalSize</c> once per interval.</summary>
internal sealed class RetentionService(IMailStore store, IOptionsMonitor<MailsinkOptions> options, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var retention = options.CurrentValue.Retention;
            try
            {
                await Task.Delay(retention.Interval > TimeSpan.Zero ? retention.Interval : TimeSpan.FromMinutes(1), stoppingToken);
                var removed = await store.ApplyRetentionAsync(
                    new RetentionPolicy(retention.MaxMessages, retention.MaxAge, retention.MaxTotalSize, retention.AppliesToMailboxes), stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Retention removed {Count} message(s)", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention pass failed");
            }
        }
    }
}

/// <summary>Feeds capture events into <see cref="MetricsCollector"/>.</summary>
internal sealed class MetricsEventListener(IMailEventBus bus, MetricsCollector metrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        try
        {
            await foreach (var e in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                if (e is MessageCapturedEvent { Message.Source: not MessageSource.ImapAppend })
                {
                    metrics.MessageReceived();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
