﻿// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Data.Common;
using System.Threading;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;

namespace Hangfire.SqlServer
{
#pragma warning disable 618
    internal class ExpirationManager : IServerComponent
#pragma warning restore 618
    {
        private const string DistributedLockKey = "locks:expirationmanager";
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);
        
        // This value should be high enough to optimize the deletion as much, as possible,
        // reducing the number of queries. But low enough to cause lock escalations (it
        // appears, when ~5000 locks were taken, but this number is a subject of version).
        // Note, that lock escalation may also happen during the cascade deletions for
        // State (3-5 rows/job usually) and JobParameters (2-3 rows/job usually) tables.
        private const int DefaultNumberOfRecordsInSinglePass = 1000;
        
        private static readonly string[] ProcessedTables =
        {
            "AggregatedCounter",
            "Job",
            "List",
            "Set",
            "Hash",
        };

        private readonly ILog _logger = LogProvider.For<ExpirationManager>();
        private readonly SqlServerStorage _storage;
        private readonly TimeSpan _stateExpirationTimeout;
        private readonly TimeSpan _checkInterval;

        public ExpirationManager(SqlServerStorage storage, TimeSpan stateExpirationTimeout, TimeSpan checkInterval)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (stateExpirationTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stateExpirationTimeout), "Timeout value should be equal to or greater than zero.");
            if (checkInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkInterval), "Timeout value should be greater than zero.");

            _storage = storage;
            _stateExpirationTimeout = stateExpirationTimeout;
            _checkInterval = checkInterval;
        }

        public void Execute(CancellationToken cancellationToken)
        {
            var numberOfRecordsInSinglePass = _storage.Options.DeleteExpiredBatchSize;
            if (numberOfRecordsInSinglePass <= 0 || numberOfRecordsInSinglePass > 100_000)
            {
                numberOfRecordsInSinglePass = DefaultNumberOfRecordsInSinglePass;
            }

            foreach (var table in ProcessedTables)
            {
                CleanupTable(GetExpireQuery(_storage.SchemaName, table), table, numberOfRecordsInSinglePass, cancellationToken);
            }

            if (_stateExpirationTimeout > TimeSpan.Zero)
            {
                CleanupTable(GetStateCleanupQuery(_storage.SchemaName), "State", numberOfRecordsInSinglePass,
                    cancellationToken,
                    command =>
                    {
                        var expireMinParameter = command.CreateParameter();
                        expireMinParameter.ParameterName = "@expireMin";
                        expireMinParameter.Value = (long)_stateExpirationTimeout.Negate().TotalMinutes;

                        command.Parameters.Add(expireMinParameter);
                    });
            }

            cancellationToken.Wait(_checkInterval);
        }

        public override string ToString()
        {
            return GetType().ToString();
        }

        private void CleanupTable(string query, string table, int numberOfRecordsInSinglePass, CancellationToken cancellationToken, Action<DbCommand> additionalActions = null)
        {
            _logger.Debug($"Removing outdated records from the '{table}' table...");

            UseConnectionDistributedLock(_storage, connection =>
            {
                int affected;

                do
                {
                    affected = ExecuteNonQuery(
                        connection,
                        query,
                        numberOfRecordsInSinglePass,
                        cancellationToken,
                        additionalActions);
                } while (affected == numberOfRecordsInSinglePass);
            });

            _logger.Trace($"Outdated records removed from the '{table}' table.");
        }

        private void UseConnectionDistributedLock(SqlServerStorage storage, Action<DbConnection> action)
        {
            try
            {
                storage.UseConnection(null, connection =>
                {
                    SqlServerDistributedLock.Acquire(connection, DistributedLockKey, DefaultLockTimeout);

                    try
                    {
                        action(connection);
                    }
                    finally
                    {
                        SqlServerDistributedLock.Release(connection, DistributedLockKey);
                    }
                });
            }
            catch (DistributedLockTimeoutException e) when (e.Resource == DistributedLockKey)
            {
                // DistributedLockTimeoutException here doesn't mean that outdated records weren't removed.
                // It just means another Hangfire server did this work.
                _logger.Log(
                    LogLevel.Debug,
                    () => $@"An exception was thrown during acquiring distributed lock on the {DistributedLockKey} resource within {DefaultLockTimeout.TotalSeconds} seconds. Outdated records were not removed.
It will be retried in {_checkInterval.TotalSeconds} seconds.",
                    e);
            }
        }

        private static string GetExpireQuery(string schemaName, string table)
        {
            if (table.Equals("AggregatedCounter", StringComparison.OrdinalIgnoreCase))
            {
                // Schema 5, which still should be supported by Hangfire doesn't have an index that covers
                // the `ExpireAt` column, making it impossible to run the query.
                return $@"
set deadlock_priority low;
set transaction isolation level read committed;
set xact_abort on;
set lock_timeout 1000;
delete top (@count) T from [{schemaName}].[{table}] T
where ExpireAt < @now
option (loop join, optimize for (@count = 20000));";
            }

            return $@"
set deadlock_priority low;
set transaction isolation level read committed;
set xact_abort on;
set lock_timeout 1000;
delete top (@count) T from [{schemaName}].[{table}] T with (forceseek)
where ExpireAt < @now
option (loop join, optimize for (@count = 20000));";
        }

        private static string GetStateCleanupQuery(string schemaName)
        {
            // TODO: Make expiration condition configurable
            return $@"
set deadlock_priority low;
set transaction isolation level read committed;
set xact_abort on;
set lock_timeout 1000;

;with cte as (
	select s.[JobId], s.[Id]
	from [{schemaName}].[State] s with (forceseek)
	where s.[CreatedAt] < dateadd(minute, @expireMin, @now)
	and exists (
		select * from [{schemaName}].[Job] j with (forceseek)
		where j.[Id] = s.[JobId] and j.[StateId] != s.[Id]))
delete top(@count) from cte option (maxdop 1);";
        }

        private static int ExecuteNonQuery(
            DbConnection connection,
            string commandText,
            int numberOfRecordsInSinglePass,
            CancellationToken cancellationToken,
            Action<DbCommand> additionalActions)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = commandText;
                command.CommandTimeout = 0;

                var countParameter = command.CreateParameter();
                countParameter.ParameterName = "@count";
                countParameter.Value = numberOfRecordsInSinglePass;

                var nowParameter = command.CreateParameter();
                nowParameter.ParameterName = "@now";
                nowParameter.Value = DateTime.UtcNow;

                command.Parameters.Add(countParameter);
                command.Parameters.Add(nowParameter);
                
                additionalActions?.Invoke(command);

                using (cancellationToken.Register(state => ((DbCommand)state).Cancel(), command))
                {
                    try
                    {
                        return command.ExecuteNonQuery();
                    }
                    catch (DbException ex) when (cancellationToken.IsCancellationRequested || ex.Message.Contains("Lock request time out period exceeded"))
                    {
                        // Exception was triggered due to the Cancel method call, ignoring
                        return 0;
                    }
                }
            }
        }
    }
}

