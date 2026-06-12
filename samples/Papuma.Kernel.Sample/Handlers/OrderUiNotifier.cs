// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.SignalR;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Sample.Handlers;

/// <summary>SignalR hub clients join to watch the shop (recipe: realtime-ui-notifications).</summary>
public sealed class ShopHub : Hub;

/// <summary>
/// Pushes "something changed" to connected UIs — the realtime-ui-notifications
/// recipe as running code. Note what is pushed: ids, version, operation and the
/// changed *paths* — never values. The diff is policy-applied (ADR-007), so even
/// the paths leak no protected content; clients reload via the authorized read path.
/// </summary>
public sealed class OrderUiNotifier(IHubContext<ShopHub> hub) : IChangeHandler
{
    public string Name => "ui-notifier";

    public Task HandleAsync(ChangeRecord change, CancellationToken ct) =>
        hub.Clients.All.SendAsync("documentChanged", new
        {
            change.DocumentType,
            change.DocumentId,
            change.Version,
            Operation = change.Operation.ToString(),
            ChangedFields = change.Diff.Paths,
        }, ct);
}
