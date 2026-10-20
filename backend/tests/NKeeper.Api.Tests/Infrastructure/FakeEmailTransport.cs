using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NKeeper.Infrastructure.Email;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>Keeps the email NKeeper sends instead of delivering it; can be told to fail like a down server.</summary>
public sealed partial class FakeEmailTransport : IEmailTransport
{
    public ConcurrentQueue<(SmtpServer Server, EmailMessage Message)> Sent { get; } = new();

    public bool Fail { get; set; }

    public Task SendAsync(SmtpServer server, EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("Connection refused");
        }

        Sent.Enqueue((server, message));
        return Task.CompletedTask;
    }

    /// <summary>The sign-in code in the last email to <paramref name="to"/>.</summary>
    public string LastCode(string to)
    {
        var message = Sent.Last(m => m.Message.To == to).Message;
        return CodePattern().Match(message.Subject).Value;
    }

    [GeneratedRegex(@"\d{9}")]
    private static partial Regex CodePattern();
}
