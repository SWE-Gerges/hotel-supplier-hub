using System.Reflection;
using Hub.Core;

namespace Hub.Suppliers.Fakes;

/// <summary>The cancellation-policy rule files that ship with the fake suppliers, one per supplier.</summary>
public sealed class EmbeddedPolicyRuleSource : IPolicyRuleSource
{
    public IEnumerable<(string Name, string Json)> Read()
    {
        var assembly = typeof(EmbeddedPolicyRuleSource).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(static n => n.Contains(".PolicyRules.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[(resource.IndexOf(".PolicyRules.", StringComparison.Ordinal) + ".PolicyRules.".Length)..], reader.ReadToEnd());
        }
    }
}
