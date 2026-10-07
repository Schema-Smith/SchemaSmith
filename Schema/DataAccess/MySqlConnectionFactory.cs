// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Data;
using MySqlConnector;
using MySqlConnector.Authentication.Ed25519;
using Schema.Isolators;

namespace Schema.DataAccess;

public class MySqlConnectionFactory : IDbConnectionFactory
{
    // Same rationale as PostgreSqlConnectionFactory: a MySqlDataSource owns a pool and must be shared,
    // not created per call (which leaks a pool per call). Cache one per connection string.
    private static readonly ConcurrentDictionary<string, Lazy<MySqlDataSource>> DataSources = new();

    internal static int CachedDataSourceCount => DataSources.Count;

    // MariaDB accounts can authenticate with ed25519 (10.1.22+) or PARSEC (11.6+), which the connector supports only
    // once their plugins are installed. Without them such an account could not connect at all.
    static MySqlConnectionFactory()
    {
        Ed25519AuthenticationPlugin.Install();
        ParsecAuthenticationPlugin.Install();
    }

    public IDbConnection GetDbConnection(string connectionString)
    {
        var dataSource = DataSources.GetOrAdd(connectionString,
            cs => new Lazy<MySqlDataSource>(() => new MySqlDataSource(cs))).Value;
        return dataSource.CreateConnection();
    }

    public static IDbConnectionFactory GetFromFactory()
    {
        return FactoryContainer.ResolveOrCreate<IDbConnectionFactory, MySqlConnectionFactory>();
    }
}
