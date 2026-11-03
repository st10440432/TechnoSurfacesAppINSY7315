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
    private int _rows;

    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Rows read back across every command since the last reset. A list that loads
    /// every quote with every version reads more rows each month, even when the
    /// number of commands stays the same.
    /// </summary>
    public int RowsRead => Volatile.Read(ref _rows);

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _rows, 0);
    }

    private void Counted() => Interlocked.Increment(ref _count);

    private void RowRead() => Interlocked.Increment(ref _rows);

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) =>
        new RowCountingReader(result, RowRead);

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<DbDataReader>(new RowCountingReader(result, RowRead));

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
