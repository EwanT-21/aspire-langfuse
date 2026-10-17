using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace CommunityToolkit.Aspire.Hosting.Langfuse;

/// <summary>Blob storage used for ingestion events, media, and batch exports.</summary>
/// <remarks>The three uses share a bucket/container with separate prefixes. Azure Blob uses Langfuse's native Azure implementation.</remarks>
public sealed record LangfuseBlobStorageSettings
{
    private LangfuseBlobStorageSettings(ReferenceExpression endpoint, ReferenceExpression accountName,
        ReferenceExpression accountKey, string bucketName, bool useAzureBlob)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(accountName);
        ArgumentNullException.ThrowIfNull(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        Endpoint = endpoint;
        AccessKeyId = accountName;
        SecretAccessKey = accountKey;
        BucketName = bucketName;
        UseAzureBlob = useAzureBlob;
    }

    /// <summary>The server-reachable endpoint.</summary>
    public ReferenceExpression Endpoint { get; }

    /// <summary>The Azure account name or S3 access key ID.</summary>
    public ReferenceExpression AccessKeyId { get; }

    /// <summary>The Azure account key or S3 secret access key.</summary>
    public ReferenceExpression SecretAccessKey { get; }

    /// <summary>The bucket or Azure container name.</summary>
    public string BucketName { get; }

    /// <summary>Whether to use the native Azure Blob implementation.</summary>
    public bool UseAzureBlob { get; }

    /// <summary>The S3 region. Ignored by Azure Blob.</summary>
    public string Region { get; init; } = "auto";

    /// <summary>Whether S3 should use path-style requests.</summary>
    public bool ForcePathStyle { get; init; } = true;

    /// <summary>A browser- and SDK-reachable URL for signed media and export URLs. Defaults to Endpoint.</summary>
    public ReferenceExpression? PublicEndpoint { get; init; }

    /// <summary>An optional Aspire resource to wait for before starting web and worker.</summary>
    public IResourceBuilder<IResource>? Resource { get; init; }

    /// <summary>Configures an existing S3-compatible bucket. The bucket must already exist.</summary>
    public static LangfuseBlobStorageSettings S3(ReferenceExpression endpoint, ReferenceExpression accessKeyId,
        ReferenceExpression secretAccessKey, string bucketName = "langfuse") =>
        new(endpoint, accessKeyId, secretAccessKey, bucketName, useAzureBlob: false);

    /// <summary>Configures native Azure Blob storage. The container must exist before ingestion starts.</summary>
    public static LangfuseBlobStorageSettings AzureBlob(ReferenceExpression endpoint, ReferenceExpression accountName,
        ReferenceExpression accountKey, string containerName = "langfuse") =>
        new(endpoint, accountName, accountKey, containerName, useAzureBlob: true);

    /// <summary>Reuses an Aspire Azure Blob resource configured with RunAsEmulator().</summary>
    /// <remarks>Registers or reuses an Aspire blob container and waits for it to be ready. Emulator credentials are public development credentials.</remarks>
    public static LangfuseBlobStorageSettings AzureBlob(IResourceBuilder<AzureBlobStorageResource> blobs,
        string containerName = "langfuse")
    {
        ArgumentNullException.ThrowIfNull(blobs);
        if (!blobs.Resource.Parent.IsEmulator)
        {
            throw new ArgumentException("The storage resource must use RunAsEmulator(). For Azure, use AzureBlob() with account credentials.", nameof(blobs));
        }

        var existing = blobs.ApplicationBuilder.Resources.OfType<AzureBlobStorageContainerResource>()
            .FirstOrDefault(container => ReferenceEquals(container.Parent.Parent, blobs.Resource.Parent) && container.BlobContainerName == containerName);
        var container = existing is null
            ? blobs.ApplicationBuilder.CreateResourceBuilder(blobs.Resource.Parent)
                .AddBlobContainer($"{blobs.Resource.Name}-{containerName}", containerName)
            : blobs.ApplicationBuilder.CreateResourceBuilder(existing);
        var endpoint = blobs.Resource.Parent.GetEndpoint("blob");
        var publicEndpoint = blobs.Resource.Parent.GetEndpoint("blob", KnownNetworkIdentifiers.LocalhostNetwork);
        return new LangfuseBlobStorageSettings(
            ReferenceExpression.Create($"{endpoint}/devstoreaccount1"),
            ReferenceExpression.Create($"devstoreaccount1"),
            ReferenceExpression.Create($"Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="),
            containerName, useAzureBlob: true)
        {
            PublicEndpoint = ReferenceExpression.Create($"{publicEndpoint}/devstoreaccount1"),
            Resource = container
        };
    }

    /// <summary>Reuses an Aspire blob container whose storage account is configured with RunAsEmulator().</summary>
    public static LangfuseBlobStorageSettings AzureBlob(IResourceBuilder<AzureBlobStorageContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return AzureBlob(container.ApplicationBuilder.CreateResourceBuilder(container.Resource.Parent), container.Resource.BlobContainerName);
    }
}
