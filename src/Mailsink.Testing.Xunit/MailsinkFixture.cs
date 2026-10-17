using Mailsink.Core.Options;
using Xunit;

namespace Mailsink.Testing.Xunit;

/// <summary>
/// xUnit v3 fixture: one in-process Mailsink server per test class (<c>IClassFixture&lt;MailsinkFixture&gt;</c>)
/// or per collection (<c>[CollectionDefinition] ... ICollectionFixture&lt;MailsinkFixture&gt;</c>).
/// Derive and override <see cref="Configure"/> to change options, or <see cref="EnableHttp"/> to host the REST API.
/// </summary>
public class MailsinkFixture : IAsyncLifetime
{
    private MailsinkServer? _server;

    public MailsinkServer Server => _server ?? throw new InvalidOperationException("The fixture has not been initialized.");

    protected virtual bool EnableHttp => false;

    protected virtual void Configure(MailsinkOptions options)
    {
    }

    public virtual async ValueTask InitializeAsync() =>
        _server = await MailsinkServer.StartAsync(Configure, EnableHttp);

    public virtual async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
