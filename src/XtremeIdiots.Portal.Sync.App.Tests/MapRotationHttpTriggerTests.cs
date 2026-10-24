using System.Net;
using System.Reflection;
using System.Text.Json;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationHttpTriggerTests
{
    [Fact]
    public void RotationHttpRoutesArePublishedInTheApimContract()
    {
        var contractPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "XtremeIdiots.Portal.Sync.App", "openapi.json");
        using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var method in typeof(MapRotationHttpTriggers).GetMethods())
        {
            var function = method.GetCustomAttribute<FunctionAttribute>();
            if (function is null)
            {
                continue;
            }
            var trigger = method.GetParameters().Select(parameter => parameter.GetCustomAttribute<HttpTriggerAttribute>())
                .OfType<HttpTriggerAttribute>().Single();
            Assert.True(paths.TryGetProperty($"/api/{trigger.Route}", out var route), $"Missing APIM route for {function.Name}");
            Assert.NotNull(trigger.Methods);
            foreach (var verb in trigger.Methods)
            {
                Assert.Equal(function.Name, route.GetProperty(verb.ToLowerInvariant()).GetProperty("operationId").GetString());
            }
        }
    }

    [Fact]
    public async Task CancellationAcknowledgmentDoesNotClaimTerminationOrAbandonInFlightTransfer()
    {
        using var services = new ServiceCollection().Configure<WorkerOptions>(options =>
            options.Serializer = new JsonObjectSerializer()).BuildServiceProvider();
        var function = new Mock<FunctionContext>();
        function.SetupGet(x => x.InstanceServices).Returns(services);
        var request = new Mock<HttpRequestData>(function.Object);
        request.Setup(x => x.CreateResponse()).Returns(() => new TestResponse(function.Object));
        var client = new Mock<DurableTaskClient>("tests");
        const string instance = "maprot-apply-test";
        client.Setup(x => x.GetInstanceAsync(instance, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(MapRotationWorkflow.ApplyMapRotationV2), instance)
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Running
            });
        client.Setup(x => x.RaiseEventAsync(instance, MapRotationWorkflow.CancellationEvent,
            It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var trigger = new MapRotationHttpTriggers(Mock.Of<ILogger<MapRotationHttpTriggers>>());

        var response = await trigger.TerminateMapRotationOrchestration(request.Object, client.Object, instance);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body);
        var json = await reader.ReadToEndAsync();
        Assert.Contains("\"terminated\":false", json);
        Assert.Contains("\"cancellationRequested\":true", json);
        client.Verify(x => x.RaiseEventAsync(instance, MapRotationWorkflow.CancellationEvent,
            It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(client.Invocations, invocation => invocation.Method.Name == nameof(DurableTaskClient.TerminateInstanceAsync));
    }

    private sealed class TestResponse(FunctionContext context) : HttpResponseData(context)
    {
        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies => throw new NotSupportedException();
    }
}
