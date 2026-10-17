using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Langfuse;

/// <summary>Controls which infrastructure is used by a hosted Langfuse stack.</summary>
/// <remarks>Configure dependencies here before defaults are created. Supplied resources remain caller-owned.</remarks>
public sealed class LangfuseSettings
{
    /// <summary>An existing Aspire PostgreSQL database. A dedicated database is created when omitted.</summary>
    public IResourceBuilder<PostgresDatabaseResource>? Postgres { get; set; }

    /// <summary>An external PostgreSQL URI resource, such as AddConnectionString. Must use postgresql:// syntax.</summary>
    public IResourceBuilder<IResourceWithConnectionString>? DatabaseUrl { get; set; }

    /// <summary>An existing Aspire Redis server. It must use the noeviction policy.</summary>
    public IResourceBuilder<RedisResource>? Redis { get; set; }

    /// <summary>An external redis:// or rediss:// URI resource. Aspire's .NET Redis connection string syntax is not accepted by Langfuse.</summary>
    public IResourceBuilder<IResourceWithConnectionString>? RedisUrl { get; set; }

    /// <summary>Existing ClickHouse connection settings. A local single-node container is created when omitted.</summary>
    public LangfuseClickHouseSettings? ClickHouse { get; set; }

    /// <summary>Existing Azure Blob or S3 storage. Local runs use Aspire Azure Storage with RunAsEmulator() when omitted. Required when publishing.</summary>
    public LangfuseBlobStorageSettings? BlobStorage { get; set; }

    /// <summary>The browser-visible Langfuse URL, required when publishing behind a public hostname or reverse proxy.</summary>
    public ReferenceExpression? PublicUrl { get; set; }

    /// <summary>The web and worker image tag. Both containers always use the same version.</summary>
    public string ImageTag { get; set; } = "4";

    /// <summary>Whether to attach named data volumes to infrastructure created by this integration.</summary>
    public bool UseDataVolumes { get; set; }

    /// <summary>The authentication secret. A persistent secret parameter is generated for local runs when omitted.</summary>
    public IResourceBuilder<ParameterResource>? NextAuthSecret { get; set; }

    /// <summary>The hashing salt. Keep this stable when reusing databases.</summary>
    public IResourceBuilder<ParameterResource>? Salt { get; set; }

    /// <summary>The encryption key, exactly 64 hexadecimal characters. Keep this stable when reusing databases.</summary>
    public IResourceBuilder<ParameterResource>? EncryptionKey { get; set; }
}
