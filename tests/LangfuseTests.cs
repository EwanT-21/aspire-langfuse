using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Azure;
using CommunityToolkit.Aspire.Hosting.Langfuse;
using Xunit;

namespace Aspire.Langfuse.Tests;

public sealed class LangfuseTests
{
    [Fact]
    public async Task DefaultsCreateCompleteStackWithSharedConfigurationAndWaits()
    {
        var builder = CreateBuilder();
        var langfuse = builder.AddLangfuse("langfuse");
        using var app = builder.Build();

        Assert.Equal(6, builder.Resources.Count(resource => resource.IsContainer()));
        Assert.Equal("langfuse-worker", langfuse.Resource.Worker.Resource.Name);
        Assert.Contains(langfuse.Resource.Worker.Resource.Annotations.OfType<WaitAnnotation>(),
            wait => ReferenceEquals(wait.Resource, langfuse.Resource));
        Assert.Equal(3000, langfuse.Resource.HttpEndpoint.TargetPort);
        Assert.Equal("docker.langfuse.com/langfuse/langfuse:4", Image(langfuse.Resource));
        Assert.Equal("docker.langfuse.com/langfuse/langfuse-worker:4", Image(langfuse.Resource.Worker.Resource));

        var web = await Environment(builder, langfuse.Resource);
        var worker = await Environment(builder, langfuse.Resource.Worker.Resource);
        foreach (var key in new[] { "DATABASE_URL", "REDIS_CONNECTION_STRING", "SALT", "ENCRYPTION_KEY", "NEXTAUTH_SECRET",
                     "CLICKHOUSE_URL", "CLICKHOUSE_MIGRATION_URL", "CLICKHOUSE_PASSWORD", "LANGFUSE_S3_EVENT_UPLOAD_ENDPOINT" })
        {
            Assert.Equal(Expression(web[key]), Expression(worker[key]));
        }

        Assert.StartsWith("postgresql://", Expression(web["DATABASE_URL"]));
        AllocateEndpoints(builder);
        Assert.StartsWith("redis://", await Value(web["REDIS_CONNECTION_STRING"]));
        Assert.Equal("false", web["CLICKHOUSE_CLUSTER_ENABLED"]);
        Assert.Equal("true", web["LANGFUSE_USE_AZURE_BLOB"]);
        Assert.Equal("events/", web["LANGFUSE_S3_EVENT_UPLOAD_PREFIX"]);
        Assert.Equal("media/", web["LANGFUSE_S3_MEDIA_UPLOAD_PREFIX"]);
        Assert.Equal("exports/", web["LANGFUSE_S3_BATCH_EXPORT_PREFIX"]);
        Assert.Equal("true", web["LANGFUSE_S3_BATCH_EXPORT_ENABLED"]);

        foreach (var resource in new IResource[] { langfuse.Resource, langfuse.Resource.Worker.Resource })
        {
            var waits = resource.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource.Name).ToArray();
            Assert.Contains("langfuse-db", waits);
            Assert.Contains("langfuse-redis", waits);
            Assert.Contains("langfuse-clickhouse", waits);
            Assert.Contains("langfuse-blobs", waits);
            Assert.Contains("langfuse-blobs-langfuse", waits);
            Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        }

        foreach (var suffix in new[] { "nextauth-secret", "salt", "encryption-key", "clickhouse-password" })
        {
            Assert.True(builder.Resources.OfType<ParameterResource>().Single(p => p.Name == $"langfuse-{suffix}").Secret);
        }

        var encryptionKey = await Value(web["ENCRYPTION_KEY"]);
        Assert.Matches("^[0-9a-f]{64}$", encryptionKey);
        Assert.Equal(encryptionKey, await Value(worker["ENCRYPTION_KEY"]));
        Assert.NotEqual(encryptionKey, await Value(web["SALT"]));
    }

    [Fact]
    public async Task SuppliedAspireDependenciesUseUrisAndAreNotDuplicatedOrReparented()
    {
        var builder = CreateBuilder();
        var user = builder.AddParameter("user", "user@name");
        var password = builder.AddParameter("password", "pass@word:/?#%", secret: true);
        var database = builder.AddPostgres("existing-postgres", userName: user, password: password)
            .AddDatabase("existing-db", "my/database");
        var redis = builder.AddRedis("existing-redis", password: password);
        var langfuse = builder.AddLangfuse("langfuse", options =>
        {
            options.Postgres = database;
            options.Redis = redis;
        });
        using var app = builder.Build();

        Assert.Same(database, langfuse.Resource.Postgres);
        Assert.Same(redis, langfuse.Resource.Redis);
        Assert.DoesNotContain(builder.Resources, r => r.Name is "langfuse-postgres" or "langfuse-db" or "langfuse-redis");
        Assert.Empty(database.Resource.Parent.Annotations.OfType<ResourceRelationshipAnnotation>());
        Assert.Empty(redis.Resource.Annotations.OfType<ResourceRelationshipAnnotation>());

        AllocateEndpoints(builder);
        var env = await Environment(builder, langfuse.Resource);
        var postgresUri = await Value(env["DATABASE_URL"]);
        Assert.Contains("user%40name:pass%40word%3A%2F%3F%23%25@", postgresUri);
        Assert.EndsWith("/my%2Fdatabase", postgresUri);
        Assert.Contains(":pass%40word%3A%2F%3F%23%25@", await Value(env["REDIS_CONNECTION_STRING"]));
    }

    [Fact]
    public async Task ExternalDependenciesCreateOnlyWebAndWorker()
    {
        var builder = CreateBuilder();
        var postgres = builder.AddConnectionString("existing-db");
        var redis = builder.AddConnectionString("existing-redis");
        var clickHousePassword = builder.AddParameter("ch-password", secret: true);
        var accountKey = builder.AddParameter("account-key", secret: true);
        var langfuse = builder.AddLangfuse("langfuse", options =>
        {
            options.DatabaseUrl = postgres;
            options.RedisUrl = redis;
            options.ClickHouse = new LangfuseClickHouseSettings(
                ReferenceExpression.Create($"https://clickhouse.example:8443"),
                ReferenceExpression.Create($"clickhouse://clickhouse.example:9440?secure=true"),
                ReferenceExpression.Create($"default"), ReferenceExpression.Create($"{clickHousePassword.Resource}"))
            {
                Database = "observability",
                ClusterEnabled = true,
                ClusterName = "analytics"
            };
            options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(
                ReferenceExpression.Create($"https://storage.blob.core.windows.net"),
                ReferenceExpression.Create($"storage"), ReferenceExpression.Create($"{accountKey.Resource}"));
        });
        using var app = builder.Build();

        Assert.Equal(2, builder.Resources.OfType<ContainerResource>().Count());
        var env = await Environment(builder, langfuse.Resource);
        Assert.Equal("{existing-db.connectionString}", Expression(env["DATABASE_URL"]));
        Assert.Equal("{existing-redis.connectionString}", Expression(env["REDIS_CONNECTION_STRING"]));
        Assert.Equal("true", env["LANGFUSE_USE_AZURE_BLOB"]);
        Assert.Equal("observability", env["CLICKHOUSE_DB"]);
        Assert.Equal("true", env["CLICKHOUSE_CLUSTER_ENABLED"]);
        Assert.Equal("analytics", env["CLICKHOUSE_CLUSTER_NAME"]);
        Assert.Equal("{account-key.value}", Expression(env["LANGFUSE_S3_EVENT_UPLOAD_SECRET_ACCESS_KEY"]));
    }

    [Fact]
    public async Task AzuriteUsesNativeAzureAndAccountPathInBothNetworks()
    {
        var builder = CreateBuilder();
        var storage = builder.AddAzureStorage("storage")
            .RunAsEmulator(emulator => emulator.WithArgs("--disableProductStyleUrl"));
        var blobs = storage.AddBlobs("blobs");
        var container = storage.AddBlobContainer("existing-container", "langfuse");
        var langfuse = builder.AddLangfuse("langfuse", options =>
            options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(blobs));
        using var app = builder.Build();
        AllocateEndpoints(builder);

        var env = await Environment(builder, langfuse.Resource);
        Assert.Equal("true", env["LANGFUSE_USE_AZURE_BLOB"]);
        Assert.Same(container.Resource, langfuse.Resource.BlobStorage!.Resource);
        Assert.Single(builder.Resources.OfType<AzureBlobStorageContainerResource>());
        Assert.DoesNotContain(builder.Resources, r => r.Name == "langfuse-storage");
        var containerContext = new ValueProviderContext { Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork };
        Assert.StartsWith("http://storage:", await Value(env["LANGFUSE_S3_EVENT_UPLOAD_ENDPOINT"], containerContext));
        Assert.EndsWith("/devstoreaccount1", await Value(env["LANGFUSE_S3_EVENT_UPLOAD_ENDPOINT"], containerContext));
        Assert.StartsWith("http://localhost:", await Value(env["LANGFUSE_S3_MEDIA_UPLOAD_ENDPOINT"], containerContext));
    }

    [Fact]
    public async Task BrowserUrlsRemainOnHostWhileContainerConnectionsUseContainerNetwork()
    {
        var builder = CreateBuilder();
        var langfuse = builder.AddLangfuse("langfuse");
        using var app = builder.Build();
        AllocateEndpoints(builder);
        var env = await Environment(builder, langfuse.Resource);
        var containerContext = new ValueProviderContext { Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork };

        Assert.StartsWith("http://localhost:", await Value(env["NEXTAUTH_URL"], containerContext));
        Assert.StartsWith("http://localhost:", await Value(env["LANGFUSE_S3_MEDIA_UPLOAD_ENDPOINT"], containerContext));
        Assert.StartsWith("http://langfuse-storage:", await Value(env["LANGFUSE_S3_EVENT_UPLOAD_ENDPOINT"], containerContext));
        Assert.StartsWith("http://langfuse-storage:", await Value(env["LANGFUSE_S3_MEDIA_UPLOAD_INTERNAL_ENDPOINT"], containerContext));
    }

    [Fact]
    public async Task ExistingEndpointPassesSdkConfigurationAndCreatesNoInfrastructure()
    {
        var builder = CreateBuilder();
        var publicKey = builder.AddParameter("public-key", "pk-lf-example");
        var secretKey = builder.AddParameter("secret-key", "sk-lf-example", secret: true);
        var langfuse = builder.AddLangfuseEndpoint("langfuse", new Uri("https://cloud.langfuse.com/"));
        var client = builder.AddContainer("client", "example/client").WithReference(langfuse);
        // Reading keys during environment evaluation also supports later configuration.
        langfuse.WithApiKeys(publicKey, secretKey);
        using var app = builder.Build();

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        var env = await Environment(builder, client.Resource);
        Assert.Equal("https://cloud.langfuse.com", await Value(env["LANGFUSE_BASE_URL"]));
        Assert.Equal("pk-lf-example", await Value(env["LANGFUSE_PUBLIC_KEY"]));
        Assert.Equal("sk-lf-example", await Value(env["LANGFUSE_SECRET_KEY"]));
        Assert.Contains("ConnectionStrings__langfuse", env.Keys);

        var manifest = await Manifest(builder, langfuse.Resource);
        Assert.Equal("value.v0", manifest.GetProperty("type").GetString());
        Assert.Equal("https://cloud.langfuse.com", manifest.GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task InitialProjectUsesSameKeysAsReferencingApplications()
    {
        var builder = CreateBuilder();
        var publicKey = builder.AddParameter("public-key");
        var secretKey = builder.AddParameter("secret-key", secret: true);
        var langfuse = builder.AddLangfuse("langfuse").WithInitialProject(publicKey, secretKey);
        var client = builder.AddContainer("client", "example/client").WithReference(langfuse).WaitFor(langfuse);
        using var app = builder.Build();

        var web = await Environment(builder, langfuse.Resource);
        var env = await Environment(builder, client.Resource);
        Assert.Equal("aspire", web["LANGFUSE_INIT_ORG_ID"]);
        Assert.Equal("aspire", web["LANGFUSE_INIT_PROJECT_ID"]);
        Assert.Equal(Expression(web["LANGFUSE_INIT_PROJECT_PUBLIC_KEY"]), Expression(env["LANGFUSE_PUBLIC_KEY"]));
        Assert.Equal(Expression(web["LANGFUSE_INIT_PROJECT_SECRET_KEY"]), Expression(env["LANGFUSE_SECRET_KEY"]));
        Assert.Equal(langfuse.Resource.ConnectionStringExpression.ValueExpression, Expression(env["LANGFUSE_BASE_URL"]));
    }

    [Fact]
    public async Task PublishRequiresSecretInputsAndKeepsExpressionsUnresolved()
    {
        var builder = CreateBuilder(publish: true);
        var accountKey = builder.AddParameter("blob-account-key", secret: true);
        var langfuse = builder.AddLangfuse("langfuse", options =>
        {
            options.PublicUrl = ReferenceExpression.Create($"https://langfuse.example.com");
            options.BlobStorage = LangfuseBlobStorageSettings.AzureBlob(
                ReferenceExpression.Create($"https://storage.blob.core.windows.net"),
                ReferenceExpression.Create($"storage"), ReferenceExpression.Create($"{accountKey.Resource}"));
        });
        using var app = builder.Build();
        var manifest = await Manifest(builder, langfuse.Resource);
        var env = manifest.GetProperty("env");

        Assert.Equal("container.v0", manifest.GetProperty("type").GetString());
        Assert.Equal("https://langfuse.example.com", env.GetProperty("NEXTAUTH_URL").GetString());
        Assert.Equal("{langfuse-encryption-key.value}", env.GetProperty("ENCRYPTION_KEY").GetString());
        var key = builder.Resources.OfType<ParameterResource>().Single(p => p.Name == "langfuse-encryption-key");
        Assert.Null(key.Default);
        Assert.True(key.Secret);
    }

    [Fact]
    public void PublishingRequiresManagedStorageBeforeCreatingResources()
    {
        var builder = CreateBuilder(publish: true);
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddLangfuse("langfuse"));
        Assert.Contains("BlobStorage", exception.Message);
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void VolumesOnlyApplyToIntegrationOwnedDependencies()
    {
        var builder = CreateBuilder();
        var langfuse = builder.AddLangfuse("langfuse", options => options.UseDataVolumes = true);
        var mounts = builder.Resources.SelectMany(r => r.Annotations.OfType<ContainerMountAnnotation>()).ToArray();

        Assert.Equal(4, mounts.Length);
        Assert.Contains(mounts, m => m.Target == "/var/lib/clickhouse");
        Assert.Contains(mounts, m => m.Target == "/data");
        Assert.Empty(langfuse.Resource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Theory]
    [InlineData("ftp://langfuse.example.com")]
    [InlineData("https://user:password@langfuse.example.com")]
    [InlineData("https://langfuse.example.com?key=secret")]
    public void InvalidExternalUrlsAreRejected(string url)
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddLangfuseEndpoint("langfuse", new Uri(url)));
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void ConflictingOverridesFailBeforeCreatingResources()
    {
        var builder = CreateBuilder();
        var postgres = builder.AddPostgres("postgres").AddDatabase("db");
        var external = builder.AddConnectionString("external");
        var count = builder.Resources.Count;

        Assert.Throws<ArgumentException>(() => builder.AddLangfuse("langfuse", o =>
        {
            o.Postgres = postgres;
            o.DatabaseUrl = external;
        }));
        Assert.Equal(count, builder.Resources.Count);
    }

    private static IDistributedApplicationBuilder CreateBuilder(bool publish = false) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publish ? ["--publisher", "manifest", "--output-path", "/tmp/langfuse-test-manifest.json"] : [],
            AssemblyName = typeof(LangfuseTests).Assembly.FullName,
            DisableDashboard = true
        });

    private static async Task<Dictionary<string, object>> Environment(IDistributedApplicationBuilder builder, IResource resource)
    {
        var env = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, env);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return env;
    }

    private static string Image(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        return $"{image.Registry}/{image.Image}:{image.Tag}";
    }

    private static string Expression(object value) => value is IManifestExpressionProvider expression
        ? expression.ValueExpression : value.ToString()!;

    private static async Task<string> Value(object value, ValueProviderContext? context = null) => value is IValueProvider provider
        ? (await provider.GetValueAsync(context ?? new ValueProviderContext()))! : value.ToString()!;

    private static void AllocateEndpoints(IDistributedApplicationBuilder builder)
    {
        var port = 20000;
        foreach (var resource in builder.Resources.OfType<IResourceWithEndpoints>())
        {
            foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
            {
                endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.LocalhostNetwork,
                    new AllocatedEndpoint(endpoint, "localhost", port++));
                endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                    new AllocatedEndpoint(endpoint, resource.Name, endpoint.TargetPort ?? port++,
                        EndpointBindingMode.SingleAddress, targetPortExpression: null,
                        networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            }
        }
    }

    private static async Task<JsonElement> Manifest(IDistributedApplicationBuilder builder, IResource resource)
    {
        using var stream = new MemoryStream();
        await using var writer = new Utf8JsonWriter(stream);
        var context = new ManifestPublishingContext(builder.ExecutionContext, "/tmp/langfuse-test-manifest.json", writer);
        writer.WriteStartObject();
        if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var annotation))
        {
            await annotation!.Callback!(context);
        }
        else
        {
            await context.WriteContainerAsync(Assert.IsAssignableFrom<ContainerResource>(resource));
        }

        writer.WriteEndObject();
        await writer.FlushAsync();
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
