using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SutRules.Api.Rules;

/// <summary>
/// Degraded, not unhealthy, after a rejected reload: the previous bundle still answers correctly, so the instance
/// should stay in load balancing while someone fixes the rule file.
/// </summary>
internal sealed class RulesHealthCheck(RuleBundleProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(provider.LastFailure is null
            ? HealthCheckResult.Healthy($"bundle {provider.Bundle.Hash[..12]}")
            : HealthCheckResult.Degraded("The last rule reload was rejected; the previous bundle is serving."));
}
