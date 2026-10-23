using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminIdentityProvisioningApiTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Admin_boundary_provisions_only_validated_subjects_without_changing_authorization()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CompareShopAdminIdentityApi_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        const string clientAdminRole = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";
        var superAdminSubject = $"root-{Guid.NewGuid():N}";
        var noClientRoleSubject = $"no-client-role-{Guid.NewGuid():N}";
        var noPermissionSubject = $"no-permission-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var root = new User(superAdminSubject, DateTime.UtcNow);
                setup.Users.Add(root);
                await setup.SaveChangesAsync();
                setup.AdminUserRoles.Add(new AdminUserRole { UserId = root.Id, RoleId = 1 });
                await setup.SaveChangesAsync();
            }

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, includeSubject: false));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/me/permissions")).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: noClientRoleSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/me/permissions")).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: noPermissionSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/articles")).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: noClientRoleSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/me/permissions")).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: superAdminSubject));
            using var usersResponse = await client.GetAsync("/admin/users?page=1&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
            using var usersJson = JsonDocument.Parse(await usersResponse.Content.ReadAsStringAsync());
            await using var verify = new CompareShopDbContext(options);
            var noClientRoleId = await verify.Users.Where(user => user.KeycloakSubject == noClientRoleSubject).Select(user => user.Id).SingleAsync();
            var firstPage = usersJson.RootElement;
            var rootId = await verify.Users.Where(user => user.KeycloakSubject == superAdminSubject).Select(user => user.Id).SingleAsync();
            Assert.Equal(1, firstPage.GetProperty("pageNumber").GetInt32());
            Assert.Equal(1, firstPage.GetProperty("pageSize").GetInt32());
            Assert.Equal(3, firstPage.GetProperty("totalItems").GetInt32());
            var firstUser = firstPage.GetProperty("items")[0];
            Assert.Equal(rootId, firstUser.GetProperty("id").GetInt32());
            Assert.Equal(superAdminSubject, firstUser.GetProperty("keycloakSubject").GetString());
            Assert.True(firstUser.TryGetProperty("createdAtUtc", out _));
            Assert.Equal(new[] { "Super Admin" }, firstUser.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray());

            using var secondPageResponse = await client.GetAsync("/admin/users?page=2&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
            using var secondPageJson = JsonDocument.Parse(await secondPageResponse.Content.ReadAsStringAsync());
            var secondPage = secondPageJson.RootElement;
            Assert.Equal(2, secondPage.GetProperty("pageNumber").GetInt32());
            Assert.Equal(1, secondPage.GetProperty("pageSize").GetInt32());
            Assert.Equal(3, secondPage.GetProperty("totalItems").GetInt32());
            var listedUser = secondPage.GetProperty("items")[0];
            Assert.Equal(noClientRoleId, listedUser.GetProperty("id").GetInt32());
            Assert.Equal(noClientRoleSubject, listedUser.GetProperty("keycloakSubject").GetString());
            Assert.Empty(listedUser.GetProperty("roles").EnumerateArray());
            Assert.Equal(1, await verify.AdminAuditLogs.CountAsync(entry => entry.ActorSubject == noClientRoleSubject && entry.Action == "user_provisioned"));
            Assert.Equal(1, await verify.AdminAuditLogs.CountAsync(entry => entry.ActorSubject == noPermissionSubject && entry.Action == "user_provisioned"));
            Assert.Equal(3, await verify.Users.CountAsync());
            Assert.Empty(await verify.AdminUserRoles.Where(role => role.UserId == noClientRoleId).ToListAsync());
            Assert.Equal(1, await verify.Users.CountAsync(user => user.KeycloakSubject == noClientRoleSubject));
            var noPermissionId = await verify.Users.Where(user => user.KeycloakSubject == noPermissionSubject).Select(user => user.Id).SingleAsync();
            Assert.Empty(await verify.AdminUserRoles.Where(role => role.UserId == noPermissionId).ToListAsync());
            Assert.False(await verify.Users.AnyAsync(user => user.KeycloakSubject == "invalid-token"));

            client.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/users")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: noPermissionSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/users")).StatusCode);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
