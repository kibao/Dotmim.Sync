﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class BaseOrchestrator
    {
        internal virtual async Task<(SyncContext context, bool provisioned)> InternalProvisionAsync(ScopeInfo scopeInfo, SyncContext context, bool overwrite, SyncProvision provision, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            if (Provider == null)
                throw new MissingProviderException(nameof(InternalProvisionAsync));

            context.SyncStage = SyncStage.Provisioning;

            // If schema does not have any table, raise an exception
            if (scopeInfo.Schema == null || scopeInfo.Schema.Tables == null || !scopeInfo.Schema.HasTables)
                throw new MissingTablesException(scopeInfo.Name);

            await this.InterceptAsync(new ProvisioningArgs(context, provision, scopeInfo.Schema, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            // get Database builder
            var builder = this.Provider.GetDatabaseBuilder();

            // Initialize database if needed
            await builder.EnsureDatabaseAsync(connection, transaction).ConfigureAwait(false);

            // Check if we have tables AND columns
            // If we don't have any columns it's most probably because user called method with the Setup only
            // So far we have only tables names, it's enough to get the schema
            if (scopeInfo.Schema.HasTables && !scopeInfo.Schema.HasColumns)
                (context, scopeInfo.Schema) = await this.InternalGetSchemaAsync(context, scopeInfo.Setup, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            // Shoudl we create scope
            if (provision.HasFlag(SyncProvision.ScopeInfo))
            {
                bool exists;
                (context, exists) = await this.InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfo, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (!exists)
                    (context, _) = await this.InternalCreateScopeInfoTableAsync(context, DbScopeType.ScopeInfo, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            }

            if (provision.HasFlag(SyncProvision.ScopeInfoClient))
            {
                bool exists;
                (context, exists) = await this.InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (!exists)
                    (context, _) = await this.InternalCreateScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            }

            // Sorting tables based on dependencies between them
            var schemaTables = scopeInfo.Schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, scopeInfo);

                // Check if we need to create a schema there
                bool schemaExists;
                (context, schemaExists) = await InternalExistsSchemaAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (!schemaExists)
                    (context, _) = await InternalCreateSchemaAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (provision.HasFlag(SyncProvision.Table))
                {
                    bool tableExists;
                    (context, tableExists) = await this.InternalExistsTableAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (!tableExists)
                        (context, _) = await this.InternalCreateTableAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                }

                if (provision.HasFlag(SyncProvision.TrackingTable))
                {
                    bool trackingTableExist;
                    (context, trackingTableExist) = await this.InternalExistsTrackingTableAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (!trackingTableExist)
                        (context, _) = await this.InternalCreateTrackingTableAsync(scopeInfo, context, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                }

                if (provision.HasFlag(SyncProvision.Triggers))
                    await this.InternalCreateTriggersAsync(scopeInfo, context, overwrite, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (provision.HasFlag(SyncProvision.StoredProcedures))
                    (context, _) = await this.InternalCreateStoredProceduresAsync(scopeInfo, context, overwrite, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            }


            await this.InterceptAsync(new ProvisionedArgs(context, provision, scopeInfo.Schema, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            return (context, true);

        }

        internal virtual async Task<(SyncContext context, bool deprovisioned)> InternalDeprovisionAsync(ScopeInfo scopeInfo, SyncContext context, SyncProvision provision, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            if (Provider == null)
                throw new MissingProviderException(nameof(InternalDeprovisionAsync));

            context.SyncStage = SyncStage.Deprovisioning;

            await using var runner = await this.GetConnectionAsync(context, SyncMode.WithTransaction, SyncStage.Deprovisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            await this.InterceptAsync(new DeprovisioningArgs(context, provision, scopeInfo?.Setup, runner.Connection, runner.Transaction), progress, cancellationToken).ConfigureAwait(false);

            // get Database builder
            var builder = this.Provider.GetDatabaseBuilder();

            // Sorting tables based on dependencies between them
            IEnumerable<SyncTable> schemaTables;
            if (scopeInfo == null)
            {
                schemaTables = new List<SyncTable>();
            }
            else
            {
                if (scopeInfo.Schema != null)
                {
                    schemaTables = scopeInfo.Schema.Tables.SortByDependencies(tab => tab.GetRelations().Select(r => r.GetParentTable())).ToList();
                }
                else
                {
                    schemaTables = new List<SyncTable>();
                    foreach (var setupTable in scopeInfo.Setup.Tables)
                        ((List<SyncTable>)schemaTables).Add(new SyncTable(setupTable.TableName, setupTable.SchemaName));
                }
            }

            // Disable check constraints
            if (this.Options.DisableConstraintsOnApplyChanges)
                foreach (var table in schemaTables.Reverse())
                    await this.InternalDisableConstraintsAsync(scopeInfo, context, table, runner.Connection, runner.Transaction).ConfigureAwait(false);


            // Checking if we have to deprovision tables
            bool hasDeprovisionTableFlag = provision.HasFlag(SyncProvision.Table);

            // Firstly, removing the flag from the provision, because we need to drop everything in correct order, then drop tables in reverse side
            if (hasDeprovisionTableFlag)
                provision ^= SyncProvision.Table;

            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, scopeInfo);

                if (provision.HasFlag(SyncProvision.StoredProcedures))
                {
                    (context, _) = await InternalDropStoredProceduresAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    // Removing cached commands
                    this.RemoveCommands();
                }

                if (provision.HasFlag(SyncProvision.Triggers))
                {
                    (context, _) = await InternalDropTriggersAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }

                if (provision.HasFlag(SyncProvision.TrackingTable))
                {
                    bool exists;
                    (context, exists) = await InternalExistsTrackingTableAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (exists)
                        (context, _) = await this.InternalDropTrackingTableAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }
            }

            // Eventually if we have the "Table" flag, then drop the table
            if (hasDeprovisionTableFlag)
            {
                foreach (var schemaTable in schemaTables.Reverse())
                {
                    var tableBuilder = this.GetTableBuilder(schemaTable, scopeInfo);
                    bool exists;
                    (context, exists) = await InternalExistsTableAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (exists)
                        (context, _) = await this.InternalDropTableAsync(scopeInfo, context, tableBuilder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }
            }

            if (provision.HasFlag(SyncProvision.ScopeInfo))
            {
                bool exists;
                (context, exists) = await this.InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfo, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                if (exists)
                {
                    (context, _) = await this.InternalDropScopeInfoTableAsync(context, DbScopeType.ScopeInfo, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }
            }

            if (provision.HasFlag(SyncProvision.ScopeInfoClient))
            {
                bool exists;
                (context, exists) = await this.InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                if (exists)
                    (context, _) = await this.InternalDropScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
            }

            var args = new DeprovisionedArgs(context, provision, scopeInfo?.Setup, runner.Connection);
            await this.InterceptAsync(args, progress, cancellationToken).ConfigureAwait(false);

            await runner.CommitAsync().ConfigureAwait(false);


            return (context, true);
        }

    }
}
