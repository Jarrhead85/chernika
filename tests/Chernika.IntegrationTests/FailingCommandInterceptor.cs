using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chernika.IntegrationTests;

/// <summary>
/// Global (inert unless armed) command interceptor for controlled-failure
/// tests: injects an exception at a chosen command inside an EF operation so
/// service-level transactions can be exercised mid-flight.
///
/// Arming is AsyncLocal-scoped: it flows only into the arming test's async
/// call chain, so parallel test collections (other DbContexts sharing this
/// singleton instance) are never affected.
/// </summary>
public sealed class FailingCommandInterceptor : DbCommandInterceptor
{
    public static readonly FailingCommandInterceptor Instance = new();

    private sealed class ArmedState
    {
        public required string SqlContains { get; init; }
        public required int FailAtOccurrence { get; init; }
        public int SeenMatches { get; set; }
        public bool Fired { get; set; }
    }

    private static readonly AsyncLocal<ArmedState?> Armed = new();

    /// <summary>True after the interceptor injected its failure (per test).</summary>
    public static bool Fired => Armed.Value?.Fired ?? false;

    public static void ArmAt(string sqlContains, int occurrence = 1) =>
        Armed.Value = new ArmedState { SqlContains = sqlContains, FailAtOccurrence = occurrence };

    public static void Disarm() => Armed.Value = null;

    private void MaybeFail(DbCommand command)
    {
        var armed = Armed.Value;
        if (armed is null || !command.CommandText.Contains(armed.SqlContains))
            return;
        if (++armed.SeenMatches < armed.FailAtOccurrence)
            return;
        armed.Fired = true;
        Armed.Value = null;
        throw new InvalidOperationException("Injected test failure.");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        MaybeFail(command);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        MaybeFail(command);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        MaybeFail(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
