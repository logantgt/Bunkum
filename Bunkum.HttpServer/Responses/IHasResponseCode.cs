using System.Net;

namespace Bunkum.HttpServer.Responses;

/// <summary>
/// Used to indicate that a data object contains a code to include in the response.
/// </summary>
public interface IHasResponseCode
{
    public HttpStatusCode StatusCode { get; }
}