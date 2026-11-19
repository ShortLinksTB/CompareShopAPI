using CompareShopAPI.Domain.Catalog;

namespace CompareShopAPI.Application.Catalog;

public sealed record CountryDto(int Id, string Code, string Name);
public sealed record CountryInput(string Code, string Name, bool IsActive = true);
public sealed class CountryUnavailableException(string code) : Exception($"Country '{code}' is unknown or inactive.");
public sealed class CountryCodeConflictException() : Exception("A Country with this code already exists.");
public sealed class CountryInUseException() : Exception("Country is referenced by catalog records and cannot be deleted.");

public interface ICountryCatalog
{
    Task<IReadOnlyList<CountryDto>> ActiveCountries(CancellationToken ct);
    Task<int?> ActiveCountryId(string code, CancellationToken ct);
}

public sealed class CountrySelectionService(ICountryCatalog countries)
{
    public async Task<int> Resolve(string? code, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "VN" : code.Trim().ToUpperInvariant();
        if (normalized.Length != 2 || await countries.ActiveCountryId(normalized, ct) is not int id)
            throw new CountryUnavailableException(normalized);
        return id;
    }
}

public sealed record AdminCountryDto(int Id, string Code, string Name, bool IsActive);
public interface ICountryAdminData
{
    Task<IReadOnlyList<AdminCountryDto>> List(CancellationToken ct);
    Task<AdminCountryDto?> Get(int id, CancellationToken ct);
    Task<AdminCountryDto> Save(int? id, CountryInput input, string actor, CancellationToken ct);
    Task<bool> Delete(int id, string actor, CancellationToken ct);
}

public sealed class CountryAdminService(ICountryAdminData data, CompareShopAPI.Application.Admin.IAdminPermissionReader permissions)
{
    public async Task<IReadOnlyList<AdminCountryDto>> List(string? subject, CancellationToken ct) { await Authorize(subject, "countries.read", ct); return await data.List(ct); }
    public async Task<AdminCountryDto?> Get(string? subject, int id, CancellationToken ct) { await Authorize(subject, "countries.read", ct); return await data.Get(id, ct); }
    public async Task<AdminCountryDto> Create(string? subject, CountryInput input, CancellationToken ct) { var actor = await Authorize(subject, "countries.manage", ct); var country = new Country(input.Code, input.Name, input.IsActive); return await data.Save(null, new(country.Code, country.Name, country.IsActive), actor, ct); }
    public async Task<AdminCountryDto> Update(string? subject, int id, CountryInput input, CancellationToken ct) { var actor = await Authorize(subject, "countries.manage", ct); var country = new Country(input.Code, input.Name, input.IsActive); return await data.Save(id, new(country.Code, country.Name, country.IsActive), actor, ct); }
    public async Task Delete(string? subject, int id, CancellationToken ct) { var actor = await Authorize(subject, "countries.manage", ct); if (!await data.Delete(id, actor, ct)) throw new KeyNotFoundException("Country not found."); }
    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new CompareShopAPI.Application.Admin.AdminUnauthorizedException();
        if (!await permissions.HasPermission(subject, permission, ct)) throw new CompareShopAPI.Application.Admin.AdminForbiddenException();
        return subject;
    }
}
