using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TechnoSurfaces.IntegrationTests.Infrastructure;

/// <summary>
/// Counts the SQL commands EF Core sends, so a test can hold a request to a budget.
/// Every command is a round trip to the database, and on the Basic tier in Azure the
/// number of round trips, more than their size, is what makes a page slow.
/// </summary>
public sealed class QueryCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    private void Counted() => Interlocked.Increment(ref _count);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Counted();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Counted();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Counted();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Counted();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Counted();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Counted();
        return ValueTask.FromResult(result);
    }
}
