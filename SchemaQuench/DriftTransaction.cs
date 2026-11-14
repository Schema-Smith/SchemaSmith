// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;

namespace SchemaQuench;

/// <summary>
/// H1, tier 1: a drift check on SQL Server or PostgreSQL runs on one connection, inside one transaction that is always
/// rolled back, so kindling and every preview write leave nothing behind. PostgreSQL aborts the whole transaction on any
/// error, and a deploy catches errors it expects -- a probe of a table that may not exist -- and carries on; so here each
/// statement runs under a savepoint and an error rolls back to it, which is what such a statement does outside a
/// transaction.
/// </summary>
internal sealed class DriftTransaction(IDbConnection connection, bool savepointEachStatement)
{
    private const string Savepoint = "schemasmith_drift_statement";
    // A reader's savepoint is released before the next statement: the connection cannot run one while the reader is open.
    private bool _readerSavepointOpen;

    public bool SavepointEachStatement { get; } = savepointEachStatement;

    public static string BeginStatement(bool sqlServer) => sqlServer ? "BEGIN TRANSACTION" : "BEGIN";

    public static string RollbackStatement(bool sqlServer) => sqlServer ? "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION" : "ROLLBACK";

    internal void Control(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal T Statement<T>(Func<T> execute, bool keepsReaderOpen = false)
    {
        if (!SavepointEachStatement) return execute();
        if (_readerSavepointOpen)
        {
            _readerSavepointOpen = false;
            Control($"RELEASE SAVEPOINT {Savepoint}");
        }
        Control($"SAVEPOINT {Savepoint}");
        T result;
        try
        {
            result = execute();
        }
        catch (DbException)
        {
            Control($"ROLLBACK TO SAVEPOINT {Savepoint}");
            Control($"RELEASE SAVEPOINT {Savepoint}");
            throw;
        }
        if (keepsReaderOpen) _readerSavepointOpen = true;
        else Control($"RELEASE SAVEPOINT {Savepoint}");
        return result;
    }
}

/// <summary>
/// One lane of a tier-1 drift check: a command on the shared connection that runs each statement through the transaction
/// (a savepoint on PostgreSQL) and, around it, switches the connection to what that lane's own connection would have been
/// -- SQL Server's error-as-message mode off for the object and kindle lanes, the server's messages muted for kindling.
/// </summary>
internal sealed class DriftLaneCommand(IDbCommand inner, DriftTransaction transaction, Action enter = null, Action exit = null) : IDbCommand
{
    private T Run<T>(Func<T> execute, bool keepsReaderOpen = false)
    {
        enter?.Invoke();
        try
        {
            return transaction.Statement(execute, keepsReaderOpen);
        }
        finally
        {
            exit?.Invoke();
        }
    }

    public int ExecuteNonQuery() => Run(inner.ExecuteNonQuery);
    public object ExecuteScalar() => Run(inner.ExecuteScalar);
    public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);
    public IDataReader ExecuteReader(CommandBehavior behavior) => Run(() => inner.ExecuteReader(behavior), keepsReaderOpen: true);

    public void Prepare() => inner.Prepare();
    public void Cancel() => inner.Cancel();
    public IDbDataParameter CreateParameter() => inner.CreateParameter();
    public void Dispose() => inner.Dispose();

    public string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public IDbConnection Connection { get => inner.Connection; set => inner.Connection = value; }
    public IDataParameterCollection Parameters => inner.Parameters;
    public IDbTransaction Transaction { get => inner.Transaction; set => inner.Transaction = value; }
    public UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
}
