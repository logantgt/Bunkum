using Bunkum.CustomHttpListener.Parsing;
using Bunkum.HttpServer;
using Bunkum.HttpServer.Database.Dummy;
using Bunkum.HttpServer.Endpoints;
using Bunkum.HttpServer.Responses;
using Bunkum.HttpServer.Storage;
using BunkumSampleApplication.Configuration;
using BunkumSampleApplication.Models;
using BunkumSampleApplication.Services;
using System.Net;
using System.Runtime.CompilerServices;

namespace BunkumSampleApplication.Endpoints;

public class WeatherEndpoints : EndpointGroup
{
    [Endpoint("/api/v1/weather", Method.Get, ContentType.Json)]
    // This is a simple endpoint - the bare minimum.
    // The only requirement of an endpoint is that it is marked with an [Endpoint] attribute and the first argument is a RequestContext.
    public WeatherStatistic GetWeather(RequestContext context)
    {
        // Since we've marked our ContentType as Json, Bunkum will automatically serialize this object with Newtonsoft.JSON if we return it.
        // Unlike ASP.NET, you can return anything for any content type.
        // If you would like to return a string here despite the JSON content type, you're more than welcome to do so.
        // Bunkum tries to let you do anything, even if it's not exactly standard compliant.
        return new WeatherStatistic
        {
            Time = DateTimeOffset.Now,
            Temperature = 72,
            Description = "It's hot today!",
        };
    }

    [Endpoint($"/sip:{"192.168.0.233"}", Method.Register)]
    public Response SipRegister(RequestContext context)
    {
        // A valid SIP request formulated by a UAC MUST, at a minimum, contain
        // the following header fields: To, From, CSeq, Call - ID, Max - Forwards,
        // and Via; all of these header fields are mandatory in all SIP requests.
        Response res = new Response(HttpStatusCode.Unauthorized);

        context.ResponseHeaders.Add("Via", context.RequestHeaders.Get("Via"));
        context.ResponseHeaders.Add("From", context.RequestHeaders.Get("From"));
        context.ResponseHeaders.Add("To", context.RequestHeaders.Get("To"));
        context.ResponseHeaders.Add("Call-ID", context.RequestHeaders.Get("Call-ID"));
        context.ResponseHeaders.Add("CSeq", context.RequestHeaders.Get("CSeq"));

        return new Response(HttpStatusCode.Unauthorized);
    }
    
    // Let's introduce some more Bunkum concepts.
    [Endpoint("/api/v2/weather", Method.Get, ContentType.Json)]
    // Here, we inject the ExampleConfiguration we set up in Program.cs.
    // It can be named anything, just as long as the type matches.
    public WeatherStatistic GetWeatherV2(RequestContext context, ExampleConfiguration configuration)
    {
        return new WeatherStatistic
        {
            Time = DateTimeOffset.Now,
            Temperature = 72,
            Description = configuration.ExampleVariable,
        };
    }
    
    [Endpoint("/api/v3/weather", Method.Get, ContentType.Json)]
    // We can do this for just about anything we set up in Program.cs. Let's get crazy...
    public WeatherStatistic GetWeatherV3(RequestContext context,
        ExampleConfiguration configuration,
        DummyDatabaseContext database,
        IDataStore dataStore, // For IDataStore, it's important to use the interface. Otherwise, you can't swap it out easily.
        DateTimeOffset timeFromTimeService)
    {
        dataStore.WriteToStore($"request-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}", "I'm a span with data"u8);
        return new WeatherStatistic
        {
            Time = timeFromTimeService,
            Temperature = database.GetDummyValue(),
            Description = configuration.ExampleVariable,
        };
    }
}