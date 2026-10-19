# Aspire Langfuse

Run [Langfuse](https://langfuse.com) alongside your Aspire application. Start a local Langfuse stack, reuse services already in your AppHost, or connect your application to an existing Langfuse instance.

Available on NuGet as [EwanT21.Aspire.Hosting.Langfuse](https://www.nuget.org/packages/EwanT21.Aspire.Hosting.Langfuse).

## Installation

Requires .NET 10 and Aspire 13.6.1. To run Langfuse locally, you also need Docker and the [Aspire CLI](https://aspire.dev/get-started/install-cli/).

From your AppHost directory, install the package:

```bash
dotnet add package EwanT21.Aspire.Hosting.Langfuse --version 0.1.0-beta.1
```

## Quick start

Add Langfuse to your AppHost's `Program.cs`:

```csharp
using Aspire.Hosting;

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

Replace `Projects.Api` with your application's project. This starts Langfuse v4 with its worker, PostgreSQL, Redis, ClickHouse, and Azure Blob Storage emulator. Aspire handles the connections and startup order.

Generated local secrets are saved in your AppHost's user secrets. If your AppHost doesn't already have a `UserSecretsId`, run `dotnet user-secrets init` from its directory before starting it.

Run your AppHost and open the `langfuse` endpoint in the Aspire dashboard. Create your account, organization, and project, then generate API keys in Langfuse. Omit `port: 3000` to let Aspire assign an available port.

## Connect your application

`WithReference(langfuse)` passes the Langfuse URL to your application. To pass an existing project's API keys as well, add these lines before `builder.Build()`:

```csharp
var publicKey = builder.AddParameter("langfuse-public-key");
var secretKey = builder.AddParameter("langfuse-secret-key", secret: true);

langfuse.WithApiKeys(publicKey, secretKey);
```

Save the values in your AppHost's user secrets:

```bash
dotnet user-secrets set "Parameters:langfuse-public-key" "pk-lf-..."
dotnet user-secrets set "Parameters:langfuse-secret-key" "sk-lf-..."
```

Applications that reference the resource receive:

| Configuration | Value |
| --- | --- |
| `ConnectionStrings__langfuse` | Langfuse base URL; the name follows your resource name |
| `LANGFUSE_BASE_URL` | Langfuse base URL |
| `LANGFUSE_PUBLIC_KEY` | Public API key, when configured |
| `LANGFUSE_SECRET_KEY` | Secret API key, when configured |

Configure your application's Langfuse SDK or OpenTelemetry exporter to use these values. The hosting package manages the Langfuse services; telemetry setup lives in your application. The [example agent](https://github.com/EwanT-21/aspire-langfuse/blob/main/examples/Langfuse.Agent/Program.cs) shows OTLP export to Langfuse v4.

### Create a project on first startup

You can also initialize an organization, project, and API keys without using the Langfuse UI. Use `WithInitialProject` in place of `WithApiKeys`:

```csharp
var userEmail = builder.AddParameter("langfuse-user-email");
var userPassword = builder.AddParameter("langfuse-user-password", secret: true);

langfuse.WithInitialProject(publicKey, secretKey,
        organizationId: "my-org", projectId: "my-project")
    .WithEnvironment("LANGFUSE_INIT_USER_EMAIL", userEmail)
    .WithEnvironment("LANGFUSE_INIT_USER_PASSWORD", userPassword);
```

Supply the email and password through AppHost configuration or user secrets, as with the API keys above. Initialization applies when the project and user are first created. Changing these values later does not rotate existing credentials. See [Langfuse's headless initialization guide](https://langfuse.com/self-hosting/administration/headless-initialization).

## Use an existing Langfuse instance

Use `AddLangfuseEndpoint` for Langfuse Cloud or a self-hosted instance:

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var publicKey = builder.AddParameter("langfuse-public-key");
var secretKey = builder.AddParameter("langfuse-secret-key", secret: true);

var langfuse = builder.AddLangfuseEndpoint("langfuse", new Uri("https://cloud.langfuse.com"))
    .WithApiKeys(publicKey, secretKey);

builder.AddProject<Projects.Api>("api")
    .WithReference(langfuse);

builder.Build().Run();
```

Use the base URL for your Langfuse instance or cloud region. This passes the URL and keys to your application without starting any Langfuse containers. The endpoint resource does not check the remote service's health.

For a URL supplied through configuration, use an Aspire parameter:

```csharp
using Aspire.Hosting.ApplicationModel;

var url = builder.AddParameter("langfuse-url");
var langfuse = builder.AddLangfuseEndpoint("langfuse",
    ReferenceExpression.Create($"{url.Resource}"));
```

## Reuse services in your AppHost

Supply resources in the configuration callback to reuse them. Langfuse creates any dependencies you leave out.

```csharp
using CommunityToolkit.Aspire.Hosting.Langfuse;

var database = builder.AddPostgres("postgres").WithDataVolume()
    .AddDatabase("langfuse-db", "langfuse");
var redis = builder.AddRedis("redis")
    .WithArgs("--maxmemory-policy", "noeviction")
    .WithDataVolume();
var blobs = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator
        .WithArgs("--disableProductStyleUrl")
        .WithDataVolume())
    .AddBlobs("blobs");

var langfuse = builder.AddLangfuse("langfuse", options =>
{
    options.Postgres = database;
    options.Redis = redis;
    options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(blobs);
    options.UseDataVolumes = true;
});
```

Give Langfuse its own PostgreSQL database. Redis needs the `noeviction` policy for Langfuse's queues. The Azure Blob resource overload uses Aspire's storage emulator and creates the required blob container for you.

`UseDataVolumes` applies to dependencies created by this integration. Set up persistence on any resources you supply yourself, as shown above.

<details>
<summary>Use managed databases, ClickHouse, and blob storage</summary>

For services outside your AppHost, provide connection URLs and storage credentials:

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

Set `ConnectionStrings:langfuse-postgres-url` to a `postgresql://` URI and `ConnectionStrings:langfuse-redis-url` to a `redis://` or `rediss://` URI. These must be URLs the Langfuse containers can reach; `localhost` inside a container refers to that container.

ClickHouse needs both an HTTP endpoint and a native migration endpoint. Set `Database`, `ClusterEnabled`, and `ClusterName` on `LangfuseClickHouseSettings` to match your deployment.

For managed Azure Blob Storage, create the container beforehand and supply an account key with read, write, and delete permissions. The account must allow shared-key access.

For S3-compatible storage, set `options.BlobStorage` with `LangfuseBlobStorageSettings.S3(endpoint, accessKeyId, secretAccessKey, bucketName)`. The endpoint and credentials are `ReferenceExpression` values, as in the Azure example. Create the bucket beforehand; use the settings' `Region` and `ForcePathStyle` properties for your provider. Set `PublicEndpoint` when browsers and SDK clients need a different URL from the one used by the containers. See [Langfuse's blob storage guide](https://langfuse.com/self-hosting/deployment/infrastructure/blobstorage).

External services are not checked for readiness automatically. Set the ClickHouse or blob storage settings' `Resource` property if an existing Aspire resource should be awaited.

</details>

## Configuration and deployment

The `AddLangfuse` callback accepts these common settings:

| Setting | Purpose |
| --- | --- |
| `UseDataVolumes` | Keep data in named volumes across local runs. Defaults to `false`. |
| `ImageTag` | Langfuse image version, applied to both web and worker. Defaults to `4`. |
| `PublicUrl` | Public Langfuse URL when running behind a hostname or reverse proxy. |
| `NextAuthSecret`, `Salt`, `EncryptionKey` | Supply your own Aspire secret parameters instead of generating local values. |

Keep the salt and encryption key stable when reusing existing data. The encryption key must be 64 hexadecimal characters.

For further Aspire configuration, access the resources through `langfuse.Resource.Worker`, `.Postgres`, `.Redis`, `.ClickHouse`, and `.BlobStorage`. Calling `WithEnvironment` on `langfuse` configures the web container; apply settings needed by the worker to `langfuse.Resource.Worker` too.

When deploying with Aspire, configure managed blob storage, set `PublicUrl`, and supply secrets through your deployment environment. The default storage emulator is for local use. Choose image versions, networking, TLS, persistence, and backups for your deployment using [Langfuse's self-hosting documentation](https://langfuse.com/self-hosting).

## Try the example

The repository includes an [AppHost](https://github.com/EwanT-21/aspire-langfuse/tree/main/examples/Langfuse.AppHost) and a chat application using Microsoft Agent Framework and Ollama. The agent checks Langfuse's health and sends its agent, model, and tool traces to Langfuse through OTLP.

From a checkout of this repository, run:

```bash
dotnet run --project examples/Langfuse.AppHost -- --Agent:UseGpu=false
```

The first run downloads the `qwen3.5:2b` model. Open the `agent` endpoint in the Aspire dashboard to chat, then open `langfuse` to view the traces. The example records prompts, tool results, and responses.

Sign in to Langfuse as `agent@example.test`. Find the generated `Parameters:langfuse-user-password` value with:

```bash
dotnet user-secrets list --project examples/Langfuse.AppHost
```

The example saves its credentials in user secrets and keeps its data in Docker volumes. An existing database keeps its original login credentials.

To use an NVIDIA GPU, omit `--Agent:UseGpu=false` and make sure Docker has GPU access. Other options:

| Argument | Effect |
| --- | --- |
| `--Agent:Model=qwen3.5:0.8b` | Use a different Ollama model. |
| `--Agent:Enabled=false` | Run the Langfuse stack without the agent or Ollama. |
| `--Langfuse:Mode=shared` | Reuse PostgreSQL, Redis, and blob storage resources from the AppHost. |
| `--Langfuse:Mode=existing` | Connect to an existing Langfuse instance while running the agent and Ollama locally. |

For `existing` mode, set `Parameters:langfuse-url`, `Parameters:langfuse-public-key`, and `Parameters:langfuse-secret-key` in the example AppHost's user secrets.

## Development

```bash
dotnet build Aspire.Langfuse.slnx
dotnet test tests/Aspire.Langfuse.Tests.csproj
dotnet pack src/Aspire.Langfuse.csproj -c Release
```

Unit tests run without Docker. To check a running stack, the Docker smoke tests initialize a project, send a trace, and check that Langfuse stored it:

```bash
dotnet run --project tests/SmokeTests
dotnet run --project tests/SmokeTests -- --shared=true
dotnet run --project tests/SmokeTests -- --agent=true --gpu=false
```

Found a bug or have a suggestion? [Open an issue](https://github.com/EwanT-21/aspire-langfuse/issues).

## License

[MIT](https://github.com/EwanT-21/aspire-langfuse/blob/main/LICENSE).
