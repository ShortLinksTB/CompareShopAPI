using System.Data;
using System.Reflection;
using System.Text.Json;
using CompareShopAPI.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Persistence;

public static class CountrySeed
{
    private sealed record InitialCountry(string Code, string Name, bool IsActive = true);

    public static async Task SeedInitialAsync(CompareShopDbContext db, Assembly sourceAssembly, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var stream = sourceAssembly.GetManifestResourceStream("CompareShopAPI.Api.countries.initial.json")
            ?? throw new InvalidOperationException("Embedded Country seed resource countries.initial.json was not found.");
        var countries = await JsonSerializer.DeserializeAsync<InitialCountry[]>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
            ?? throw new InvalidOperationException("Embedded Country seed resource is empty or invalid.");
        foreach (var initial in countries)
        {
            var code = initial.Code.Trim().ToUpperInvariant();
            if (!await db.Countries.AnyAsync(country => country.Code == code, ct))
                db.Countries.Add(new Country(code, initial.Name, initial.IsActive));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
