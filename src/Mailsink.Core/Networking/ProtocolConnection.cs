using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Mailsink.Core.Networking;

public sealed class LineTooLongException() : IOException("Line too long");

/// <summary>
/// A client connection shared by the SMTP, IMAP and POP3 servers: CRLF line framing and byte literals over a
/// <see cref="PipeReader"/>, buffered writes, and in-place TLS upgrade for STARTTLS/STLS.
/// </summary>
public sealed class ProtocolConnection : IAsyncDisposable
{
    private readonly Socket? _socket;
    private readonly ArrayBufferWriter<byte> _output = new(4096);
    private Stream _stream;
    private PipeReader _reader;

    public ProtocolConnection(Stream stream, EndPoint? remote, bool isSecure, X509Certificate2? certificate, Socket? socket = null)
    {
        _stream = stream;
        _socket = socket;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        RemoteEndPoint = remote;
        IsSecure = isSecure;
        Certificate = certificate;
    }

    public EndPoint? RemoteEndPoint { get; }

    public string? ClientIp => RemoteEndPoint is IPEndPoint ip ? ip.Address.ToString() : RemoteEndPoint?.ToString();

    public bool IsSecure { get; private set; }

    public X509Certificate2? Certificate { get; }

    public bool CanStartTls => !IsSecure && Certificate is not null;

    /// <summary>Reads one line without its CR/LF. Returns null at end of stream.</summary>
    public async ValueTask<byte[]?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var lf = buffer.PositionOf((byte)'\n');
            if (lf is { } position)
            {
                var line = buffer.Slice(0, position).ToArray();
                _reader.AdvanceTo(buffer.GetPosition(1, position));
                return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
            }

            if (buffer.Length > maxLength)
            {
                _reader.AdvanceTo(buffer.End);
                throw new LineTooLongException();
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                _reader.AdvanceTo(buffer.End);
                return null;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (an IMAP literal). Returns null at end of stream.</summary>
    public async ValueTask<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        if (count == 0)
        {
            return [];
        }

        while (true)
        {
            var result = await _reader.ReadAsync(ct);
            var buffer = result.Buffer;
            if (buffer.Length >= count)
            {
                var data = buffer.Slice(0, count).ToArray();
                _reader.AdvanceTo(buffer.GetPosition(count));
                return data;
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                _reader.AdvanceTo(buffer.End);
                return null;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public void Write(ReadOnlySpan<byte> data) => _output.Write(data);

    public void Write(string ascii)
    {
        var span = _output.GetSpan(System.Text.Encoding.UTF8.GetMaxByteCount(ascii.Length));
        var written = System.Text.Encoding.UTF8.GetBytes(ascii, span);
        _output.Advance(written);
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        if (_output.WrittenCount == 0)
        {
            return;
        }

        await _stream.WriteAsync(_output.WrittenMemory, ct);
        _output.ResetWrittenCount();
        await _stream.FlushAsync(ct);
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken ct)
    {
        Write(line);
        Write("\r\n"u8);
        await FlushAsync(ct);
    }

    /// <summary>
    /// Upgrades to TLS in place. Any plaintext the client pipelined after the STARTTLS command is discarded
    /// (STARTTLS command injection defence).
    /// </summary>
    public async Task UpgradeToTlsAsync(CancellationToken ct)
    {
        if (Certificate is null)
        {
            throw new InvalidOperationException("No TLS certificate configured");
        }

        await FlushAsync(ct);
        await _reader.CompleteAsync();
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = Certificate,
            ClientCertificateRequired = false,
        }, ct);
        _stream = ssl;
        _reader = PipeReader.Create(ssl, new StreamPipeReaderOptions(leaveOpen: true));
        IsSecure = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Peer went away.
        }

        await _reader.CompleteAsync();
        await _stream.DisposeAsync();
        _socket?.Dispose();
    }
}
