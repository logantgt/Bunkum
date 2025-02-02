using Bunkum.HttpServer;
using Bunkum.HttpServer.Endpoints;

namespace BunkumSampleApplication.Endpoints;

public class ManualEndpoints : EndpointGroup
{
    public string ManuallyAddedEndpoint(RequestContext context) =>
        "This endpoint was added manually by BunkumHttpServer.AddEndpointGroup";
}