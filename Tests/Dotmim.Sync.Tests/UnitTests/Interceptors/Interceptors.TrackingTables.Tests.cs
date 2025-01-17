﻿using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.SqlServer;
using Dotmim.Sync.Tests.Core;
using Dotmim.Sync.Tests.Models;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Dotmim.Sync.Tests.UnitTests
{
    public partial class InterceptorsTests
    {
        [Fact]
        public async Task TrackingTable_Create_One()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();

            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                TrackingTablesPrefix = "t_",
                TrackingTablesSuffix = "_t"
            };

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            var onCreating = false;
            var onCreated = false;

            remoteOrchestrator.OnTrackingTableCreating(ttca =>
            {
                var addingID = $" ALTER TABLE {ttca.TrackingTableName.Schema().Quoted()} ADD internal_id int identity(1,1)";
                ttca.Command.CommandText += addingID;
                onCreating = true;
            });

            remoteOrchestrator.OnTrackingTableCreated(ttca =>
            {
                onCreated = true;
            });

            await remoteOrchestrator.CreateTrackingTableAsync(scopeInfo, "Product", "SalesLT");

            Assert.True(onCreating);
            Assert.True(onCreated);


            // Check we have a new column in tracking table
            using (var c = new SqlConnection(cs))
            {
                await c.OpenAsync().ConfigureAwait(false);
                var cols = await SqlManagementUtils.GetColumnsForTableAsync("t_Product_t", "SalesLT", c, null).ConfigureAwait(false);
                Assert.Equal(7, cols.Rows.Count);
                Assert.NotNull(cols.Rows.FirstOrDefault(r => r["name"].ToString() == "internal_id"));
                c.Close();
            }

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task TrackingTable_Exists()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product", "SalesLT.ProductCategory" });
            setup.TrackingTablesPrefix = "t_";
            setup.TrackingTablesSuffix = "_t";

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            await remoteOrchestrator.CreateTrackingTableAsync(scopeInfo, "Product", "SalesLT");

            var exists = await remoteOrchestrator.ExistTrackingTableAsync(scopeInfo, "Product", "SalesLT");
            Assert.True(exists);

            exists = await remoteOrchestrator.ExistTrackingTableAsync(scopeInfo, "ProductCategory", "SalesLT");
            Assert.False(exists);

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }


        [Fact]
        public async Task TrackingTable_Create_All()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[]
            { "SalesLT.ProductCategory", "SalesLT.ProductModel", "SalesLT.Product", "Posts" })
            {
                TrackingTablesPrefix = "t_",
                TrackingTablesSuffix = "_t"
            };

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            var onCreating = 0;
            var onCreated = 0;
            var onDropping = 0;
            var onDropped = 0;

            remoteOrchestrator.OnTrackingTableCreating(ttca => onCreating++);
            remoteOrchestrator.OnTrackingTableCreated(ttca => onCreated++);
            remoteOrchestrator.OnTrackingTableDropping(ttca => onDropping++);
            remoteOrchestrator.OnTrackingTableDropped(ttca => onDropped++);

            await remoteOrchestrator.CreateTrackingTablesAsync(scopeInfo);

            Assert.Equal(4, onCreating);
            Assert.Equal(4, onCreated);
            Assert.Equal(0, onDropping);
            Assert.Equal(0, onDropped);

            onCreating = 0;
            onCreated = 0;
            onDropping = 0;
            onDropped = 0;

            await remoteOrchestrator.CreateTrackingTablesAsync(scopeInfo);

            Assert.Equal(0, onCreating);
            Assert.Equal(0, onCreated);
            Assert.Equal(0, onDropping);
            Assert.Equal(0, onDropped);

            onCreating = 0;
            onCreated = 0;
            onDropping = 0;
            onDropped = 0;

            await remoteOrchestrator.CreateTrackingTablesAsync(scopeInfo, true);

            Assert.Equal(4, onCreating);
            Assert.Equal(4, onCreated);
            Assert.Equal(4, onDropping);
            Assert.Equal(4, onDropped);

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task TrackingTable_Drop_One()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" });
            setup.TrackingTablesPrefix = "t_";
            setup.TrackingTablesSuffix = "_t";

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            var onDropping = false;
            var onDropped = false;

            remoteOrchestrator.OnTrackingTableDropping(ttca =>
            {
                onDropping = true;
            });

            remoteOrchestrator.OnTrackingTableDropped(ttca =>
            {
                onDropped = true;
            });

            await remoteOrchestrator.CreateTrackingTableAsync(scopeInfo, "Product", "SalesLT");
            await remoteOrchestrator.DropTrackingTableAsync(scopeInfo, "Product", "SalesLT");

            Assert.True(onDropping);
            Assert.True(onDropped);


            // Check we have a new column in tracking table
            using (var c = new SqlConnection(cs))
            {
                await c.OpenAsync().ConfigureAwait(false);

                var table = await SqlManagementUtils.GetTableDefinitionAsync("t_Product_t", "SalesLT", c, null).ConfigureAwait(false);

                Assert.Empty(table.Rows);

                c.Close();
            }

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task TrackingTable_Drop_One_Cancel()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" });
            setup.TrackingTablesPrefix = "t_";
            setup.TrackingTablesSuffix = "_t";

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            var onDropping = false;
            var onDropped = false;

            remoteOrchestrator.OnTrackingTableDropping(ttca =>
            {
                ttca.Cancel = true;
                onDropping = true;
            });

            remoteOrchestrator.OnTrackingTableDropped(ttca =>
            {
                onDropped = true;
            });

            await remoteOrchestrator.CreateTrackingTableAsync(scopeInfo, "Product", "SalesLT");
            await remoteOrchestrator.DropTrackingTableAsync(scopeInfo, "Product", "SalesLT");

            Assert.True(onDropping);
            Assert.False(onDropped);

            // Check we have a new column in tracking table
            using (var c = new SqlConnection(cs))
            {
                await c.OpenAsync().ConfigureAwait(false);

                var table = await SqlManagementUtils.GetTableDefinitionAsync("t_Product_t", "SalesLT", c, null).ConfigureAwait(false);

                Assert.NotEmpty(table.Rows);

                c.Close();
            }

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task TrackingTable_Drop_All()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.ProductCategory", "SalesLT.ProductModel", "SalesLT.Product", "Posts" });
            setup.TrackingTablesPrefix = "t_";
            setup.TrackingTablesSuffix = "_t";

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

            var onDropping = 0;
            var onDropped = 0;

            remoteOrchestrator.OnTrackingTableDropping(ttca => onDropping++);
            remoteOrchestrator.OnTrackingTableDropped(ttca => onDropped++);

            await remoteOrchestrator.CreateTrackingTablesAsync(scopeInfo);
            await remoteOrchestrator.DropTrackingTablesAsync(scopeInfo);


            Assert.Equal(4, onDropping);
            Assert.Equal(4, onDropped);

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

    }
}
