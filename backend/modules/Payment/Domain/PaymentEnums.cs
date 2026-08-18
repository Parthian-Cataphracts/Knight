namespace Payment.Domain;

public enum PaymentMethod
{
    Online = 1,
    PayOnFulfillment = 2
}

public enum PaymentStatus
{
    Pending = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}

public enum PaymentAttemptStatus
{
    Created = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}
