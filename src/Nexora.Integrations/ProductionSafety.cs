namespace Nexora.Integrations;

public static class ProductionSafety
{
    public static void ValidateDevelopmentAdapters(bool isProduction, bool aiEnabled, bool paymentEnabled, bool uploadEnabled)
    {
        if (!isProduction || (!aiEnabled && !paymentEnabled && !uploadEnabled)) return;
        throw new InvalidOperationException(
            "Production cannot enable the current fake AI, fake payment or local upload adapters before DEC-01, DEC-02 and DEC-04 are resolved. Disable Features:Ai, Features:Payment and Features:Upload.");
    }
}
