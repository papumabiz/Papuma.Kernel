// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Reflection;

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// Pins the public surface of the two SQLite feed processors. They are facades over a
/// shared engine; moving code behind them must not change a single public member.
/// </summary>
public sealed class SqliteFeedProcessorApiShapeTests
{
    [Fact]
    public void SqliteChangeFeedProcessor_PublicSurface_IsUnchanged() =>
        Assert.Equal(Expected_SqliteChange, Describe(typeof(SqliteChangeFeedProcessor)));

    [Fact]
    public void SqliteEventFeedProcessor_PublicSurface_IsUnchanged() =>
        Assert.Equal(Expected_SqliteEvent, Describe(typeof(SqliteEventFeedProcessor)));

    private static string[] Describe(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var lines = new List<string>
        {
            $"type {type.FullName} public={type.IsPublic} sealed={type.IsSealed} abstract={type.IsAbstract}",
            $"base {type.BaseType}",
        };
        lines.AddRange(type.GetInterfaces().Select(i => $"interface {i}"));
        lines.AddRange(type.GetConstructors(all).Select(c => $"ctor {c}"));
        lines.AddRange(type.GetMethods(all).Select(m => $"method {m}"));
        lines.AddRange(type.GetProperties(all).Select(p => $"property {p.PropertyType} {p.Name}"));
        lines.AddRange(type.GetFields(all).Select(f => $"field {f.FieldType} {f.Name} = {f.GetRawConstantValue()}"));
        lines.AddRange(type.GetEvents(all).Select(e => $"event {e.Name}"));
        lines.Sort(StringComparer.Ordinal);
        return lines.ToArray();
    }

    private static readonly string[] Expected_SqliteChange = [
        "base System.Object",
        "ctor Void .ctor(System.String, System.Collections.Generic.IEnumerable`1[Papuma.Kernel.Processing.IChangeHandler], Papuma.Kernel.Processing.SqliteChangeNotifier, Papuma.Kernel.Processing.ChangeFeedProcessorOptions, Microsoft.Extensions.Logging.ILogger`1[Papuma.Kernel.Processing.SqliteChangeFeedProcessor])",
        "interface System.IDisposable",
        "method System.Threading.Tasks.Task ResetCheckpointAsync(System.String, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Boolean] ResetProjectionAsync(System.String, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Boolean] RetryFailureAsync(System.String, Int64, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Papuma.Kernel.Processing.ChangeFeedLagSnapshot]] GetLagAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Papuma.Kernel.Processing.FeedFailure]] GetFailuresAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Int32] ProcessOnceAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Int32] ResetProjectionsAsync(System.Threading.CancellationToken)",
        "method Void Dispose()",
        "type Papuma.Kernel.Processing.SqliteChangeFeedProcessor public=True sealed=True abstract=False",
    ];

    private static readonly string[] Expected_SqliteEvent = [
        "base System.Object",
        "ctor Void .ctor(System.String, System.Collections.Generic.IEnumerable`1[Papuma.Kernel.Events.IEventHandler], Papuma.Kernel.Processing.SqliteChangeNotifier, Papuma.Kernel.Processing.ChangeFeedProcessorOptions, Microsoft.Extensions.Logging.ILogger`1[Papuma.Kernel.Events.SqliteEventFeedProcessor])",
        "interface System.IDisposable",
        "method System.Threading.Tasks.Task ResetCheckpointAsync(System.String, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Boolean] ResetProjectionAsync(System.String, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Boolean] RetryFailureAsync(System.String, Int64, System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Papuma.Kernel.Processing.ChangeFeedLagSnapshot]] GetLagAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Papuma.Kernel.Processing.FeedFailure]] GetFailuresAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Int32] ProcessOnceAsync(System.Threading.CancellationToken)",
        "method System.Threading.Tasks.Task`1[System.Int32] ResetProjectionsAsync(System.Threading.CancellationToken)",
        "method Void Dispose()",
        "type Papuma.Kernel.Events.SqliteEventFeedProcessor public=True sealed=True abstract=False",
    ];
}
