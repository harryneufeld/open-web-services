using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Application.Domains;

/// <summary>
/// One line in the domain search: a name with the same status and prices at one or more groups. Groups of the same
/// registrar usually return identical results, so they share a line and the group is chosen when ordering.
/// </summary>
/// <param name="Choices">The result as each group returned it, in search order.</param>
public sealed record DomainRow(IReadOnlyList<DomainAvailability> Choices)
{
    public DomainAvailability Result => Choices[0];

    public IReadOnlyList<string> GroupNames => [.. Choices.Select(c => c.GroupName).Distinct()];

    /// <summary>Merges results that differ only in their group, keeping the order of their first appearance.</summary>
    public static IReadOnlyList<DomainRow> Merge(IEnumerable<DomainAvailability> results) =>
        [.. results
            .GroupBy(r => (r.Domain, r.Available, r.Transferable, r.IsPremium, r.Price, r.RenewalPrice, r.TransferPrice, r.Currency))
            .Select(g => new DomainRow([.. g]))];
}
