namespace Mailsink.Core.Options;

/// <summary>Root configuration, bound from the <c>Mailsink</c> section.</summary>
public sealed class MailsinkOptions
{
    public const string SectionName = "Mailsink";

    public string DataDir { get; set; } = "./data";
    public StorageKind Storage { get; set; } = StorageKind.Sqlite;
    public BlobStorageKind BlobStorage { get; set; } = BlobStorageKind.File;
    public string DefaultDomain { get; set; } = "test.local";
    public string Hostname { get; set; } = "mailsink";

    /// <summary>Address the SMTP/IMAP/POP3 listeners bind to. <c>0.0.0.0</c> (or <c>*</c>) for all interfaces, as in Docker.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public AuthOptions Auth { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public ImapOptions Imap { get; set; } = new();
    public Pop3Options Pop3 { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public DiagnosticsOptions Diagnostics { get; set; } = new();
}

public enum StorageKind { Sqlite, InMemory }

public enum BlobStorageKind { File, Database }

public enum AuthMode { AcceptAny, Registry }

public enum TlsMode { None, Optional, Required }

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.AcceptAny;
    public bool RequireTls { get; set; }
    public TimeSpan FailureDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public List<UserSeed> Users { get; set; } = [];
}

public sealed class UserSeed
{
    public string Address { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SmtpEndpointOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; }
    public TlsMode StartTls { get; set; } = TlsMode.Optional;
    public bool RequireAuth { get; set; }
    public bool SaveToSent { get; set; }
}

public sealed class SmtpOptions
{
    public SmtpEndpointOptions Capture { get; set; } = new() { Port = 1025 };
    public SmtpEndpointOptions Submission { get; set; } = new() { Port = 1587, RequireAuth = true, SaveToSent = true };
    public SmtpEndpointOptions Smtps { get; set; } = new() { Port = 1465, Enabled = false, RequireAuth = true, SaveToSent = true };
    public long MaxMessageSize { get; set; } = 50 * 1024 * 1024;
    public int MaxRecipients { get; set; } = 1000;
    public bool AutoCreateRecipients { get; set; } = true;
    public bool PlusAddressFolders { get; set; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class ImapOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 1143;
    public int ImapsPort { get; set; } = 1993;
    public bool ImapsEnabled { get; set; } = true;
    public bool EnableRev2 { get; set; }
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(29);
    public bool OutboxSubmission { get; set; }
    public bool PreAuth { get; set; }
    public int MaxConnections { get; set; } = 1000;
    public int MaxLineLength { get; set; } = 64 * 1024;
}

public sealed class Pop3Options
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 1110;
}

public sealed class HttpOptions
{
    public string Urls { get; set; } = "http://127.0.0.1:8025";
    public string BasePath { get; set; } = "/";
    public string? UiAuthFile { get; set; }
    public List<string> CorsOrigins { get; set; } = [];
}

public sealed class TlsOptions
{
    public string? CertificatePath { get; set; }
    public string? KeyPath { get; set; }
    public string? CertificatePassword { get; set; }
    public bool GenerateSelfSigned { get; set; } = true;
    public List<string> Hostnames { get; set; } = [];
}

public sealed class RetentionOptions
{
    public int MaxMessages { get; set; } = 500;
    public TimeSpan? MaxAge { get; set; } = TimeSpan.FromDays(7);
    public long? MaxTotalSize { get; set; }
    public bool AppliesToMailboxes { get; set; }
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class DiagnosticsOptions
{
    public int TranscriptSessions { get; set; } = 200;
    public int TranscriptLinesPerSession { get; set; } = 2000;
}
