using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hub.Core;

public sealed class CancellationPolicyOptions
{
    public const string SectionName = "Hub:CancellationPolicy";

    /// <summary>
    /// A directory of rule files (<c>*.json</c>) loaded next to the rules supplier packages ship with. Relative paths are
    /// resolved against the application directory. A file for a supplier that already has rules is a startup error.
    /// </summary>
    public string? RulesDirectory { get; set; }
}

/// <summary>Builds the rule book when the host starts, so an invalid rule file stops startup instead of failing the first search.</summary>
internal sealed class PolicyRulesStartupCheck(PolicyRuleBook rules, ILogger<PolicyRulesStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Cancellation-policy rules loaded for {Suppliers}.", string.Join(", ", rules.Suppliers.OrderBy(static s => s.Value)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
