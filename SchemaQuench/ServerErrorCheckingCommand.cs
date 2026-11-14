// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;

namespace SchemaQuench;

/// <summary>
/// A SQL Server command on a connection that reports user errors as messages (FireInfoMessageEventOnUserErrors), so
/// that a procedure's errors arrive in order with its progress. On such a connection an error never throws: a gate that
/// failed came back NULL and read as false, and a failed statement read as done. Each execution here clears the error
/// the message handler captured, runs, and throws the error if one arrived.
/// </summary>
internal sealed class ServerErrorCheckingCommand(IDbCommand inner, Func<Exception> takeCapturedError) : IDbCommand
{
    public int ExecuteNonQuery()
    {
        takeCapturedError();
        var result = inner.ExecuteNonQuery();
        ThrowIfCaptured();
        return result;
    }

    public object ExecuteScalar()
    {
        takeCapturedError();
        var result = inner.ExecuteScalar();
        ThrowIfCaptured();
        return result;
    }

    // An error raised while rows are read arrives after this returns; errors before the first result set are caught here.
    public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);

    public IDataReader ExecuteReader(CommandBehavior behavior)
    {
        takeCapturedError();
        var reader = inner.ExecuteReader(behavior);
        var error = takeCapturedError();
        if (error == null) return reader;
        reader.Dispose();
        throw error;
    }

    private void ThrowIfCaptured()
    {
        var error = takeCapturedError();
        if (error != null) throw error;
    }

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
