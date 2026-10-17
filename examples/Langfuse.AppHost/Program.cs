using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using CommunityToolkit.Aspire.Hosting.Langfuse;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var mode = builder.Configuration["Langfuse:Mode"] ?? "local";
IResourceBuilder<LangfuseResource>? hosted = null;
IResourceBuilder<LangfuseEndpointResource>? external = null;
var publicKey = AddApiKey("langfuse-public-key", "pk-lf-", secret: false);
var secretKey = AddApiKey("langfuse-secret-key", "sk-lf-", secret: true);

switch (mode)
{
    case "local":
        hosted = builder.AddLangfuse("langfuse", options => options.UseDataVolumes = true);
        break;

    case "shared":
        // This mode demonstrates reusing other AppHost resources during development.
        // For a managed Azure account, use LangfuseBlobStorageSettings.AzureBlob instead.
        if (builder.ExecutionContext.IsPublishMode)
        {
            throw new InvalidOperationException("The shared example uses Azurite and runs locally. Configure managed storage for publishing.");
        }

        var database = builder.AddPostgres("shared-postgres").WithDataVolume()
            .AddDatabase("langfuse-db", "langfuse");
        var redis = builder.AddRedis("shared-redis")
            .WithArgs("--maxmemory-policy", "noeviction").WithDataVolume();
        var blobs = builder.AddAzureStorage("shared-storage")
            .RunAsEmulator(emulator => emulator.WithArgs("--disableProductStyleUrl").WithDataVolume())
            .AddBlobs("shared-blobs");

        hosted = builder.AddLangfuse("langfuse", options =>
        {
            options.Postgres = database;
            options.Redis = redis;
            options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(blobs);
            options.UseDataVolumes = true;
        });
        break;

    case "existing":
        var url = builder.AddParameter("langfuse-url");
        external = builder.AddLangfuseEndpoint("langfuse", ReferenceExpression.Create($"{url.Resource}"))
            .WithApiKeys(publicKey, secretKey);
        break;

    default:
        throw new ArgumentException("Langfuse:Mode must be local, shared, or existing.");
}

if (hosted is not null)
{
    var email = builder.AddParameter("langfuse-user-email", "agent@example.test");
    var password = builder.AddParameter("langfuse-user-password",
        new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);
    hosted.WithInitialProject(publicKey, secretKey, "aspire-example", "aspire-agent-example")
        .WithEnvironment("LANGFUSE_INIT_USER_EMAIL", email)
        .WithEnvironment("LANGFUSE_INIT_USER_PASSWORD", password);
}

if (builder.Configuration.GetValue("Agent:Enabled", true))
{
    var ollama = builder.AddOllama("ollama")
        .WithImageTag("0.40.5")
        .WithDataVolume("aspire-langfuse-ollama-models")
        .WithEnvironment("OLLAMA_CONTEXT_LENGTH", "2048")
        .WithEnvironment("OLLAMA_NUM_PARALLEL", "1")
        .WithEnvironment("OLLAMA_MAX_LOADED_MODELS", "1");
    if (builder.Configuration.GetValue("Agent:UseGpu", true))
    {
        ollama.WithGPUSupport();
    }

    var model = ollama.AddModel("chat", builder.Configuration["Agent:Model"] ?? "qwen3.5:2b");
    var agent = builder.AddProject<Projects.Langfuse_Agent>("agent")
        .WithHttpEndpoint(name: "http")
        .WithHttpHealthCheck("/health")
        .WithReference(model)
        .WaitFor(model);
    if (hosted is not null)
    {
        agent.WithReference(hosted).WaitFor(hosted).WaitFor(hosted.Resource.Worker);
    }
    else
    {
        agent.WithReference(external!);
    }
}

builder.Build().Run();

IResourceBuilder<ParameterResource> AddApiKey(string name, string prefix, bool secret) =>
    mode == "existing" || builder.ExecutionContext.IsPublishMode
        ? builder.AddParameter(name, secret: secret)
        : builder.AddParameter(name, new ApiKeyDefault(prefix), secret: secret, persist: true);

internal sealed class ApiKeyDefault(string prefix) : ParameterDefault
{
    public override string GetDefaultValue() => $"{prefix}{Guid.NewGuid():N}";
    public override void WriteToManifest(ManifestPublishingContext context) { }
}
