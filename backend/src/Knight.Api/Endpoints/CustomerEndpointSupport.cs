using Customer.Domain;
using Knight.Contracts.Customer;

namespace Knight.Api.Endpoints;

internal static class CustomerEndpointSupport
{
    public static CustomerResponse ToResponse(Customer.Domain.Customer customer)
    {
        return new CustomerResponse
        {
            Id = customer.Id,
            DisplayName = customer.DisplayName,
            Phone = customer.Phone,
            Email = customer.Email,
            Status = customer.Status.ToString(),
            CreatedAt = customer.CreatedAt,
            UpdatedAt = customer.UpdatedAt,
            ArchivedAt = customer.ArchivedAt
        };
    }

    public static CustomerStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (Enum.TryParse<CustomerStatus>(status.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new Knight.Application.Exceptions.ValidationException(new Dictionary<string, string[]>
        {
            ["status"] = [$"Unknown status '{status}'."]
        });
    }
}
