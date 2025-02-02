using System.Net;
using Bunkum.HttpServer;
using BunkumTests.HttpServer.Endpoints;

namespace BunkumTests.HttpServer.Tests;

public class BodyTests : ServerDependentTest
{
    [Test]
    [TestCase("/body/string")]
    [TestCase("/body/byteArray")]
    [TestCase("/body/stream")]
    public async Task CorrectResponseForAllTypes(string endpoint)
    {
        (BunkumHttpServer server, HttpClient client) = this.Setup();
        server.AddEndpointGroup<BodyEndpoints>();
        
        HttpResponseMessage msg = await client.PostAsync(endpoint, new StringContent("works"));
        Assert.Multiple(async () =>
        {
            Assert.That(msg.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await msg.Content.ReadAsStringAsync(), Is.EqualTo("works"));
        });
    }

    [Test]
    public async Task ReturnsBadRequestOnNoData()
    {
        (BunkumHttpServer server, HttpClient client) = this.Setup();
        server.AddEndpointGroup<BodyEndpoints>();
        
        HttpResponseMessage msg = await client.PostAsync("/body/string", null);
        Assert.That(msg.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}