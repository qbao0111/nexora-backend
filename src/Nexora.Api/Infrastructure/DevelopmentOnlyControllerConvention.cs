using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Nexora.Api.Controllers;

namespace Nexora.Api.Infrastructure;

internal sealed class DevelopmentOnlyControllerConvention(IHostEnvironment environment) : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing") ||
            controller.ControllerType != typeof(DevelopmentPracticeController)) return;

        controller.Selectors.Clear();
        foreach (var action in controller.Actions) action.Selectors.Clear();
    }
}
