using Bunkum.CustomHttpListener.Request;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BunkumSampleApplication.Listeners;

public class SipListenerContext : ListenerContext
{
    private readonly Socket _socket;

    private bool _socketClosed;
    private bool SocketClosed => this._socketClosed || !this._socket.Connected;

    public SipListenerContext(Socket socket)
    {
        this._socket = socket;
    }

    protected override bool CanSendData => !this.SocketClosed;

    protected override void CloseConnection()
    {
        if (this.SocketClosed) return;

        this._socketClosed = true;
        try
        {
            this._socket.Shutdown(SocketShutdown.Both);
            this._socket.Disconnect(false);
            this._socket.Close();
            this._socket.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    public override async Task SendResponse(HttpStatusCode code, ArraySegment<byte>? data = null)
    {
        //if (!this.CanSendData) return;

        List<string> response = new() { $"SIP/2.0 {(int)code} {code.ToString()}" }; // TODO: spaced code names ("Not Found" instead of "NotFound")
        foreach ((string? key, string? value) in this.ResponseHeaders)
        {
            Debug.Assert(key != null);
            Debug.Assert(value != null);

            response.Add($"{key}: {value}");
        }
        response.Add("\r\n");

        //await this.SendBufferSafe(string.Join("\r\n", response));
        await _socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.0.132"), 5060));
        byte[] datagram = Encoding.UTF8.GetBytes(string.Join("\r\n", response));
        await this._socket.SendAsync(datagram);
        //if (data.HasValue) await this._socket.SendAsync(datagram);

        this.CloseConnection();
    }

    private Task SendBufferSafe(string str) => this.SendBufferSafe(Encoding.UTF8.GetBytes(str));
    private async Task SendBufferSafe(ArraySegment<byte> buffer)
    {
        if (!this.CanSendData) return;

        try
        {
            await this.SendBuffer(buffer);
        }
        catch(Exception e)
        {
            // ignored, log warning in the future?
        }
    }

    protected override async Task SendBuffer(ArraySegment<byte> buffer)
    {
        await this._socket.SendAsync(buffer);
    }
}