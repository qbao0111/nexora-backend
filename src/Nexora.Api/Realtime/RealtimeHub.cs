using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Nexora.Api.Realtime;

[Authorize]
public sealed class RealtimeHub : Hub
{
    public const string Path = "/hubs/realtime";
}
