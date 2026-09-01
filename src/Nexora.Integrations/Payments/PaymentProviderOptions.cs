namespace Nexora.Integrations.Payments;

public sealed class PaymentProviderOptions
{
    public const string SectionName = "Billing:Payment";
    public string Provider { get; init; } = "fake";
}
