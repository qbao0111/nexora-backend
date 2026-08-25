using Nexora.Integrations;

namespace Nexora.UnitTests;

public sealed class ProductionSafetyTests
{
    [Fact]
    public void ProductionFailsClosedWhileAnyDevelopmentAdapterFeatureIsEnabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateDevelopmentAdapters(true, aiEnabled: false, paymentEnabled: true, uploadEnabled: false));
    }

    [Fact]
    public void DisabledProductionCapabilitiesAndDevelopmentRemainAvailable()
    {
        ProductionSafety.ValidateDevelopmentAdapters(true, aiEnabled: false, paymentEnabled: false, uploadEnabled: false);
        ProductionSafety.ValidateDevelopmentAdapters(false, aiEnabled: true, paymentEnabled: true, uploadEnabled: true);
    }
}
