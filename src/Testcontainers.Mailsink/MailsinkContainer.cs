using System.Net.Http.Json;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;

namespace Testcontainers.Mailsink;

/// <summary>Builds a Mailsink container: <c>new MailsinkBuilder().WithUser("bob@test.local", "pw").Build()</c>.</summary>
public sealed class MailsinkBuilder : ContainerBuilder<MailsinkBuilder, MailsinkContainer, MailsinkConfiguration>
{
    public const string DefaultImage = "ghcr.io/mailsink/mailsink:latest";
    public const ushort SmtpPort = 1025;
    public const ushort SubmissionPort = 1587;
    public const ushort ImapPort = 1143;
    public const ushort ImapsPort = 1993;
    public const ushort Pop3Port = 1110;
    public const ushort HttpPort = 8025;

    private int _userCount;

    public MailsinkBuilder()
        : this(new MailsinkConfiguration())
    {
        DockerResourceConfiguration = Init().DockerResourceConfiguration;
    }

    private MailsinkBuilder(MailsinkConfiguration configuration)
        : base(configuration)
    {
        DockerResourceConfiguration = configuration;
    }

    protected override MailsinkConfiguration DockerResourceConfiguration { get; }

    /// <summary>Seeds a user (registry mode is enabled automatically).</summary>
    public MailsinkBuilder WithUser(string address, string password)
    {
        var index = _userCount++;
        return WithEnvironment("MAILSINK__Auth__Mode", "Registry")
            .WithEnvironment($"MAILSINK__Auth__Users__{index}__Address", address)
            .WithEnvironment($"MAILSINK__Auth__Users__{index}__Password", password);
    }

    public MailsinkBuilder WithPop3() => WithEnvironment("MAILSINK__Pop3__Enabled", "true");

    public override MailsinkContainer Build()
    {
        Validate();
        return new MailsinkContainer(DockerResourceConfiguration);
    }

    protected override MailsinkBuilder Init() =>
        base.Init()
            .WithImage(DefaultImage)
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(SubmissionPort, true)
            .WithPortBinding(ImapPort, true)
            .WithPortBinding(ImapsPort, true)
            .WithPortBinding(Pop3Port, true)
            .WithPortBinding(HttpPort, true)
            .WithEnvironment("MAILSINK__Storage", "InMemory")
            .WithEnvironment("MAILSINK__Auth__FailureDelay", "00:00:00")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/api/v1/health")));

    protected override MailsinkBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new MailsinkConfiguration(resourceConfiguration));

    protected override MailsinkBuilder Clone(IContainerConfiguration resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new MailsinkConfiguration(resourceConfiguration));

    protected override MailsinkBuilder Merge(MailsinkConfiguration oldValue, MailsinkConfiguration newValue) =>
        new(new MailsinkConfiguration(oldValue, newValue)) { _userCount = _userCount };
}

public sealed class MailsinkConfiguration : ContainerConfiguration
{
    public MailsinkConfiguration()
    {
    }

    public MailsinkConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    public MailsinkConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    public MailsinkConfiguration(MailsinkConfiguration resourceConfiguration)
        : this(new MailsinkConfiguration(), resourceConfiguration)
    {
    }

    public MailsinkConfiguration(MailsinkConfiguration oldValue, MailsinkConfiguration newValue)
        : base(oldValue, newValue)
    {
    }
}

public sealed class MailsinkContainer(MailsinkConfiguration configuration) : DockerContainer(configuration)
{
    public int GetSmtpPort() => GetMappedPublicPort(MailsinkBuilder.SmtpPort);

    public int GetSubmissionPort() => GetMappedPublicPort(MailsinkBuilder.SubmissionPort);

    public int GetImapPort() => GetMappedPublicPort(MailsinkBuilder.ImapPort);

    public int GetImapsPort() => GetMappedPublicPort(MailsinkBuilder.ImapsPort);

    public int GetPop3Port() => GetMappedPublicPort(MailsinkBuilder.Pop3Port);

    public Uri GetApiBaseUri() => new($"http://{Hostname}:{GetMappedPublicPort(MailsinkBuilder.HttpPort)}/");

    /// <summary>Long-polls <c>/api/v1/wait</c> until <paramref name="count"/> messages match <paramref name="query"/>.</summary>
    public async Task<JsonElement> WaitForMessagesAsync(string? query = null, int count = 1, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = GetApiBaseUri(), Timeout = (timeout ?? TimeSpan.FromSeconds(30)) + TimeSpan.FromSeconds(5) };
        var seconds = (int)(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds;
        using var response = await http.GetAsync($"api/v1/wait?count={count}&timeout={seconds}s&query={Uri.EscapeDataString(query ?? "")}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
