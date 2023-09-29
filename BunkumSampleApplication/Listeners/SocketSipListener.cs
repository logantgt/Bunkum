using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Web;
using Bunkum.CustomHttpListener;
using Bunkum.CustomHttpListener.Parsing;
using Bunkum.CustomHttpListener.Request;
using BunkumSampleApplication.Listeners.Extensions;

namespace BunkumSampleApplication.Listeners
{
    public class SocketSipListener : BunkumHttpListener
    {
        private UdpClient? _client;
        private readonly Uri _listenEndpoint;
        private readonly bool _useForwardedIp;

        public SocketSipListener(Uri listenEndpoint, bool useForwardedIp, bool logToConsole) : base(logToConsole)
        {
            this._listenEndpoint = listenEndpoint;
            this._useForwardedIp = useForwardedIp;
            this.Logger.LogInfo(HttpLogContext.Startup, "SIP server is listening at URL " + _listenEndpoint);
        }

        public override void StartListening()
        {
            if (this._client != null) throw new InvalidOperationException("Cannot start listening when we are already doing so");

            IPAddress host;

            IPEndPoint listenEndpoint = new(IPAddress.Parse(this._listenEndpoint.Host), this._listenEndpoint.Port);

            this._client = new UdpClient();

            this._client.Client.Bind(listenEndpoint);

            this.Logger.LogInfo(HttpLogContext.Startup, "Listening...");
        }

        protected override async Task<ListenerContext?> WaitForConnectionAsyncInternal(CancellationToken? globalCt = null)
        {
            if (this._client == null)
                throw new InvalidOperationException("Cannot wait for a connection when we are not listening");

            UdpReceiveResult rec = await this._client.ReceiveAsync();

            //this._client. .Connect(rec.RemoteEndPoint);

            Stream stream = new MemoryStream(rec.Buffer);

            try
            {
                return this.ReadRequestIntoContext(_client.Client, stream, rec);
            }
            catch (NotSupportedException e)
            {
                this.Logger.LogWarning(HttpLogContext.Request, $"Failed to handle request due to invalid HTTP version {e.Message}");
                return null;
            }
            catch (Exception e)
            {
                this.Logger.LogWarning(HttpLogContext.Request, $"Failed to read request: {e}");
                await new SipListenerContext(_client.Client).SendResponse(HttpStatusCode.BadRequest);
                return null;
            }
        }

        private ListenerContext ReadRequestIntoContext(Socket client, Stream stream, UdpReceiveResult rec)
        {
            Span<char> method = stackalloc char[RequestLineMethodLimit];
            Span<char> path = stackalloc char[RequestLinePathLimit];
            Span<char> version = stackalloc char[RequestLineVersionLimit];

            try
            {
                // Read method
                int read = stream.ReadIntoBufferUntilChar(' ', method);
                method = method[..read];

                // Read path
                read = stream.ReadIntoBufferUntilChar(' ', path);
                path = path[..read];

                // Read version
                read = stream.ReadIntoBufferUntilChar('\r', version);
                version = version[..read];

                stream.ReadByte(); // skip \n after \r
            }
            catch (Exception e)
            {
                throw new Exception("Failed to read request line. Maybe you tried to connect with HTTPS?", e);
            }

            ListenerContext context = new SipListenerContext(client)
            {
                RealRemoteEndpoint = (rec.RemoteEndPoint as IPEndPoint)!,
            };

            
            Bunkum.CustomHttpListener.Request.HttpVersion httpVersion = version switch
            {
                "HTTP/1.0" => Bunkum.CustomHttpListener.Request.HttpVersion.Http1_0,
                "HTTP/1.1" => Bunkum.CustomHttpListener.Request.HttpVersion.Http1_1,
                "SIP/2.0" => Bunkum.CustomHttpListener.Request.HttpVersion.Sip2_0,
                _ => Bunkum.CustomHttpListener.Request.HttpVersion.Unknown,
            };

            context.Version = httpVersion;

            if (httpVersion == Bunkum.CustomHttpListener.Request.HttpVersion.Unknown)
                throw new NotSupportedException(version.ToString());
            
            context.Method = MethodUtils.FromString(method);
            
            if (context.Method == Method.Invalid)
            {
                throw new Exception("Rejected request that sent invalid method " + method.ToString());
            }
            
            foreach ((string? key, string? value) in ReadHeaders(stream))
            {
                Debug.Assert(key != null);
                Debug.Assert(value != null);

                context.RequestHeaders.Add(key, value);
            }

            if (context.RequestHeaders["Host"] == null)
            {
                /*
                if (httpVersion >= Bunkum.CustomHttpListener.Request.HttpVersion.Http1_1)
                {
                    throw new Exception("Rejected request without Host header");
                }
                */

                context.RequestHeaders["Host"] = "localhost";
            }

            if (this._useForwardedIp && context.RequestHeaders["X-Forwarded-For"] != null)
            {
                string forwardedFor = context.RequestHeaders["X-Forwarded-For"]!.Split(',', 2)[0];

                if (forwardedFor.Contains(':')) // if IPV6, surround in brackets to support parsing
                    forwardedFor = '[' + forwardedFor + ']';

                string forwardedIp = $"{forwardedFor}:{context.RealRemoteEndpoint.Port}";
                bool result = IPEndPoint.TryParse(forwardedIp, out IPEndPoint? endPoint);

                if (!result)
                {
                    throw new Exception($"Rejected request from proxy that sent invalid IP '{forwardedIp}'");
                }

                Debug.Assert(endPoint != null);

                context.RemoteEndpoint = endPoint;
            }
            else
            {
                context.RemoteEndpoint = context.RealRemoteEndpoint;
            }
            
            // skip nullable warning since we have already asserted that this header exists
            string host = context.RequestHeaders.GetValues("Host")!.First();

            context.Uri = new Uri($"sip://{host}:5060/{path}", UriKind.Absolute);

            if (context.RequestHeaders["Cookie"] != null)
            {
                foreach ((string? key, string? value) in ReadCookies(context.RequestHeaders["Cookie"]))
                {
                    Debug.Assert(key != null);
                    Debug.Assert(value != null);

                    context.Cookies.Add(key, value);
                }
            }
            
            context.Query = HttpUtility.ParseQueryString(context.Uri.Query);
            
            MemoryStream inputStream = new((int)context.ContentLength);
            if (context.ContentLength > 0)
            {
                stream.ReadIntoStream(inputStream, (int)context.ContentLength);
                inputStream.Seek(0, SeekOrigin.Begin);
            }
            context.InputStream = inputStream;

            return context;
        }
    }
}

