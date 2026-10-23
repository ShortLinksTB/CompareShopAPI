using System.Data.Common;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.UserCommerce;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminIdentityProvisioningIntegrationTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Concurrent_admin_provisioning_keeps_one_user_and_one_audit_event()
    {
        var connection = IsolatedConnection("CompareShopProvisionRace");
        var baseOptions = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using (var setup = new CompareShopDbContext(baseOptions)) await setup.Database.MigrateAsync();
            var barrier = new FirstLookupBarrier();
            var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).AddInterceptors(barrier).Options;
            var subject = $"race-{Guid.NewGuid():N}";

            async Task<CompareShopAPI.Application.UserCommerce.UserProvisioningResult> Provision()
            {
                await using var db = new CompareShopDbContext(options);
                return await new EfUserProvisioning(db, TimeProvider.System).GetOrCreateForAdmin(subject, default);
            }

            var results = await Task.WhenAll(Task.Run(Provision), Task.Run(Provision));

            Assert.Equal(1, results.Count(result => result.Created));
            Assert.Equal(results[0].UserId, results[1].UserId);
            await using var verify = new CompareShopDbContext(baseOptions);
            Assert.Equal(1, await verify.Users.CountAsync(user => user.KeycloakSubject == subject));
            var audit = await verify.AdminAuditLogs.Where(entry => entry.ActorSubject == subject).ToListAsync();
            var entry = Assert.Single(audit);
            Assert.Equal("user_provisioned", entry.Action);
            Assert.Equal("user", entry.ResourceType);
            Assert.Equal(results[0].UserId.ToString(), entry.ResourceId);
            Assert.Equal("{\"roleCount\":0}", entry.AfterJson);
            Assert.Empty(await verify.AdminUserRoles.Where(role => role.UserId == results[0].UserId).ToListAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(baseOptions);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_new_identity()
    {
        var connection = IsolatedConnection("CompareShopProvisionRollback");
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var subject = $"rollback-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync();
                await setup.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_AdminAuditLogs_FailInsert ON dbo.AdminAuditLogs AFTER INSERT AS BEGIN RAISERROR ('Forced audit failure', 16, 1); END");
            }

            await using (var db = new CompareShopDbContext(options))
                await Assert.ThrowsAsync<DbUpdateException>(() => new EfUserProvisioning(db, TimeProvider.System).GetOrCreateForAdmin(subject, default));

            await using var verify = new CompareShopDbContext(options);
            Assert.False(await verify.Users.AnyAsync(user => user.KeycloakSubject == subject));
            Assert.False(await verify.AdminAuditLogs.AnyAsync(entry => entry.ActorSubject == subject));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private string IsolatedConnection(string prefix) => new SqlConnectionStringBuilder(fixture.ConnectionString)
    {
        InitialCatalog = $"{prefix}_{Guid.NewGuid():N}"
    }.ConnectionString;

    private sealed class FirstLookupBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothLookupsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _lookupCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Users]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _lookupCount) <= 2)
            {
                if (Volatile.Read(ref _lookupCount) == 2) _bothLookupsStarted.TrySetResult();
                await _bothLookupsStarted.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
