using System.Net;
using System.Net.Http.Headers;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CountryAdminServiceTests
{
    [Fact]
    public async Task Admin_service_normalizes_inputs_and_enforces_permissions()
    {
        var data = new FakeCountryAdminData();
        var service = new CountryAdminService(data, new FakePermissionReader(true));

        Assert.Empty(await service.List("admin", default));
        Assert.Null(await service.Get("admin", 42, default));
        var created = await service.Create("admin", new CountryInput(" id ", " Indonesia "), default);
        Assert.Equal(new AdminCountryDto(1, "ID", "Indonesia", true), created);
        var updated = await service.Update("admin", created.Id, new CountryInput("ZZ", "Indonesia", false), default);
        Assert.Equal("ID", updated.Code);
        Assert.False(updated.IsActive);
        await service.Delete("admin", created.Id, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Delete("admin", 99, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Update("admin", 99, new CountryInput("ID", "Missing"), default));

        var missingSubject = new CountryAdminService(data, new FakePermissionReader(true));
        await Assert.ThrowsAsync<AdminUnauthorizedException>(() => missingSubject.List(null, default));
        var forbidden = new CountryAdminService(data, new FakePermissionReader(false));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => forbidden.Create("viewer", new CountryInput("SG", "Singapore"), default));
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("XK")]
    [InlineData("A1")]
    [InlineData("USA")]
    [InlineData("")]
    public void Country_rejects_codes_outside_iso_alpha2(string code) =>
        Assert.Throws<ArgumentException>(() => new Country(code, "Invalid code"));

    private sealed class FakeCountryAdminData : ICountryAdminData
    {
        private AdminCountryDto? _country;
        public Task<IReadOnlyList<AdminCountryDto>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<AdminCountryDto>>(_country is null ? [] : [_country]);
        public Task<AdminCountryDto?> Get(int id, CancellationToken ct) => Task.FromResult(_country?.Id == id ? _country : null);
        public Task<AdminCountryDto> Save(int? id, CountryInput input, string actor, CancellationToken ct)
        {
            _country = new AdminCountryDto(id ?? 1, input.Code, input.Name, input.IsActive);
            return Task.FromResult(_country);
        }
        public Task<bool> Delete(int id, string actor, CancellationToken ct)
        {
            if (_country?.Id != id) return Task.FromResult(false);
            _country = null;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePermissionReader(bool allowed) : IAdminPermissionReader
    {
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(allowed);
    }
}

[Collection(SqlServerTestCollection.Name)]
public sealed class EfCountryDataTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Deleting_referenced_country_returns_conflict_and_preserves_country()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CountryDelete_{Guid.NewGuid():N}" }.ConnectionString;
        var raceInterceptor = new InsertCountryReferenceOnSaveInterceptor(connection);
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).AddInterceptors(raceInterceptor).Options;
        var subject = $"country-admin-{Guid.NewGuid():N}";
        int countryId;
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await MigrationTestHelpers.MigrateToLatestAsync(db);
                var country = new Country("US", "United States");
                db.Countries.Add(country);
                await db.SaveChangesAsync();
                countryId = country.Id;
                db.PartnerShops.Add(new PartnerShop { Slug = "country-delete-reference", Name = "Referenced", CountryId = country.Id });

                var permission = await db.AdminPermissions.SingleAsync(x => x.Key == "countries.manage");
                var role = new AdminRole { Name = $"Country Manager {Guid.NewGuid():N}" };
                var user = new User(subject, DateTime.UtcNow);
                db.AddRange(role, user);
                await db.SaveChangesAsync();
                db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
                db.AdminUserRoles.Add(new AdminUserRole { RoleId = role.Id, UserId = user.Id });
                await db.SaveChangesAsync();
            }

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));
            using var response = await client.DeleteAsync($"/admin/countries/{countryId}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await using var verify = new CompareShopDbContext(options);
            var persisted = await verify.Countries.AsNoTracking().SingleAsync(x => x.Id == countryId);
            Assert.Equal("US", persisted.Code);
            Assert.Equal("United States", persisted.Name);
            Assert.True(persisted.IsActive);

            await using (var race = new CompareShopDbContext(options))
            {
                race.PartnerShops.Remove(await race.PartnerShops.SingleAsync(x => x.Slug == "country-delete-reference"));
                await race.SaveChangesAsync();
                raceInterceptor.Arm();
                var data = new EfCountryData(race, new FixedClock());
                await Assert.ThrowsAsync<CountryInUseException>(() => data.Delete(countryId, subject, default));
            }

            await using var raceVerify = new CompareShopDbContext(options);
            Assert.True(await raceVerify.Countries.AnyAsync(x => x.Id == countryId));
            Assert.True(await raceVerify.PartnerShops.AnyAsync(x => x.Slug == "country-delete-race-reference" && x.CountryId == countryId));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Country_reads_writes_audit_and_delete_guards_work_in_sql_server()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CountryAdmin_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await MigrationTestHelpers.MigrateToLatestAsync(db);
            var data = new EfCountryData(db, new FixedClock());
            Assert.Equal(new[] { "ID", "VN" }, (await data.ActiveCountries(default)).Select(x => x.Code));
            Assert.Null(await data.ActiveCountryId("XX", default));

            var created = await data.Save(null, new CountryInput("SG", "Singapore"), "admin", default);
            Assert.Equal("SG", created.Code);
            Assert.Equal(created.Id, await data.ActiveCountryId("SG", default));
            Assert.Equal(created, await data.Get(created.Id, default));
            Assert.Equal(3, (await data.List(default)).Count);
            await Assert.ThrowsAsync<CountryCodeConflictException>(() => data.Save(null, new CountryInput("sg", "Duplicate"), "admin", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => data.Save(int.MaxValue, new CountryInput("SG", "Missing"), "admin", default));
            var updated = await data.Save(created.Id, new CountryInput("sg", "Singapore Republic", false), "admin", default);
            Assert.Equal("Singapore Republic", updated.Name);
            Assert.Null(await data.ActiveCountryId("SG", default));

            var shop = new PartnerShop { Slug = "country-referenced-shop", Name = "Referenced", CountryId = created.Id };
            db.PartnerShops.Add(shop);
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<CountryInUseException>(() => data.Delete(created.Id, "admin", default));
            db.PartnerShops.Remove(shop);
            await db.SaveChangesAsync();
            Assert.True(await data.Delete(created.Id, "admin", default));
            Assert.False(await data.Delete(created.Id, "admin", default));
            Assert.Equal(new[] { "create", "update", "delete" }, await db.AdminAuditLogs.Where(x => x.ResourceType == "country").OrderBy(x => x.Id).Select(x => x.Action).ToArrayAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InsertCountryReferenceOnSaveInterceptor(string connectionString) : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_armed) return result;
            _armed = false;
            var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connectionString).Options;
            await using var concurrent = new CompareShopDbContext(options);
            var country = await concurrent.Countries.AsNoTracking().SingleAsync(x => x.Code == "US", cancellationToken);
            concurrent.PartnerShops.Add(new PartnerShop { Slug = "country-delete-race-reference", Name = "Concurrent reference", CountryId = country.Id });
            await concurrent.SaveChangesAsync(cancellationToken);
            return result;
        }
    }
}
