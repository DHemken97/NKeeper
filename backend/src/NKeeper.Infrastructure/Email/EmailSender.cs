using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Infrastructure.Email;

public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>An SMTP server to send through, with its password decrypted.</summary>
public sealed record SmtpServer(string Host, int Port, SmtpSecurity Security, string? UserName, string? Password, string FromAddress, string? FromName);

/// <summary>Nothing can be sent: email is off, or the server refused or couldn't be reached.</summary>
public sealed class EmailException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Delivers a message to an SMTP server. Tests replace it.</summary>
public interface IEmailTransport
{
    Task SendAsync(SmtpServer server, EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Sends email with the settings administrators saved.</summary>
public sealed class EmailSender(NKeeperDbContext db, CredentialProtector protector, IEmailTransport transport, ILogger<EmailSender> logger)
{
    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        db.Set<EmailSettings>().AnyAsync(s => s.Id == EmailSettings.SingletonId && s.Enabled, cancellationToken);

    /// <exception cref="EmailException">Email is off, or sending failed (the reason is logged).</exception>
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = await db.Set<EmailSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == EmailSettings.SingletonId, cancellationToken);
        if (settings is not { Enabled: true })
        {
            throw new EmailException("Email isn't set up on this server.");
        }

        await SendAsync(ToServer(settings, settings.ProtectedPassword is null ? null : protector.Unprotect(settings.ProtectedPassword)), message, cancellationToken);
    }

    /// <summary>With settings not saved yet, e.g. to test them.</summary>
    public async Task SendAsync(SmtpServer server, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await transport.SendAsync(server, message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EmailException)
        {
            logger.LogWarning(ex, "Couldn't send email through {Host}:{Port}.", server.Host, server.Port);
            throw new EmailException($"The email couldn't be sent: {ex.Message}", ex);
        }
    }

    public static SmtpServer ToServer(EmailSettings settings, string? password) =>
        new(settings.Host, settings.Port, settings.Security, settings.UserName, password, settings.FromAddress, settings.FromName);
}

internal sealed class SmtpEmailTransport : IEmailTransport
{
    public async Task SendAsync(SmtpServer server, EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(server.FromName ?? "NKeeper", server.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient { Timeout = 30_000 };
        var options = server.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(server.Host, server.Port, options, cancellationToken);
        if (!string.IsNullOrEmpty(server.UserName))
        {
            await client.AuthenticateAsync(server.UserName, server.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
