namespace Cortex.Application.Authorization;

/// <summary>
/// Where a user's effective authorization comes from, bound from the "Auth" section (alongside the
/// OIDC settings). Two modes:
/// <list type="bullet">
///   <item><b>Database</b> (default) — token roles AND internal assignments merge: DB role
///   assignments, per-user grants, and token claims all contribute (the admin console manages the
///   internal parts).</item>
///   <item><b>Token</b> — the external IdP (Entra External ID / B2C app roles) is the single source
///   of truth: DB role assignments and per-user grants are ignored, JIT provisioning never invents
///   a default role, and the admin endpoints that would edit them are disabled. The tenant's
///   role → permission <em>baselines</em> remain in force — they are what translate an IdP role
///   name into Cortex's fine-grained tool permissions, which no IdP knows about.</item>
/// </list>
/// </summary>
public sealed class AuthorizationSourceOptions
{
    public const string SectionName = "Auth";

    /// <summary>"Database" (default) or "Token".</summary>
    public string PermissionSource { get; set; } = "Database";

    /// <summary>True when the external IdP's token is the only source of roles and grants.</summary>
    public bool IsTokenSourced => string.Equals(PermissionSource, "Token", StringComparison.OrdinalIgnoreCase);

    public void ThrowIfInvalid()
    {
        if (!IsTokenSourced && !string.Equals(PermissionSource, "Database", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Auth:PermissionSource '{PermissionSource}' is not supported. Use \"Database\" (internal RBAC + token) or \"Token\" (external IdP only).");
        }
    }
}
