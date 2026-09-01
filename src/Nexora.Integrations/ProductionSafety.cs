namespace Nexora.Integrations;

public static class ProductionSafety
{
    public static void ValidateDevelopmentAdapters(bool isProduction, bool aiEnabled, bool paymentEnabled, bool uploadEnabled)
    {
        if (!isProduction || (!aiEnabled && !paymentEnabled && !uploadEnabled)) return;
        var enabled = new List<string>();
        if (aiEnabled) enabled.Add("AI (DEC-01)");
        if (paymentEnabled) enabled.Add("non-production payment adapter (DEC-02)");
        if (uploadEnabled) enabled.Add("local upload (DEC-04)");
        throw new InvalidOperationException(
            $"Production cannot enable {string.Join(", ", enabled)} before the corresponding production decisions are resolved. Disable the affected Features settings.");
    }
}
