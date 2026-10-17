# Aspire Langfuse hosting integration

A standalone, CommunityToolkit-style Aspire hosting package for Langfuse. This is a local implementation, not a published or officially accepted CommunityToolkit package. It targets .NET 10 and Aspire 13.6.1, with Langfuse v4 images by default.

`AddLangfuse` orchestrates the web/API, worker, PostgreSQL, Redis, ClickHouse, and Aspire Azure Blob Storage resources. Local blob storage uses `AddAzureStorage(...).RunAsEmulator().AddBlobs(...)`. Supply any existing dependencies in the configuration callback; only missing dependencies are created. `AddLangfuseEndpoint` references an existing self-hosted or cloud instance without creating infrastructure.

The [MassTransit example](https://github.com/CommunityToolkit/Aspire/tree/main/src/CommunityToolkit.Aspire.MassTransit.RabbitMQ) is a **client** integration. This package follows the toolkit's packaging conventions and [Aspire's hosting integration model](https://aspire.dev/integrations/custom-integrations/hosting-integrations/): `Add*` builders, resource types, deferred expressions, secret parameters, health checks, and resource references. A client SDK or OpenTelemetry exporter remains application-owned.

## Reference from an AppHost

Until a package is published, add a project reference:

```xml
<ProjectReference Include="path/to/aspire-langfuse/src/Aspire.Langfuse.csproj"
                  IsAspireProjectResource="false" />
```

Extensions are in `Aspire.Hosting`; configuration types are in `CommunityToolkit.Aspire.Hosting.Langfuse`. The package ID is `CommunityToolkit.Aspire.Hosting.Langfuse`.

## Start a complete stack

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var langfuse = builder.AddLangfuse("langfuse", options =>
{
    options.UseDataVolumes = true;
}, port: 3000);

builder.AddProject<Projects.Api>("api")
    .WithReference(langfuse)
    .WaitFor(langfuse);

builder.Build().Run();
```

Open the web resource from the Aspire dashboard and create your user, organization, project, and API keys. There is no default login. `WithReference` supplies `ConnectionStrings__langfuse` and `LANGFUSE_BASE_URL`. Add `.WithApiKeys(publicKey, secretKey)` to supply `LANGFUSE_PUBLIC_KEY` and `LANGFUSE_SECRET_KEY` as well; both arguments are Aspire parameter builders, and the secret key must be marked secret. Configure your application's Langfuse SDK to consume these variables, or configure an OTLP exporter using Langfuse's instructions. This package does not register an SDK or change Aspire's dashboard telemetry exporter.

Ports are dynamically assigned when omitted. Both containers wait for infrastructure health checks; the worker also waits for web readiness so database migrations finish first. Application readiness is explicit through `.WaitFor(langfuse)`. The default resource names are `<name>-worker`, `<name>-postgres`, `<name>-db`, `<name>-redis`, `<name>-clickhouse`, `<name>-storage`, and `<name>-blobs`, so multiple stacks can coexist.

## Reuse PostgreSQL, Redis, and Aspire blob storage

```csharp
using CommunityToolkit.Aspire.Hosting.Langfuse;

var database = builder.AddPostgres("postgres").WithDataVolume()
    .AddDatabase("langfuse-db", "langfuse");
var redis = builder.AddRedis("redis")
    .WithArgs("--maxmemory-policy", "noeviction");
var blobs = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithArgs("--disableProductStyleUrl"))
    .AddBlobs("blobs");

var langfuse = builder.AddLangfuse("langfuse", options =>
{
    options.Postgres = database;
    options.Redis = redis;
    options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(blobs);
    options.UseDataVolumes = true;
});
```

Only the Langfuse web, worker, and ClickHouse containers are added here. The integration registers or reuses a logical blob container through Aspire's `AddBlobContainer(...)` and waits for its health check before starting Langfuse. You can also pass an existing Aspire blob container to `LangfuseBlobStorageSettings.AzureBlob(...)`. Supplied services are not reparented or assigned volumes. Use a dedicated Langfuse database; Langfuse applies schema migrations. PostgreSQL is passed as a properly escaped `postgresql://` URI rather than an Npgsql connection string. Redis must use `noeviction` because it holds queues, and sharing it requires accounting for Langfuse's queue keys.

Aspire's `RunAsEmulator()` manages Azurite internally. The integration uses Langfuse's native Azure Blob support and the emulator's `/devstoreaccount1` endpoint path. `--disableProductStyleUrl` is needed when accessing the emulator by its Aspire container hostname. The resource overload uses the standard public development credentials and requires `RunAsEmulator()`; for managed Azure Blob, use the expression overload with account credentials shown below.

## Reuse managed or externally hosted dependencies

```csharp
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Langfuse;

var databaseUrl = builder.AddConnectionString("langfuse-postgres-url");
var redisUrl = builder.AddConnectionString("langfuse-redis-url");
var clickHousePassword = builder.AddParameter("clickhouse-password", secret: true);
var blobAccount = builder.AddParameter("blob-account");
var blobKey = builder.AddParameter("blob-key", secret: true);

var langfuse = builder.AddLangfuse("langfuse", options =>
{
    options.DatabaseUrl = databaseUrl;
    options.RedisUrl = redisUrl;
    options.PublicUrl = ReferenceExpression.Create($"https://langfuse.example.com");
    options.ClickHouse = new LangfuseClickHouseSettings(
        ReferenceExpression.Create($"https://clickhouse.example.com:8443"),
        ReferenceExpression.Create($"clickhouse://clickhouse.example.com:9440?secure=true"),
        ReferenceExpression.Create($"default"),
        ReferenceExpression.Create($"{clickHousePassword.Resource}"));
    options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(
        ReferenceExpression.Create($"https://{blobAccount.Resource}.blob.core.windows.net"),
        ReferenceExpression.Create($"{blobAccount.Resource}"),
        ReferenceExpression.Create($"{blobKey.Resource}"),
        containerName: "langfuse");
});
```

Store `ConnectionStrings:langfuse-postgres-url` as a PostgreSQL URI and `ConnectionStrings:langfuse-redis-url` as a `redis://` or `rediss://` URI in AppHost configuration or user secrets. Aspire's .NET connection string formats are not interchangeable with these URIs. Each container must be able to reach the supplied hosts; `localhost` inside a container refers to that container.

Both ClickHouse HTTP and native migration endpoints are required. Set `Database`, `ClusterEnabled`, and `ClusterName` on `LangfuseClickHouseSettings` for your deployment. Use its optional `Resource` property when an existing Aspire resource should be awaited. External URL-only dependencies have no Aspire health gate.

Azure Blob requires an account name and account key for Langfuse's native implementation; the account must permit shared-key access. Passing only an Aspire Azure resource's managed-identity service URI is insufficient. For expression-based managed storage settings, create the container before starting Langfuse and grant the required read/write/delete permissions. The Aspire emulator resource overload registers its container through Aspire automatically.

For S3-compatible storage, use:

```csharp
options.BlobStorage = LangfuseBlobStorageSettings.S3(
    endpointExpression, accessKeyExpression, secretKeyExpression, "existing-bucket") with
{
    Region = "us-east-1",
    ForcePathStyle = false,
    PublicEndpoint = browserReachableEndpointExpression,
    Resource = existingStorageResource // optional Aspire readiness dependency
};
```

The S3 bucket must already exist. Events, media, and batch exports use separate `events/`, `media/`, and `exports/` prefixes. `Endpoint` is used for server access; `PublicEndpoint` is used for signed media and export URLs and must be reachable by browsers and SDK clients. Local defaults retain host URLs for public links while using container addresses for server traffic. See [Langfuse's blob storage configuration](https://langfuse.com/self-hosting/deployment/infrastructure/blobstorage).

## Reference an existing Langfuse instance

```csharp
var publicKey = builder.AddParameter("langfuse-public-key");
var secretKey = builder.AddParameter("langfuse-secret-key", secret: true);
var langfuse = builder.AddLangfuseEndpoint("langfuse", new Uri("https://cloud.langfuse.com"))
    .WithApiKeys(publicKey, secretKey);

builder.AddProject<Projects.Api>("api").WithReference(langfuse);
```

For a configurable host, use the expression overload:

```csharp
var url = builder.AddParameter("langfuse-url");
var langfuse = builder.AddLangfuseEndpoint("langfuse", ReferenceExpression.Create($"{url.Resource}"));
```

The URI overload validates HTTP(S) base URLs. Parameter expressions are resolved by Aspire later and must contain a valid base URL. Neither overload creates services or probes the remote host. The standard `LANGFUSE_*` variables configure one SDK destination per application. For multiple destinations in one application, use the named connection strings and configure each client explicitly.

## Initialize a project without using the UI

```csharp
var publicKey = builder.AddParameter("langfuse-public-key");
var secretKey = builder.AddParameter("langfuse-secret-key", secret: true);
var userEmail = builder.AddParameter("langfuse-user-email");
var userPassword = builder.AddParameter("langfuse-user-password", secret: true);

var langfuse = builder.AddLangfuse("langfuse")
    .WithInitialProject(publicKey, secretKey, organizationId: "my-org", projectId: "my-project")
    .WithEnvironment("LANGFUSE_INIT_USER_EMAIL", userEmail)
    .WithEnvironment("LANGFUSE_INIT_USER_PASSWORD", userPassword);
```

Supply the key and user values through AppHost configuration or user secrets. Initialization runs on first creation; changing these values does not rotate existing keys or passwords. `WithApiKeys` alone references keys that already exist. See [Langfuse headless initialization](https://langfuse.com/self-hosting/administration/headless-initialization).

## Persistence, customization, and publishing

Named data volumes are opt-in through `UseDataVolumes` and apply to integration-created infrastructure, including Aspire's storage emulator. Configure supplied resources' persistence yourself. The AppHost needs a `UserSecretsId` to persist generated local secrets. Authentication, salt, encryption, and ClickHouse secrets use secret parameters; the encryption key is generated as 32 random bytes encoded as 64 hex characters. Keep salt and encryption values stable when using existing data. Supply `NextAuthSecret`, `Salt`, and `EncryptionKey` parameter builders to reuse your own values.

Both Langfuse containers use `ImageTag`, which defaults to `4`. Default infrastructure versions are PostgreSQL 17, Redis 7, ClickHouse 25.12, and the emulator version supplied by Aspire's Azure Storage integration. Choose and validate exact image versions for your environment. The worker and dependency builders are available through `langfuse.Resource.Worker`, `.Postgres`, `.Redis`, `.ClickHouse`, and `.BlobStorage` for further Aspire configuration. `.WithEnvironment(...)` on `langfuse` configures the web container; apply shared advanced settings to `langfuse.Resource.Worker` as well.

Container resources and deferred connection expressions participate in Aspire publishing. Set `BlobStorage` to managed storage with credentials when publishing; the default emulator is local-only and publishing without an override fails with a clear configuration error. Set `PublicUrl` to the public Langfuse hostname and configure storage's `PublicEndpoint` for signed URLs. Publishing requires explicit secret inputs; generated development secrets are not embedded in manifests. Use your AppHost's deployment integration to select compute, private networking, TLS, storage, and secrets. Production availability, backups, and deployment provisioning remain caller responsibilities.

## Run the Ollama agent example

The [sample AppHost](examples/Langfuse.AppHost/Program.cs) runs Langfuse and Ollama, downloads `qwen3.5:2b`, and connects the [agent service](examples/Langfuse.Agent/Program.cs) to both using Aspire resource references. The agent uses Microsoft Agent Framework and OllamaSharp. Its `get_langfuse_health` tool makes a real HTTP request to the configured Langfuse instance, then the model uses that result to answer. Agent, model generation, and tool spans are exported directly to Langfuse through OTLP.

```bash
dotnet run --project examples/Langfuse.AppHost
```

Open the `agent` HTTP endpoint from the Aspire dashboard to use the chat page. Type a message and click the send arrow (or press Enter; Shift+Enter adds a line), or use **Check Langfuse health** to start. Expand **Run details** under a reply for the model, tool call count, and Langfuse trace ID. **Clear chat** clears the visible messages. Each message starts an independent agent run.

The page adapts [Flowbite's default and clean chat examples](https://flowbite.com/docs/components/chat-bubble/) into a centered conversation with a bottom composer, using local HTML/CSS and no frontend build. Flowbite's MIT notice is included in [flowbite-LICENSE.txt](examples/Langfuse.Agent/wwwroot/flowbite-LICENSE.txt).

You can also call the same endpoint directly:

```bash
curl -X POST http://localhost:<agent-port>/run \
  -H 'Content-Type: application/json' \
  -d '{}'
```

The response includes the model's answer, `toolCalls`, and a `traceId` to find in Langfuse. Supply `{"prompt":"Check Langfuse health and explain the result."}` to change the prompt. This example records prompts, tool results, and answers in Langfuse; its telemetry configuration enables sensitive data capture explicitly.

The default is sized for an RTX 3050 Ti with 4 GB VRAM: Qwen3.5 2B, a 2,048-token context, one loaded model, and one concurrent inference. NVIDIA GPU access is enabled through Aspire's Ollama `WithGPUSupport()`. Docker must have GPU access; available VRAM determines how much Ollama can offload. Model files are cached in the `aspire-langfuse-ollama-models` volume. The first run takes longer while downloading them.

```bash
# Run on CPU if Docker has no NVIDIA GPU access.
dotnet run --project examples/Langfuse.AppHost -- --Agent:UseGpu=false

# Select another Ollama model, or run only the Langfuse stack.
dotnet run --project examples/Langfuse.AppHost -- --Agent:Model=qwen3.5:0.8b
dotnet run --project examples/Langfuse.AppHost -- --Agent:Enabled=false
```

For local and shared modes the sample initializes a project, API keys, and an `agent@example.test` login. Generated keys and the login password persist in the AppHost's user secrets. Retrieve `Parameters:langfuse-user-password` with `dotnet user-secrets list --project examples/Langfuse.AppHost` to sign in. Initialization applies when the database is first created; existing data keeps its existing credentials. See [Ollama tool calling](https://docs.ollama.com/capabilities/tool-calling) and [Agent Framework observability](https://learn.microsoft.com/en-us/agent-framework/tutorials/agents/enable-observability) for the underlying client and tracing APIs.

## Build and test

```bash
dotnet build Aspire.Langfuse.slnx
dotnet test tests/Aspire.Langfuse.Tests.csproj
dotnet pack src/Aspire.Langfuse.csproj -c Release

# Docker is required to run either hosted mode.
dotnet run --project examples/Langfuse.AppHost
dotnet run --project examples/Langfuse.AppHost -- --Langfuse:Mode=shared

# Existing mode uses your Langfuse host; Ollama and the agent still run locally.
dotnet run --project examples/Langfuse.AppHost -- --Langfuse:Mode=existing
```

For existing mode configure `Parameters:langfuse-url`, `Parameters:langfuse-public-key`, and `Parameters:langfuse-secret-key` in the sample AppHost's user secrets. Model tests run without Docker and cover dependency replacement, URI escaping, network-aware URLs, shared configuration, API-key propagation, persistence annotations, and publishing expressions.

The Docker smoke test creates an ephemeral Aspire stack, initializes a temporary project, sends an OTLP span, and polls Observations API v2 for the persisted result. It uses Aspire's `RunAsEmulator()` for blob storage and stops the test resources on completion:

```bash
dotnet run --project tests/SmokeTests
dotnet run --project tests/SmokeTests -- --shared=true

# Run the real Qwen3.5 agent and verify its observations in Langfuse.
dotnet run --project tests/SmokeTests -- --agent=true
```

The second command tests reuse of caller-supplied PostgreSQL, Redis, and an emulated Azure Blob resource. The agent test requires a nonempty model response, a real tool invocation, and persisted `AGENT`, `GENERATION`, and `TOOL` observations with the configured model. Add `--gpu=false` for CPU execution or `--shared=true` to combine modes. The model cache volume remains after the test.

Langfuse v4 ingestion uses OTLP with `x-langfuse-ingestion-version: 4`; the legacy `trace-create` ingestion event is not accepted by its default data model.
