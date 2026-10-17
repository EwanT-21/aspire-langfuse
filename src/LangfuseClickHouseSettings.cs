using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Langfuse;

/// <summary>Connection details for an existing ClickHouse deployment.</summary>
/// <param name="httpUrl">The HTTP(S) query endpoint, usually port 8123 or 8443.</param>
/// <param name="migrationUrl">The native migration URI, usually clickhouse://host:9000 or a TLS URI.</param>
/// <param name="username">The ClickHouse username.</param>
/// <param name="password">The ClickHouse password, normally a secret parameter expression.</param>
public sealed class LangfuseClickHouseSettings(
    ReferenceExpression httpUrl,
    ReferenceExpression migrationUrl,
    ReferenceExpression username,
    ReferenceExpression password)
{
    /// <summary>The HTTP(S) query endpoint.</summary>
    public ReferenceExpression HttpUrl { get; } = httpUrl ?? throw new ArgumentNullException(nameof(httpUrl));

    /// <summary>The native migration URI.</summary>
    public ReferenceExpression MigrationUrl { get; } = migrationUrl ?? throw new ArgumentNullException(nameof(migrationUrl));

    /// <summary>The username.</summary>
    public ReferenceExpression Username { get; } = username ?? throw new ArgumentNullException(nameof(username));

    /// <summary>The password expression.</summary>
    public ReferenceExpression Password { get; } = password ?? throw new ArgumentNullException(nameof(password));

    /// <summary>The database name. Langfuse migrations require privileges to create and alter tables.</summary>
    public string Database { get; init; } = "default";

    /// <summary>Whether migrations should use ClickHouse cluster syntax.</summary>
    public bool ClusterEnabled { get; init; }

    /// <summary>The cluster name when cluster mode is enabled.</summary>
    public string ClusterName { get; init; } = "default";

    /// <summary>An optional Aspire resource to wait for before starting web and worker.</summary>
    public IResourceBuilder<IResource>? Resource { get; init; }
}
