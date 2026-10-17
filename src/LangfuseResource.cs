using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Langfuse;

/// <summary>The Langfuse web/API container and its associated worker and dependencies.</summary>
/// <param name="name">The resource name.</param>
public sealed class LangfuseResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    /// <summary>The web/API endpoint, resolved for the consuming resource's network.</summary>
    public EndpointReference HttpEndpoint => new(this, "http");

    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{HttpEndpoint}");

    /// <summary>The worker container. Available after AddLangfuse returns.</summary>
    public IResourceBuilder<ContainerResource> Worker { get; internal set; } = null!;

    /// <summary>The PostgreSQL resource used by the stack.</summary>
    public IResourceBuilder<IResource> Postgres { get; internal set; } = null!;

    /// <summary>The Redis resource used by the stack.</summary>
    public IResourceBuilder<IResource> Redis { get; internal set; } = null!;

    /// <summary>The ClickHouse resource, if represented in the Aspire model.</summary>
    public IResourceBuilder<IResource>? ClickHouse { get; internal set; }

    /// <summary>The blob storage resource, if represented in the Aspire model.</summary>
    public IResourceBuilder<IResource>? BlobStorage { get; internal set; }

    internal IResourceBuilder<ParameterResource>? PublicKey { get; set; }
    internal IResourceBuilder<ParameterResource>? SecretKey { get; set; }
}
