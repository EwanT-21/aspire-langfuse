using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Langfuse;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DisableDashboard = true
});
var useSharedResources = builder.Configuration["shared"] == "true";
var runAgent = builder.Configuration["agent"] == "true";
var name = $"langfuse-smoke-{Guid.NewGuid():N}"[..23];
string RandomSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
IResourceBuilder<ParameterResource> Secret(string suffix) =>
    builder.AddParameter($"{name}-{suffix}", RandomSecret(), secret: true);

// Explicit ephemeral credentials keep this test independent of AppHost user secrets.
foreach (var suffix in new[] { "postgres-password", "redis-password", "clickhouse-password" })
{
    builder.Configuration[$"Parameters:{name}-{suffix}"] = RandomSecret();
}

var authSecret = Secret("auth");
var salt = Secret("salt");
var encryptionKey = Secret("encryption");
var publicKeyValue = $"pk-lf-{Guid.NewGuid():N}";
var secretKeyValue = $"sk-lf-{RandomSecret()}";
var publicKey = builder.AddParameter($"{name}-public-key", publicKeyValue);
var secretKey = builder.AddParameter($"{name}-secret-key", secretKeyValue, secret: true);

IResourceBuilder<PostgresDatabaseResource>? database = null;
IResourceBuilder<RedisResource>? redis = null;
LangfuseBlobStorageSettings? blobStorage = null;
if (useSharedResources)
{
    database = builder.AddPostgres($"{name}-shared-pg", password: Secret("shared-pg-password"))
        .WithImageTag("17").AddDatabase($"{name}-shared-db", "langfuse");
    redis = builder.AddRedis($"{name}-shared-redis", password: Secret("shared-redis-password"))
        .WithArgs("--maxmemory-policy", "noeviction");
    var blobs = builder.AddAzureStorage($"{name}-shared-storage")
        .RunAsEmulator(emulator => emulator.WithArgs("--disableProductStyleUrl"))
        .AddBlobs($"{name}-shared-blobs");
    blobStorage = LangfuseBlobStorageSettings.AzureBlob(blobs);
}

var langfuse = builder.AddLangfuse(name, options =>
{
    options.NextAuthSecret = authSecret;
    options.Salt = salt;
    options.EncryptionKey = encryptionKey;
    options.Postgres = database;
    options.Redis = redis;
    options.BlobStorage = blobStorage;
}).WithInitialProject(publicKey, secretKey, $"{name}-org", $"{name}-project");

IResourceBuilder<ProjectResource>? agentService = null;
if (runAgent)
{
    var ollama = builder.AddOllama($"{name}-ollama")
        .WithImageTag("0.40.5")
        .WithDataVolume("aspire-langfuse-ollama-models")
        .WithEnvironment("OLLAMA_CONTEXT_LENGTH", "2048")
        .WithEnvironment("OLLAMA_NUM_PARALLEL", "1")
        .WithEnvironment("OLLAMA_MAX_LOADED_MODELS", "1");
    if (builder.Configuration["gpu"] != "false")
    {
        ollama.WithGPUSupport();
    }

    var model = ollama.AddModel("chat", builder.Configuration["model"] ?? "qwen3.5:2b");
    agentService = builder.AddProject<Projects.Langfuse_Agent>("agent")
        .WithHttpEndpoint(name: "http")
        .WithHttpHealthCheck("/health")
        .WithReference(model)
        .WithReference(langfuse)
        .WaitFor(model)
        .WaitFor(langfuse.Resource.Worker);
}

await using var app = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(runAgent ? 20 : 10));
try
{
    Console.WriteLine($"Starting {name}: {(useSharedResources ? "shared PostgreSQL/Redis and Aspire blob emulator" : "default Aspire blob emulator stack")}");
    await app.StartAsync(timeout.Token);
    await app.ResourceNotifications.WaitForResourceHealthyAsync(name, timeout.Token);
    await app.ResourceNotifications.WaitForResourceHealthyAsync(langfuse.Resource.Worker.Resource.Name, timeout.Token);
    Console.WriteLine("Web and worker health checks passed.");

    using var client = new HttpClient
    {
        BaseAddress = new Uri((await langfuse.Resource.HttpEndpoint.GetValueAsync(timeout.Token))!),
        Timeout = TimeSpan.FromSeconds(30)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKeyValue}:{secretKeyValue}")));
    client.DefaultRequestHeaders.Add("x-langfuse-ingestion-version", "4");

    using (var projects = await client.GetAsync("/api/public/projects", timeout.Token))
    {
        projects.EnsureSuccessStatusCode();
        Console.WriteLine("Headless project initialization and API authentication passed.");
    }

    var traceId = Guid.NewGuid().ToString("N");
    string? expectedModel = null;
    if (agentService is not null)
    {
        Console.WriteLine("Waiting for Ollama model download and agent readiness.");
        await app.ResourceNotifications.WaitForResourceHealthyAsync(agentService.Resource.Name, timeout.Token);
        using var agentClient = new HttpClient
        {
            BaseAddress = new Uri((await agentService.GetEndpoint("http").GetValueAsync(timeout.Token))!),
            Timeout = TimeSpan.FromMinutes(5)
        };
        using var response = await agentClient.PostAsJsonAsync("/run", new { }, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Agent returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(timeout.Token)}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        if (result.GetProperty("toolCalls").GetInt32() < 1 || string.IsNullOrWhiteSpace(result.GetProperty("answer").GetString()))
        {
            throw new InvalidOperationException("The real agent must call its tool and return a nonempty model response.");
        }

        traceId = result.GetProperty("traceId").GetString()!;
        expectedModel = result.GetProperty("model").GetString();
        Console.WriteLine($"Agent model: {result.GetProperty("model").GetString()}");
        Console.WriteLine($"Agent tool calls: {result.GetProperty("toolCalls").GetInt32()}");
        Console.WriteLine($"Agent answer: {result.GetProperty("answer").GetString()}");
        Console.WriteLine($"Agent trace: {traceId}");
    }
    else
    {
        var spanId = Guid.NewGuid().ToString("N")[..16];
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        using (var ingest = await client.PostAsJsonAsync("/api/public/otel/v1/traces", new
        {
            resourceSpans = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[] { new { key = "service.name", value = new { stringValue = "aspire-langfuse-smoke" } } }
                    },
                    scopeSpans = new[]
                    {
                        new
                        {
                            scope = new { name = "aspire-langfuse-smoke" },
                            spans = new[]
                            {
                                new
                                {
                                    traceId,
                                    spanId,
                                    name = "aspire-hosting-smoke-test",
                                    kind = 1,
                                    startTimeUnixNano = startTime.ToString(),
                                    endTimeUnixNano = (startTime + 1_000_000).ToString(),
                                    attributes = new[] { new { key = "langfuse.observation.type", value = new { stringValue = "span" } } },
                                    status = new { code = 1 }
                                }
                            }
                        }
                    }
                }
            }
        }, timeout.Token))
        {
            if (!ingest.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OTLP ingestion returned {ingest.StatusCode}: {await ingest.Content.ReadAsStringAsync(timeout.Token)}");
            }

            var result = await ingest.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            if (result.TryGetProperty("partialSuccess", out var partial) &&
                partial.TryGetProperty("rejectedSpans", out var rejected) && rejected.ToString() != "0")
            {
                throw new InvalidOperationException($"OTLP ingestion rejected spans: {partial}");
            }
        }
    }

    Console.WriteLine($"Trace {traceId} accepted; waiting for worker processing.");
    using var ingestionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    ingestionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
    while (true)
    {
        using var response = await client.GetAsync($"/api/public/v2/observations?traceId={traceId}&fields=core,basic,model,usage", ingestionTimeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Observation lookup returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ingestionTimeout.Token)}");
        }

        var observations = await response.Content.ReadFromJsonAsync<JsonElement>(ingestionTimeout.Token);
        var rows = observations.GetProperty("data").EnumerateArray().ToArray();
        var types = rows.Select(row => row.GetProperty("type").GetString()).ToHashSet();
        if (runAgent && types.Contains("AGENT") && types.Contains("GENERATION") && types.Contains("TOOL"))
        {
            if (!rows.Any(row => row.TryGetProperty("model", out var model) &&
                model.GetString() == expectedModel))
            {
                throw new InvalidOperationException($"Agent observations did not include model {expectedModel}.");
            }

            Console.WriteLine($"PASS: real Ollama agent, generation, and tool observations persisted ({rows.Length} observations).");
            break;
        }

        if (!runAgent && rows.Any(observation =>
                observation.GetProperty("traceId").GetString() == traceId &&
                observation.GetProperty("name").GetString() == "aspire-hosting-smoke-test"))
        {
            Console.WriteLine("PASS: OTLP trace persisted and read back through Observations API v2.");
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ingestionTimeout.Token);
    }
}
catch
{
    var logs = app.Services.GetRequiredService<ResourceLoggerService>();
    foreach (var resource in builder.Resources.Where(resource => resource is ContainerResource or ProjectResource))
    {
        Console.Error.WriteLine($"Recent logs for {resource.Name}:");
        var recent = new Queue<string>();
        await foreach (var batch in logs.GetAllAsync(resource))
        {
            foreach (var line in batch)
            {
                recent.Enqueue(line.Content);
                if (recent.Count > 15)
                {
                    recent.Dequeue();
                }
            }
        }

        foreach (var line in recent)
        {
            Console.Error.WriteLine(line);
        }
    }

    throw;
}
finally
{
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await app.StopAsync(shutdown.Token);
    Console.WriteLine("Test resources stopped.");
}
