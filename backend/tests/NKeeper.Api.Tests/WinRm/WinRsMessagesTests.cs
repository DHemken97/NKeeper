using System.Text;
using System.Xml.Linq;
using NKeeper.Api.WinRm;

namespace NKeeper.Api.Tests.WinRm;

/// <summary>The WinRS (WS-Management shell) messages NKeeper sends, and the replies it reads.</summary>
public sealed class WinRsMessagesTests
{
    private static readonly Uri Endpoint = new("http://win01.corp.example:5985/wsman");
    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace A = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace W = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";

    [Fact]
    public void CreateShell_AsksForAUtf8ShellWithStdio()
    {
        var message = XDocument.Parse(WinRsMessages.CreateShell(Endpoint, TimeSpan.FromSeconds(20)));

        Assert.Equal("http://schemas.xmlsoap.org/ws/2004/09/transfer/Create", message.Descendants(A + "Action").Single().Value);
        Assert.Equal(Endpoint.ToString(), message.Descendants(A + "To").Single().Value);
        Assert.Equal("PT20S", message.Descendants(W + "OperationTimeout").Single().Value);
        Assert.Equal(WinRsMessages.ShellResource, message.Descendants(W + "ResourceURI").Single().Value);
        Assert.Equal("65001", Option(message, "WINRS_CODEPAGE"));
        Assert.Equal(("stdin", "stdout stderr"), (message.Descendants(Rsp + "InputStreams").Single().Value, message.Descendants(Rsp + "OutputStreams").Single().Value));
        Assert.Empty(message.Descendants(W + "SelectorSet"));
    }

    [Fact]
    public void Command_RunsAProgramWithConsoleModeStdin_InTheShell()
    {
        var message = XDocument.Parse(WinRsMessages.Command(Endpoint, "SHELL-1", "powershell.exe", "-NoLogo", TimeSpan.FromSeconds(20)));

        Assert.Equal("SHELL-1", message.Descendants(W + "Selector").Single(s => (string?)s.Attribute("Name") == "ShellId").Value);
        Assert.Equal("TRUE", Option(message, "WINRS_CONSOLEMODE_STDIN"));
        Assert.Equal(("powershell.exe", "-NoLogo"), (message.Descendants(Rsp + "Command").Single().Value, message.Descendants(Rsp + "Arguments").Single().Value));
    }

    [Fact]
    public void SendReceiveSignalAndDelete_TargetTheCommand()
    {
        var send = XDocument.Parse(WinRsMessages.Send(Endpoint, "SHELL-1", "CMD-1", Encoding.UTF8.GetBytes("Get-Date\r\n"), end: false, TimeSpan.FromSeconds(20)));
        var stream = send.Descendants(Rsp + "Stream").Single();
        Assert.Equal(("stdin", "CMD-1", "Get-Date\r\n"), ((string?)stream.Attribute("Name"), (string?)stream.Attribute("CommandId"), Encoding.UTF8.GetString(Convert.FromBase64String(stream.Value))));
        Assert.Null(stream.Attribute("End"));

        var receive = XDocument.Parse(WinRsMessages.Receive(Endpoint, "SHELL-1", "CMD-1", TimeSpan.FromSeconds(5)));
        Assert.Equal("stdout stderr", receive.Descendants(Rsp + "DesiredStream").Single().Value);
        Assert.Equal("PT5S", receive.Descendants(W + "OperationTimeout").Single().Value);

        var signal = XDocument.Parse(WinRsMessages.Signal(Endpoint, "SHELL-1", "CMD-1", WinRsMessages.CtrlCSignal, TimeSpan.FromSeconds(20)));
        Assert.Equal(WinRsMessages.CtrlCSignal, signal.Descendants(Rsp + "Code").Single().Value);

        var delete = XDocument.Parse(WinRsMessages.DeleteShell(Endpoint, "SHELL-1", TimeSpan.FromSeconds(20)));
        Assert.Equal("http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", delete.Descendants(A + "Action").Single().Value);
        Assert.Empty(delete.Descendants(S + "Body").Single().Elements());
    }

    [Fact]
    public void UserText_IsEscaped()
    {
        var message = WinRsMessages.Command(Endpoint, "SHELL-1", "cmd.exe", "/c echo <b>&\"</b>", TimeSpan.FromSeconds(20));

        Assert.Equal("/c echo <b>&\"</b>", XDocument.Parse(message).Descendants(Rsp + "Arguments").Single().Value);
    }

    [Fact]
    public void Replies_AreRead()
    {
        // Real WinRM puts the id in the body and in the ResourceCreated selector; either works.
        Assert.Equal("1E2D", WinRsMessages.ReadShellId(Reply("<rsp:Shell><rsp:ShellId>1E2D</rsp:ShellId></rsp:Shell>")));
        Assert.Equal("1E2D", WinRsMessages.ReadShellId(Reply(
            "<x:ResourceCreated xmlns:x=\"http://schemas.xmlsoap.org/ws/2004/09/transfer\"><a:ReferenceParameters><w:SelectorSet><w:Selector Name=\"ShellId\">1E2D</w:Selector></w:SelectorSet></a:ReferenceParameters></x:ResourceCreated>")));
        Assert.Equal("C0FF", WinRsMessages.ReadCommandId(Reply("<rsp:CommandResponse><rsp:CommandId>C0FF</rsp:CommandId></rsp:CommandResponse>")));

        var output = WinRsMessages.ReadOutput(Reply(
            "<rsp:ReceiveResponse>"
            + $"<rsp:Stream Name=\"stdout\" CommandId=\"C0FF\">{B64("PS C:\\> ")}</rsp:Stream>"
            + $"<rsp:Stream Name=\"stderr\" CommandId=\"C0FF\">{B64("oops")}</rsp:Stream>"
            + $"<rsp:Stream Name=\"stdout\" CommandId=\"C0FF\">{B64("done")}</rsp:Stream>"
            + "<rsp:Stream Name=\"stdout\" CommandId=\"C0FF\" End=\"true\"></rsp:Stream>"
            + "<rsp:CommandState CommandId=\"C0FF\" State=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done\"><rsp:ExitCode>3</rsp:ExitCode></rsp:CommandState>"
            + "</rsp:ReceiveResponse>"));
        Assert.Equal(("PS C:\\> done", "oops", true, (int?)3), (Encoding.UTF8.GetString(output.Stdout), Encoding.UTF8.GetString(output.Stderr), output.Done, output.ExitCode));

        var running = WinRsMessages.ReadOutput(Reply(
            "<rsp:ReceiveResponse><rsp:CommandState CommandId=\"C0FF\" State=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Running\"/></rsp:ReceiveResponse>"));
        Assert.False(running.Done);
    }

    [Fact]
    public void Faults_CarryTheWsManMessageAndCode()
    {
        const string timedOut = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><s:Fault>
              <s:Code><s:Value>s:Receiver</s:Value></s:Code>
              <s:Reason><s:Text xml:lang="en-US">The WS-Management service cannot complete the operation within the time specified in OperationTimeout.</s:Text></s:Reason>
              <s:Detail><f:WSManFault xmlns:f="http://schemas.microsoft.com/wbem/wsman/1/wsmanfault" Code="2150858793" Machine="win01"><f:Message>The operation timed out.</f:Message></f:WSManFault></s:Detail>
            </s:Fault></s:Body></s:Envelope>
            """;

        var fault = Assert.Throws<WsManFaultException>(() => WinRsMessages.ReadOutput(timedOut));
        Assert.Equal(("The operation timed out.", "2150858793", true), (fault.Message, fault.Code, fault.IsOperationTimeout));
        Assert.Throws<WsManFaultException>(() => WinRsMessages.ReadShellId("<not xml"));
    }

    private static string? Option(XDocument message, string name) =>
        message.Descendants(W + "Option").SingleOrDefault(o => (string?)o.Attribute("Name") == name)?.Value;

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Reply(string body) =>
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" "
        + "xmlns:w=\"http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd\" xmlns:rsp=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell\">"
        + $"<s:Header/><s:Body>{body}</s:Body></s:Envelope>";
}
