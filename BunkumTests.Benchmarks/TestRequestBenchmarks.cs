using BenchmarkDotNet.Attributes;
using Bunkum.CustomHttpListener.Listeners.Direct;
using Bunkum.HttpServer;
using BunkumTests.HttpServer.Endpoints;

namespace BunkumTests.Benchmarks;

[MemoryDiagnoser]
public class TestRequestBenchmarks
{
    private static readonly Uri Endpoint = new("/");
    
    private DirectHttpListener _listener = null!;
    private HttpClient _client = null!;
    private BunkumHttpServer _server = null!;

    [GlobalSetup]
    public void Setup()
    {
        this._listener = new DirectHttpListener(false);
        this._client = this._listener.GetClient();
        this._server = new BunkumHttpServer(this._listener, false);
        
        this._server.AddEndpointGroup<TestEndpoints>();
        this._server.Start(1);
    }

    [Benchmark]
    public Task GetTestEndpoint()
    {
        return this._client.GetAsync(Endpoint);
    }
}