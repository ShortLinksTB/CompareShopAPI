using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
        var updated = await service.Update("admin", created.Id, new CountryInput("id", "Indonesia", false), default);
        Assert.False(updated.IsActive);
        await service.Delete("admin", created.Id, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Delete("admin", 99, default));

        var missingSubject = new CountryAdminService(data, new FakePermissionReader(true));
        await Assert.ThrowsAsync<AdminUnauthorizedException>(() => missingSubject.List(null, default));
        var forbidden = new CountryAdminService(data, new FakePermissionReader(false));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => forbidden.Create("viewer", new CountryInput("SG", "Singapore"), default));
    }

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
}
