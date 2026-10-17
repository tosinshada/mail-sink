using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Mailsink.Core.Networking;

public sealed record ListenerDefinition(
    string Name,
    string Protocol,
    IPAddress Address,
    int Port,
    bool ImplicitTls,
    X509Certificate2? Certificate,
    int MaxConnections = 1000);

/// <summary>
/// Accept loop with connection limits and graceful shutdown. Protocol code is supplied as a handler delegate
/// (composition rather than a base class).
/// </summary>
public sealed class TcpServer : IAsyncDisposable
{
    private readonly ListenerDefinition _definition;
    private readonly Func<ProtocolConnection, CancellationToken, Task> _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Socket? _listener;
    private Task? _acceptLoop;

    public TcpServer(ListenerDefinition definition, Func<ProtocolConnection, CancellationToken, Task> handler, ILogger logger)
    {
        _definition = definition;
        _handler = handler;
        _logger = logger;
    }

    public int Port { get; private set; }

    public ListenerDefinition Definition => _definition;

    /// <summary>Binds synchronously (so the port is known on return) and starts accepting in the background.</summary>
    public void Start()
    {
        var socket = new Socket(_definition.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (_definition.Address.Equals(IPAddress.IPv6Any))
        {
            socket.DualMode = true;
        }

        socket.Bind(new IPEndPoint(_definition.Address, _definition.Port));
        socket.Listen(512);
        _listener = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _logger.LogInformation("{Protocol} listening on {Address}:{Port}{Tls}", _definition.Name.ToUpperInvariant(),
            _definition.Address, Port, _definition.ImplicitTls ? " (implicit TLS)" : "");
    }

    public async Task StopAsync(TimeSpan grace)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync();
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        var pending = _connections.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(grace)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogDebug(ex, "Accept failed on {Name}", _definition.Name);
                continue;
            }

            if (_connections.Count >= _definition.MaxConnections)
            {
                _logger.LogWarning("{Name}: connection limit {Max} reached, refusing {Remote}", _definition.Name, _definition.MaxConnections, client.RemoteEndPoint);
                client.Dispose();
                continue;
            }

            client.NoDelay = true;
            var task = Task.Run(() => RunConnectionAsync(client));
            _connections.TryAdd(task, 0);
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task RunConnectionAsync(Socket client)
    {
        var remote = client.RemoteEndPoint;
        Stream stream = new NetworkStream(client, ownsSocket: true);
        try
        {
            if (_definition.ImplicitTls && _definition.Certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _definition.Certificate,
                }, handshakeTimeout.Token);
                stream = ssl;
            }

            await using var connection = new ProtocolConnection(stream, remote, _definition.ImplicitTls, _definition.Certificate, client);
            await _handler(connection, _stopping.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
        {
            _logger.LogDebug(ex, "{Name} connection from {Remote} ended", _definition.Name, remote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name} connection from {Remote} failed", _definition.Name, remote);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }
}
