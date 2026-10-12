using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AccountOpeningTracker.Api.Security;

/// <summary>
/// Requires the signed-in, active user to hold at least one of the listed
/// permissions. Evaluated on the server against the database.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string Prefix = "perm:";

    public HasPermissionAttribute(params string[] anyOf)
    {
        Policy = Prefix + string.Join("|", anyOf);
    }
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(IReadOnlyList<string> anyOf) => AnyOf = anyOf;
    public IReadOnlyList<string> AnyOf { get; }
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IAccessService _access;

    public PermissionHandler(IAccessService access) => _access = access;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var profile = await _access.GetCurrentAsync();
        if (profile != null && profile.IsActive && requirement.AnyOf.Any(profile.Has))
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasPermissionAttribute.Prefix, StringComparison.Ordinal))
        {
            var codes = policyName[HasPermissionAttribute.Prefix.Length..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries);
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(codes))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
