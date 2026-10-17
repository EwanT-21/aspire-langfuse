using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using CommunityToolkit.Aspire.Hosting.Langfuse;

namespace Aspire.Hosting;

/// <summary>Builds and references Langfuse resources in an Aspire AppHost.</summary>
public static class LangfuseExtensions
{
    /// <summary>Adds Langfuse web and worker containers with replaceable PostgreSQL, Redis, ClickHouse, and blob storage.</summary>
    /// <remarks>Dependencies must be supplied in configure before defaults are created. Generated local secrets persist in AppHost user secrets.</remarks>
    public static IResourceBuilder<LangfuseResource> AddLangfuse(this IDistributedApplicationBuilder builder,
        string name, Action<LangfuseSettings>? configure = null, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var settings = new LangfuseSettings();
        configure?.Invoke(settings);
        Validate(settings, port);
        if (builder.ExecutionContext.IsPublishMode && settings.BlobStorage is null)
        {
            throw new InvalidOperationException("Publishing Langfuse requires BlobStorage configured with managed storage credentials. The default Aspire blob emulator is for local runs.");
        }

        var web = builder.AddResource(new LangfuseResource(name))
            .WithImage("langfuse/langfuse", settings.ImageTag)
            .WithImageRegistry("docker.langfuse.com")
            .WithHttpEndpoint(port: port, targetPort: 3000, name: "http")
            .WithHttpHealthCheck("/api/public/health");

        var worker = builder.AddContainer($"{name}-worker", "langfuse/langfuse-worker", settings.ImageTag)
            .WithImageRegistry("docker.langfuse.com")
            .WithHttpEndpoint(targetPort: 3030, name: "http")
            .WithHttpHealthCheck("/api/health")
            .WithParentRelationship(web)
            .WaitFor(web);
        web.Resource.Worker = worker;

        var authSecret = settings.NextAuthSecret ?? AddSecret(builder, $"{name}-nextauth-secret");
        var salt = settings.Salt ?? AddSecret(builder, $"{name}-salt");
        var encryptionKey = settings.EncryptionKey ?? AddSecret(builder, $"{name}-encryption-key");
        var publicUrl = settings.PublicUrl ?? ReferenceExpression.Create($"{PublicEndpoint(web, "http")}");

        foreach (IResourceBuilder<ContainerResource> container in new IResourceBuilder<ContainerResource>[] { web, worker })
        {
            container.WithEnvironment("NEXTAUTH_URL", publicUrl)
                .WithEnvironment("NEXTAUTH_SECRET", authSecret)
                .WithEnvironment("SALT", salt)
                .WithEnvironment("ENCRYPTION_KEY", encryptionKey)
                .WithEnvironment("TELEMETRY_ENABLED", "false");
        }

        ConfigurePostgres(builder, web, settings);
        ConfigureRedis(builder, web, settings);
        ConfigureClickHouse(builder, web, settings);
        ConfigureBlobStorage(builder, web, settings);
        return web;
    }

    /// <summary>References an existing Langfuse host without creating any containers or backing services.</summary>
    public static IResourceBuilder<LangfuseEndpointResource> AddLangfuseEndpoint(this IDistributedApplicationBuilder builder,
        string name, Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != "https" && url.Scheme != "http") ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            throw new ArgumentException("Use an absolute HTTP(S) base URL without credentials, query, or fragment.", nameof(url));
        }

        return builder.AddLangfuseEndpoint(name, ReferenceExpression.Create($"{url.AbsoluteUri.TrimEnd('/')}"));
    }

    /// <summary>References an existing Langfuse host using an expression, for example an Aspire URL parameter.</summary>
    public static IResourceBuilder<LangfuseEndpointResource> AddLangfuseEndpoint(this IDistributedApplicationBuilder builder,
        string name, ReferenceExpression url)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(url);
        var endpoint = builder.AddResource(new LangfuseEndpointResource(name, url));
        return endpoint.WithManifestPublishingCallback(context =>
        {
            context.Writer.WriteString("type", "value.v0");
            context.Writer.WriteString("value", url.ValueExpression);
            context.WriteConnectionString(endpoint.Resource);
        });
    }

    /// <summary>Sets existing project API keys to pass to applications that reference this Langfuse stack.</summary>
    /// <remarks>This does not create keys in Langfuse. Use WithInitialProject to provision a project during first startup.</remarks>
    public static IResourceBuilder<LangfuseResource> WithApiKeys(this IResourceBuilder<LangfuseResource> builder,
        IResourceBuilder<ParameterResource> publicKey, IResourceBuilder<ParameterResource> secretKey)
    {
        ValidateKeys(builder, publicKey, secretKey);
        builder.Resource.PublicKey = publicKey;
        builder.Resource.SecretKey = secretKey;
        return builder;
    }

    /// <summary>Sets existing project API keys to pass to applications that reference this external endpoint.</summary>
    public static IResourceBuilder<LangfuseEndpointResource> WithApiKeys(this IResourceBuilder<LangfuseEndpointResource> builder,
        IResourceBuilder<ParameterResource> publicKey, IResourceBuilder<ParameterResource> secretKey)
    {
        ValidateKeys(builder, publicKey, secretKey);
        builder.Resource.PublicKey = publicKey;
        builder.Resource.SecretKey = secretKey;
        return builder;
    }

    /// <summary>Configures Langfuse headless organization/project initialization and passes the supplied keys to consumers.</summary>
    /// <remarks>Initialization applies on first creation. To provision a user, also set LANGFUSE_INIT_USER_EMAIL and LANGFUSE_INIT_USER_PASSWORD on the web resource.</remarks>
    public static IResourceBuilder<LangfuseResource> WithInitialProject(this IResourceBuilder<LangfuseResource> builder,
        IResourceBuilder<ParameterResource> publicKey, IResourceBuilder<ParameterResource> secretKey,
        string organizationId = "aspire", string projectId = "aspire")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return builder.WithApiKeys(publicKey, secretKey)
            .WithEnvironment("LANGFUSE_INIT_ORG_ID", organizationId)
            .WithEnvironment("LANGFUSE_INIT_ORG_NAME", organizationId)
            .WithEnvironment("LANGFUSE_INIT_PROJECT_ID", projectId)
            .WithEnvironment("LANGFUSE_INIT_PROJECT_NAME", projectId)
            .WithEnvironment("LANGFUSE_INIT_PROJECT_PUBLIC_KEY", publicKey)
            .WithEnvironment("LANGFUSE_INIT_PROJECT_SECRET_KEY", secretKey);
    }

    /// <summary>Injects ConnectionStrings__name and LANGFUSE_BASE_URL, plus API keys when configured.</summary>
    public static IResourceBuilder<T> WithReference<T>(this IResourceBuilder<T> builder,
        IResourceBuilder<LangfuseResource> langfuse) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(langfuse);
        ResourceBuilderExtensions.WithReference(builder, langfuse);
        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["LANGFUSE_BASE_URL"] = langfuse.Resource.ConnectionStringExpression;
            AddKeys(context, langfuse.Resource.PublicKey, langfuse.Resource.SecretKey);
        });
    }

    /// <summary>Injects ConnectionStrings__name and LANGFUSE_BASE_URL, plus API keys when configured.</summary>
    public static IResourceBuilder<T> WithReference<T>(this IResourceBuilder<T> builder,
        IResourceBuilder<LangfuseEndpointResource> langfuse) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(langfuse);
        ResourceBuilderExtensions.WithReference(builder, langfuse);
        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["LANGFUSE_BASE_URL"] = langfuse.Resource.ConnectionStringExpression;
            AddKeys(context, langfuse.Resource.PublicKey, langfuse.Resource.SecretKey);
        });
    }

    private static void ConfigurePostgres(IDistributedApplicationBuilder builder,
        IResourceBuilder<LangfuseResource> web, LangfuseSettings settings)
    {
        if (settings.DatabaseUrl is { } external)
        {
            web.Resource.Postgres = external;
            ForBoth(web, c => c.WithEnvironment("DATABASE_URL", external.Resource.ConnectionStringExpression));
            return;
        }

        var database = settings.Postgres;
        if (database is null)
        {
            var server = builder.AddPostgres($"{web.Resource.Name}-postgres")
                .WithImageTag("17")
                .WithParentRelationship(web);
            if (settings.UseDataVolumes)
            {
                server.WithDataVolume();
            }

            database = server.AddDatabase($"{web.Resource.Name}-db", "langfuse");
        }

        web.Resource.Postgres = database;
        ForBoth(web, c => c.WithEnvironment("DATABASE_URL", database.Resource.UriExpression).WaitFor(database));
    }

    private static void ConfigureRedis(IDistributedApplicationBuilder builder,
        IResourceBuilder<LangfuseResource> web, LangfuseSettings settings)
    {
        if (settings.RedisUrl is { } external)
        {
            web.Resource.Redis = external;
            ForBoth(web, c => c.WithEnvironment("REDIS_CONNECTION_STRING", external.Resource.ConnectionStringExpression));
            return;
        }

        var redis = settings.Redis;
        if (redis is null)
        {
            redis = builder.AddRedis($"{web.Resource.Name}-redis")
                .WithImageTag("7")
                .WithArgs("--maxmemory-policy", "noeviction")
                .WithParentRelationship(web);
            if (settings.UseDataVolumes)
            {
                redis.WithDataVolume();
            }
        }

        web.Resource.Redis = redis;
        ForBoth(web, c => c.WithEnvironment("REDIS_CONNECTION_STRING", redis.Resource.UriExpression).WaitFor(redis));
    }

    private static void ConfigureClickHouse(IDistributedApplicationBuilder builder,
        IResourceBuilder<LangfuseResource> web, LangfuseSettings settings)
    {
        var clickHouse = settings.ClickHouse;
        if (clickHouse is null)
        {
            var password = AddSecret(builder, $"{web.Resource.Name}-clickhouse-password");
            var server = builder.AddContainer($"{web.Resource.Name}-clickhouse", "clickhouse/clickhouse-server", "25.12")
                .WithHttpEndpoint(targetPort: 8123, name: "http")
                .WithEndpoint(targetPort: 9000, scheme: "tcp", name: "native")
                .WithEnvironment("CLICKHOUSE_DB", "default")
                .WithEnvironment("CLICKHOUSE_USER", "langfuse")
                .WithEnvironment("CLICKHOUSE_PASSWORD", password)
                .WithHttpHealthCheck("/ping")
                .WithParentRelationship(web);
            if (settings.UseDataVolumes)
            {
                server.WithVolume($"{web.Resource.Name}-clickhouse-data", "/var/lib/clickhouse");
            }

            clickHouse = new LangfuseClickHouseSettings(
                ReferenceExpression.Create($"{server.GetEndpoint("http")}"),
                ReferenceExpression.Create($"clickhouse://{server.GetEndpoint("native").Property(EndpointProperty.HostAndPort)}"),
                ReferenceExpression.Create($"langfuse"), ReferenceExpression.Create($"{password.Resource}"))
            {
                Resource = server
            };
        }

        web.Resource.ClickHouse = clickHouse.Resource;
        ForBoth(web, c =>
        {
            c.WithEnvironment("CLICKHOUSE_URL", clickHouse.HttpUrl)
                .WithEnvironment("CLICKHOUSE_MIGRATION_URL", clickHouse.MigrationUrl)
                .WithEnvironment("CLICKHOUSE_USER", clickHouse.Username)
                .WithEnvironment("CLICKHOUSE_PASSWORD", clickHouse.Password)
                .WithEnvironment("CLICKHOUSE_DB", clickHouse.Database)
                .WithEnvironment("CLICKHOUSE_CLUSTER_ENABLED", clickHouse.ClusterEnabled ? "true" : "false")
                .WithEnvironment("CLICKHOUSE_CLUSTER_NAME", clickHouse.ClusterName);
            if (clickHouse.Resource is { } resource)
            {
                c.WaitFor(resource);
            }
        });
    }

    private static void ConfigureBlobStorage(IDistributedApplicationBuilder builder,
        IResourceBuilder<LangfuseResource> web, LangfuseSettings settings)
    {
        var storage = settings.BlobStorage;
        if (storage is null)
        {
            var blobs = builder.AddAzureStorage($"{web.Resource.Name}-storage")
                .RunAsEmulator(emulator =>
                {
                    emulator.WithArgs("--disableProductStyleUrl");
                    if (settings.UseDataVolumes)
                    {
                        emulator.WithDataVolume();
                    }
                })
                .WithParentRelationship(web)
                .AddBlobs($"{web.Resource.Name}-blobs");
            storage = LangfuseBlobStorageSettings.AzureBlob(blobs);
        }

        web.Resource.BlobStorage = storage.Resource;
        ForBoth(web, c =>
        {
            c.WithEnvironment("LANGFUSE_USE_AZURE_BLOB", storage.UseAzureBlob ? "true" : "false")
                .WithEnvironment("LANGFUSE_S3_BATCH_EXPORT_ENABLED", "true");
            foreach (var (use, prefix) in new[] { ("EVENT_UPLOAD", "events/"), ("MEDIA_UPLOAD", "media/"), ("BATCH_EXPORT", "exports/") })
            {
                var key = $"LANGFUSE_S3_{use}";
                c.WithEnvironment($"{key}_BUCKET", storage.BucketName)
                    .WithEnvironment($"{key}_REGION", storage.Region)
                    .WithEnvironment($"{key}_ACCESS_KEY_ID", storage.AccessKeyId)
                    .WithEnvironment($"{key}_SECRET_ACCESS_KEY", storage.SecretAccessKey)
                    .WithEnvironment($"{key}_FORCE_PATH_STYLE", storage.ForcePathStyle ? "true" : "false")
                    .WithEnvironment($"{key}_PREFIX", prefix)
                    .WithEnvironment($"{key}_ENDPOINT", use == "MEDIA_UPLOAD" ? storage.PublicEndpoint ?? storage.Endpoint : storage.Endpoint);
            }

            c.WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_INTERNAL_ENDPOINT", storage.Endpoint)
                .WithEnvironment("LANGFUSE_S3_BATCH_EXPORT_EXTERNAL_ENDPOINT", storage.PublicEndpoint ?? storage.Endpoint);
            if (storage.Resource is { } resource)
            {
                c.WaitFor(resource);
            }
        });
    }

    private static void ForBoth(IResourceBuilder<LangfuseResource> web, Action<IResourceBuilder<ContainerResource>> configure)
    {
        configure(web);
        configure(web.Resource.Worker);
    }

    private static EndpointReference PublicEndpoint<T>(IResourceBuilder<T> resource, string endpointName)
        where T : IResourceWithEndpoints => resource.ApplicationBuilder.ExecutionContext.IsRunMode
            ? resource.GetEndpoint(endpointName, KnownNetworkIdentifiers.LocalhostNetwork)
            : resource.GetEndpoint(endpointName);

    private static IResourceBuilder<ParameterResource> AddSecret(IDistributedApplicationBuilder builder, string name) =>
        builder.ExecutionContext.IsRunMode
            ? builder.AddParameter(name, new HexSecretDefault(), secret: true, persist: true)
            : builder.AddParameter(name, secret: true);

    private static void AddKeys(EnvironmentCallbackContext context, IResourceBuilder<ParameterResource>? publicKey,
        IResourceBuilder<ParameterResource>? secretKey)
    {
        if (publicKey is not null && secretKey is not null)
        {
            context.EnvironmentVariables["LANGFUSE_PUBLIC_KEY"] = publicKey.Resource;
            context.EnvironmentVariables["LANGFUSE_SECRET_KEY"] = secretKey.Resource;
        }
    }

    private static void ValidateKeys<T>(IResourceBuilder<T> builder,
        IResourceBuilder<ParameterResource> publicKey, IResourceBuilder<ParameterResource> secretKey) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        if (!secretKey.Resource.Secret)
        {
            throw new ArgumentException("The secret API key must be an Aspire secret parameter.", nameof(secretKey));
        }
    }

    private static void Validate(LangfuseSettings settings, int? port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ImageTag);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (settings.Postgres is not null && settings.DatabaseUrl is not null)
        {
            throw new ArgumentException("Supply either Postgres or DatabaseUrl.", nameof(settings));
        }

        if (settings.Redis is not null && settings.RedisUrl is not null)
        {
            throw new ArgumentException("Supply either Redis or RedisUrl.", nameof(settings));
        }

        foreach (var secret in new[] { settings.NextAuthSecret, settings.Salt, settings.EncryptionKey })
        {
            if (secret is not null && !secret.Resource.Secret)
            {
                throw new ArgumentException("Langfuse secrets must be Aspire secret parameters.", nameof(settings));
            }
        }
    }

    private sealed class HexSecretDefault : ParameterDefault
    {
        public override string GetDefaultValue() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        // Published stacks require secret inputs; generated development values are never exported.
        public override void WriteToManifest(ManifestPublishingContext context) { }
    }
}

