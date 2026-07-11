using System.Net;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class WebhookNotifierNodeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_sends_write_summary_payload_by_default()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string> { ["webhookUrl"] = "https://example.test/hook" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(),
            WorkflowNodeTypes.SqlServerDestination,
            new DestinationWriteResult("dest-1", 42, DateTimeOffset.UtcNow),
            WorkflowDataContract.DestinationWriteResult);

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Post);
        capturedBody.Should().Contain("\"recordsWritten\":42");
        capturedBody.Should().Contain("\"destinationId\":\"dest-1\"");
        output.Metadata["delivered"].Should().Be(true);
        output.Metadata["httpStatusCode"].Should().Be(200);
    }

    [Fact]
    public async Task ExecuteAsync_retries_until_success()
    {
        var attempts = 0;
        var handler = new FakeHandler(_ =>
        {
            attempts++;
            var response = attempts < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(response));
        });
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://example.test/hook",
            ["retryCount"] = "3",
            ["retryBackoffSeconds"] = "0",
        });

        var output = await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        attempts.Should().Be(3);
        output.Metadata["delivered"].Should().Be(true);
        output.Metadata["attempts"].Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_all_retries_fail_and_onFailure_is_Fail()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://example.test/hook",
            ["retryCount"] = "1",
            ["retryBackoffSeconds"] = "0",
        });

        var act = async () => await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_does_not_throw_when_onFailure_is_BestEffort()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://example.test/hook",
            ["onFailure"] = "BestEffort",
            ["retryCount"] = "0",
        });

        var output = await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        output.Metadata["delivered"].Should().Be(false);
    }

    [Fact]
    public async Task ExecuteAsync_applies_bearer_auth_header_from_secret_provider()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new FakeHandler(request =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var secretProvider = new FakeSecretProvider("super-secret-token");
        var executor = new WebhookNotifierNodeExecutor(secretProvider, new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://example.test/hook",
            ["authType"] = "Bearer",
            ["authSecretKeyVaultName"] = "kv",
            ["authSecretName"] = "webhook-token",
        });

        await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        capturedRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization.Parameter.Should().Be("super-secret-token");
    }

    [Fact]
    public async Task ExecuteAsync_never_forwards_record_level_data_even_when_flag_is_set()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://example.test/hook",
            ["includeRecordLevelData"] = "true",
        });

        var output = await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        capturedBody.Should().NotContain("resourceType");
        output.Metadata["warning"].Should().Be(
            "includeRecordLevelData is not supported — record-level data is never forwarded to a webhook, to protect PHI.");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_call_http_when_no_url_configured()
    {
        var called = false;
        var handler = new FakeHandler(_ => { called = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var executor = new WebhookNotifierNodeExecutor(secretProvider: null, httpClientFactory: new FakeHttpClientFactory(handler));
        var node = CreateNode(new Dictionary<string, string>());

        var output = await executor.ExecuteAsync(CreateContext(), node, [], CancellationToken.None);

        called.Should().BeFalse();
        output.Metadata["delivered"].Should().Be(false);
    }

    private static WorkflowNode CreateNode(Dictionary<string, string> config)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "webhook-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.WebhookNotifier,
            WorkflowNodeCategory.Destination,
            71,
            configurationJson: JsonSerializer.Serialize(config));
    }

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request);
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        private readonly string _secret;

        public FakeSecretProvider(string secret) => _secret = secret;

        public Task<string> GetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken)
            => Task.FromResult(_secret);
    }
}
