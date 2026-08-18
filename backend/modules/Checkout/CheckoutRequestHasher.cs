using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Checkout.Domain;
using Knight.Application.Exceptions;

namespace Checkout;

public sealed class CheckoutRequestHasher : ICheckoutRequestHasher
{
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 128;

    public string ComputeKeyHash(string rawIdempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(rawIdempotencyKey))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["The Idempotency-Key header is required."]
            });
        }

        var trimmed = rawIdempotencyKey.Trim();
        if (trimmed.Length < MinKeyLength || trimmed.Length > MaxKeyLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = [$"The Idempotency-Key header must be between {MinKeyLength} and {MaxKeyLength} characters."]
            });
        }

        var bytes = Encoding.UTF8.GetBytes(trimmed);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public string ComputeRequestHash(
        CheckoutGuestPartySelection guestParty,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection fulfillment,
        string? couponCode = null)
    {
        var partyPart = $"Party:{guestParty.DisplayName.Trim()}|{(string.IsNullOrWhiteSpace(guestParty.Phone) ? "" : guestParty.Phone.Trim())}|{(string.IsNullOrWhiteSpace(guestParty.Email) ? "" : guestParty.Email.Trim().ToLowerInvariant())}";

        var itemParts = new List<string>(items.Count);
        foreach (var item in items)
        {
            var sortedMods = (item.ModifierIds ?? [])
                .OrderBy(id => id)
                .Select(id => id.ToString())
                .ToArray();

            var itemPart = $"Item:{item.ProductId}:{item.VariantId?.ToString() ?? ""}:{item.Quantity}:Mods:[{string.Join(",", sortedMods)}]";
            itemParts.Add(itemPart);
        }

        var itemsPart = $"Items:{string.Join(";", itemParts)}";

        var method = (fulfillment.Method ?? "").Trim().ToLowerInvariant();
        var zoneId = fulfillment.DeliveryZoneId?.ToString() ?? "";
        var addr1 = (fulfillment.AddressLine1 ?? "").Trim();
        var addr2 = (fulfillment.AddressLine2 ?? "").Trim();
        var city = (fulfillment.City ?? "").Trim();
        var postal = (fulfillment.PostalCode ?? "").Trim();
        var lat = fulfillment.Latitude.HasValue ? fulfillment.Latitude.Value.ToString("R", CultureInfo.InvariantCulture) : "";
        var lon = fulfillment.Longitude.HasValue ? fulfillment.Longitude.Value.ToString("R", CultureInfo.InvariantCulture) : "";

        var fulfillmentPart = $"Fulfillment:{method}|{zoneId}|{addr1}|{addr2}|{city}|{postal}|{lat}|{lon}";

        var normalizedCoupon = string.IsNullOrWhiteSpace(couponCode) ? "" : couponCode.Trim().ToUpperInvariant();
        var couponPart = $"Coupon:{normalizedCoupon}";

        var canonical = $"{partyPart};{itemsPart};{fulfillmentPart};{couponPart}";
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
