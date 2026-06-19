using Autodesk.Revit.UI;

namespace Iris.RevitMcp;

public sealed class App : IExternalApplication
{
    private PipeBridgeService? _bridgeService;

    public Result OnStartup(UIControlledApplication application)
    {
        var handler = new RevitRequestExternalEventHandler();
        var externalEvent = ExternalEvent.Create(handler);
        _bridgeService = new PipeBridgeService(handler, externalEvent);
        _bridgeService.Start();
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _bridgeService?.Dispose();
        return Result.Succeeded;
    }
}
