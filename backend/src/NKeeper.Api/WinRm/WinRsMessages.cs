using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NKeeper.Api.WinRm;

/// <summary>A WS-Management fault: the server refused or couldn't process a request.</summary>
public sealed class WsManFaultException(string message, string? code) : Exception(message)
{
    /// <summary>The WS-Management error code (decimal, as in the fault's detail), when given.</summary>
    public string? Code { get; } = code;

    /// <summary>A Receive that waited the whole operation timeout without output; poll again.</summary>
    public bool IsOperationTimeout => Code == "2150858793";
}

/// <summary>The output a Receive returned, and whether the command has finished.</summary>
public sealed record WinRsOutput(byte[] Stdout, byte[] Stderr, bool Done, int? ExitCode);

/// <summary>
/// The Windows Remote Shell (WinRS) messages NKeeper sends over WS-Management ([MS-WSMV]): create
/// a shell, run a command in it, send to its stdin, receive its output, signal it and delete it.
/// It is the protocol winrs.exe speaks; PowerShell Remoting (PSRP) is not needed for a console.
/// </summary>
public static class WinRsMessages
{
    public const string ShellResource = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";
    public const string TerminateSignal = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate";
    public const string CtrlCSignal = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/ctrl_c";

    private const string CommandDone = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done";
    private const string AnonymousRole = "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";
    private const int MaxEnvelopeSize = 512_000;

    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace A = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace W = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace P = "http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private static readonly XNamespace Xml = XNamespace.Xml;

    /// <summary>A shell with stdin, stdout and stderr, in UTF-8 (code page 65001), without loading the user profile.</summary>
    public static string CreateShell(Uri endpoint, TimeSpan timeout) =>
        Envelope(
            endpoint,
            "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create",
            shellId: null,
            timeout,
            options: [("WINRS_NOPROFILE", "TRUE"), ("WINRS_CODEPAGE", "65001")],
            new XElement(Rsp + "Shell", new XElement(Rsp + "InputStreams", "stdin"), new XElement(Rsp + "OutputStreams", "stdout stderr")));

    /// <summary>
    /// Runs a program in the shell. With console-mode stdin, programs such as powershell.exe and
    /// cmd.exe behave as interactive consoles (prompts, line input).
    /// </summary>
    public static string Command(Uri endpoint, string shellId, string program, string? arguments, TimeSpan timeout) =>
        Envelope(
            endpoint,
            "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command",
            shellId,
            timeout,
            options: [("WINRS_CONSOLEMODE_STDIN", "TRUE"), ("WINRS_SKIP_CMD_SHELL", "FALSE")],
            new XElement(
                Rsp + "CommandLine",
                new XElement(Rsp + "Command", program),
                arguments is null ? null : new XElement(Rsp + "Arguments", arguments)));

    /// <summary>Writes to the command's stdin (base64 in the message).</summary>
    public static string Send(Uri endpoint, string shellId, string commandId, ReadOnlySpan<byte> input, bool end, TimeSpan timeout) =>
        Envelope(
            endpoint,
            "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send",
            shellId,
            timeout,
            options: [],
            new XElement(
                Rsp + "Send",
                new XElement(
                    Rsp + "Stream",
                    new XAttribute("Name", "stdin"),
                    new XAttribute("CommandId", commandId),
                    end ? new XAttribute("End", "true") : null,
                    Convert.ToBase64String(input))));

    /// <summary>Waits (up to <paramref name="timeout"/>) for output from the command's stdout and stderr.</summary>
    public static string Receive(Uri endpoint, string shellId, string commandId, TimeSpan timeout) =>
        Envelope(
            endpoint,
            "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive",
            shellId,
            timeout,
            options: [],
            new XElement(Rsp + "Receive", new XElement(Rsp + "DesiredStream", new XAttribute("CommandId", commandId), "stdout stderr")));

    public static string Signal(Uri endpoint, string shellId, string commandId, string signal, TimeSpan timeout) =>
        Envelope(
            endpoint,
            "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal",
            shellId,
            timeout,
            options: [],
            new XElement(Rsp + "Signal", new XAttribute("CommandId", commandId), new XElement(Rsp + "Code", signal)));

    public static string DeleteShell(Uri endpoint, string shellId, TimeSpan timeout) =>
        Envelope(endpoint, "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", shellId, timeout, options: [], body: null);

    /// <summary>The new shell's id, from its body (rsp:ShellId) or the ResourceCreated selector.</summary>
    public static string ReadShellId(string response)
    {
        var document = Parse(response);
        var id = document.Descendants(Rsp + "ShellId").FirstOrDefault()?.Value
            ?? document.Descendants(W + "Selector").FirstOrDefault(s => (string?)s.Attribute("Name") == "ShellId")?.Value;
        return string.IsNullOrWhiteSpace(id) ? throw new WsManFaultException("The server didn't return a shell id.", null) : id.Trim();
    }

    public static string ReadCommandId(string response)
    {
        var id = Parse(response).Descendants(Rsp + "CommandId").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(id) ? throw new WsManFaultException("The server didn't return a command id.", null) : id.Trim();
    }

    public static WinRsOutput ReadOutput(string response)
    {
        var document = Parse(response);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        foreach (var stream in document.Descendants(Rsp + "Stream"))
        {
            if (stream.Value.Length == 0)
            {
                continue;
            }

            var bytes = Convert.FromBase64String(stream.Value.Trim());
            ((string?)stream.Attribute("Name") == "stderr" ? stderr : stdout).Write(bytes);
        }

        var state = document.Descendants(Rsp + "CommandState").FirstOrDefault();
        var done = (string?)state?.Attribute("State") == CommandDone;
        var exitCode = int.TryParse(state?.Element(Rsp + "ExitCode")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : (int?)null;
        return new WinRsOutput(stdout.ToArray(), stderr.ToArray(), done, exitCode);
    }

    /// <summary>Throws <see cref="WsManFaultException"/> when the response is a SOAP fault.</summary>
    public static void ThrowIfFault(string response) => Parse(response);

    /// <summary>Parses a reply, throwing <see cref="WsManFaultException"/> for a SOAP fault.</summary>
    private static XDocument Parse(string response)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(response), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new WsManFaultException($"The server's reply isn't valid WS-Management XML ({ex.Message}).", null);
        }

        if (document.Descendants(S + "Fault").FirstOrDefault() is { } fault)
        {
            // [MS-WSMV] puts the readable message and code in the WSManFault detail; the SOAP reason is generic.
            var detail = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "WSManFault");
            var message = detail?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value.Trim()
                ?? fault.Descendants(S + "Text").FirstOrDefault()?.Value.Trim()
                ?? "The server returned a fault.";
            throw new WsManFaultException(message, (string?)detail?.Attribute("Code"));
        }

        return document;
    }

    private static string Envelope(
        Uri endpoint, string action, string? shellId, TimeSpan timeout, (string Name, string Value)[] options, XElement? body)
    {
        var mustUnderstand = new XAttribute(S + "mustUnderstand", "true");
        var header = new XElement(
            S + "Header",
            new XElement(A + "To", endpoint.ToString()),
            new XElement(A + "ReplyTo", new XElement(A + "Address", mustUnderstand, AnonymousRole)),
            new XElement(W + "MaxEnvelopeSize", new XAttribute(S + "mustUnderstand", "true"), MaxEnvelopeSize),
            new XElement(A + "MessageID", $"uuid:{Guid.NewGuid():D}".ToUpperInvariant()),
            new XElement(W + "Locale", new XAttribute(Xml + "lang", "en-US"), new XAttribute(S + "mustUnderstand", "false")),
            new XElement(P + "DataLocale", new XAttribute(Xml + "lang", "en-US"), new XAttribute(S + "mustUnderstand", "false")),
            new XElement(W + "OperationTimeout", XmlConvert.ToString(timeout)),
            new XElement(W + "ResourceURI", new XAttribute(S + "mustUnderstand", "true"), ShellResource),
            new XElement(A + "Action", new XAttribute(S + "mustUnderstand", "true"), action),
            shellId is null ? null : new XElement(W + "SelectorSet", new XElement(W + "Selector", new XAttribute("Name", "ShellId"), shellId)),
            options.Length == 0
                ? null
                : new XElement(W + "OptionSet", options.Select(o => new XElement(W + "Option", new XAttribute("Name", o.Name), o.Value))));

        var envelope = new XElement(
            S + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", S),
            new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XAttribute(XNamespace.Xmlns + "p", P),
            new XAttribute(XNamespace.Xmlns + "rsp", Rsp),
            header,
            new XElement(S + "Body", body));

        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = Encoding.UTF8 }))
        {
            envelope.WriteTo(writer);
        }

        return builder.ToString();
    }
}
