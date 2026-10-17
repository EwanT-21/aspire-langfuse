using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Langfuse;

/// <summary>A reference to an existing self-hosted or cloud Langfuse endpoint.</summary>
/// <param name="name">The resource name.</param>
/// <param name="url">The base URL, optionally backed by an Aspire parameter.</param>
public sealed class LangfuseEndpointResource(string name, ReferenceExpression url) : Resource(name), IResourceWithConnectionString
{
    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression { get; } = url ?? throw new ArgumentNullException(nameof(url));

    internal IResourceBuilder<ParameterResource>? PublicKey { get; set; }
    internal IResourceBuilder<ParameterResource>? SecretKey { get; set; }
}
