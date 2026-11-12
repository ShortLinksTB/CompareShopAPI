using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AuthSubjectMigrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Rollback_is_rejected_without_changing_users_when_a_user_has_no_Keycloak_identity()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopAuthSubjectMigration_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        const string authSubject = "rollback-auth-subject";

        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Users.Add(User.ForAuthSubject(authSubject, DateTime.UtcNow));
                await db.SaveChangesAsync();

                await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync("20261111100000_AddMatchingDecisions"));
            }

            await using var verify = new CompareShopDbContext(options);
            var user = await verify.Users.SingleAsync(candidate => candidate.AuthSubject == authSubject);
            Assert.Null(user.KeycloakSubject);
            Assert.Equal(authSubject, user.AuthSubject);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
