using System.Text.Json;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Catalog;

public sealed class EfCountryData(CompareShopDbContext db, TimeProvider clock) : ICountryCatalog, ICountryAdminData
{
    public async Task<IReadOnlyList<CountryDto>> ActiveCountries(CancellationToken ct) =>
        await db.Countries.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code)
            .Select(x => new CountryDto(x.Id, x.Code, x.Name)).ToListAsync(ct);

    public Task<int?> ActiveCountryId(string code, CancellationToken ct) => db.Countries.AsNoTracking()
        .Where(x => x.Code == code && x.IsActive).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AdminCountryDto>> List(CancellationToken ct) =>
        await db.Countries.AsNoTracking().OrderBy(x => x.Code)
            .Select(x => new AdminCountryDto(x.Id, x.Code, x.Name, x.IsActive)).ToListAsync(ct);

    public Task<AdminCountryDto?> Get(int id, CancellationToken ct) => db.Countries.AsNoTracking().Where(x => x.Id == id)
        .Select(x => new AdminCountryDto(x.Id, x.Code, x.Name, x.IsActive)).SingleOrDefaultAsync(ct);

    public async Task<AdminCountryDto> Save(int? id, CountryInput input, string actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = id.HasValue ? await db.Countries.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (id.HasValue && item is null) throw new KeyNotFoundException("Country not found.");
        if (await db.Countries.AnyAsync(x => x.Code == input.Code && (!id.HasValue || x.Id != id.Value), ct)) throw new CountryCodeConflictException();
        var before = item is null ? null : new AdminCountryDto(item.Id, item.Code, item.Name, item.IsActive);
        item ??= new Country(input.Code, input.Name, input.IsActive);
        item.Update(input.Code, input.Name, input.IsActive);
        if (!id.HasValue) db.Countries.Add(item);
        await db.SaveChangesAsync(ct);
        var after = new AdminCountryDto(item.Id, item.Code, item.Name, item.IsActive);
        db.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = actor, Action = id.HasValue ? "update" : "create", ResourceType = "country", ResourceId = item.Id.ToString(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime, BeforeJson = before is null ? null : JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return after;
    }

    public async Task<bool> Delete(int id, string actor, CancellationToken ct)
    {
        var item = await db.Countries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return false;
        if (await db.PartnerShops.AnyAsync(x => x.CountryId == id, ct) || await db.Categories.AnyAsync(x => x.CountryId == id, ct) || await db.BannedItems.AnyAsync(x => x.CountryId == id, ct))
            throw new CountryInUseException();
        db.Countries.Remove(item);
        db.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = actor, Action = "delete", ResourceType = "country", ResourceId = id.ToString(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime, BeforeJson = JsonSerializer.Serialize(new AdminCountryDto(item.Id, item.Code, item.Name, item.IsActive)) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 })
        {
            throw new CountryInUseException();
        }
        return true;
    }
}
