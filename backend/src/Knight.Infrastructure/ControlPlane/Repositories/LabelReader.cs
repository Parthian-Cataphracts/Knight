using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Resolves the human labels a list shows next to an identifier — a store's
/// customer, a subscription's plan, an account's roles.
///
/// Every method takes the whole page's identifiers at once. Reading them per row
/// would be the same query executed twenty times, and the list would get slower
/// the more it showed.
/// </summary>
internal sealed class LabelReader : ILabelReader
{
    private readonly ControlPlaneDbContext _context;

    public LabelReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await _context.Customers
            .Where(customer => customerIds.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name })
            .ToArrayAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, (string Key, string Name)>> PlanNamesAsync(
        IReadOnlyCollection<Guid> planIds,
        CancellationToken cancellationToken)
    {
        if (planIds.Count == 0)
        {
            return new Dictionary<Guid, (string, string)>();
        }

        var rows = await _context.Plans
            .Where(plan => planIds.Contains(plan.Id))
            .Select(plan => new { plan.Id, plan.Key, plan.Name })
            .ToArrayAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => (row.Key, row.Name));
    }

    /// <summary>
    /// Role names per account. Bypasses the isolation filter for the same reason
    /// permission resolution does: a customer user's own roles are
    /// platform-owned system roles carrying no customer id, and the account ids
    /// already constrain the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> RoleNamesForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyCollection<string>>();
        }

        var rows = await _context.UserRoleAssignments
            .IgnoreQueryFilters()
            .Where(assignment => userIds.Contains(assignment.UserId))
            .Join(
                _context.Roles.IgnoreQueryFilters(),
                assignment => assignment.RoleId,
                role => role.Id,
                (assignment, role) => new { assignment.UserId, role.Name })
            .ToArrayAsync(cancellationToken);

        return userIds.ToDictionary(
            id => id,
            id => (IReadOnlyCollection<string>)rows
                .Where(row => row.UserId == id)
                .Select(row => row.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Left inside the isolation filter, unlike the role lookups above: a store
    /// belongs to exactly one customer, so a customer-scoped caller must not be
    /// able to turn an id it should not have into a name.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> StoreNamesAsync(
        IReadOnlyCollection<Guid> storeIds,
        CancellationToken cancellationToken)
    {
        if (storeIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await _context.Stores
            .Where(store => storeIds.Contains(store.Id))
            .Select(store => new { store.Id, store.PrimaryDomain })
            .ToArrayAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.PrimaryDomain);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> RoleMemberCountsAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.UserRoleAssignments
            .IgnoreQueryFilters()
            .GroupBy(assignment => assignment.RoleId)
            .Select(group => new { RoleId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        return rows.ToDictionary(row => row.RoleId, row => row.Count);
    }
}
