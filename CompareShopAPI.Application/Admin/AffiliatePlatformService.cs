using System.Text.Json.Serialization;

namespace CompareShopAPI.Application.Admin;

public sealed record AffiliatePlatformInput(string? Name, string? HomeUrl, string? Notes);
public sealed record AffiliatePlatformRecord(int Id, string Name, string? HomeUrl, string? Notes, DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
{
    [JsonIgnore]
    public string NormalizedName { get; init; } = string.Empty;
}
public sealed record AffiliatePlatformImportError(int Index, IReadOnlyList<string> Errors);
public sealed record AffiliatePlatformImportPreview(int CreateCount, int UpdateCount, IReadOnlyList<AffiliatePlatformImportError> Errors);
public sealed record AffiliatePlatformImportResult(int CreatedCount, int UpdatedCount, int FailedCount, IReadOnlyList<AffiliatePlatformImportError> Errors);
public sealed record AffiliatePlatformDeleteResult(int Id, string Status);
public sealed record AffiliatePlatformBulkDeleteInput(IReadOnlyList<int>? Ids);
public sealed record AffiliatePlatformNormalizedInput(int Index, string Name, string NormalizedName, string? HomeUrl, string? Notes);
public sealed record AffiliatePlatformImportWriteResult(int CreatedCount, int UpdatedCount);

public sealed class AffiliatePlatformDuplicateException() : Exception("An affiliate platform with this name already exists.");

public interface IAffiliatePlatformStore
{
    Task<IReadOnlyList<AffiliatePlatformRecord>> List(CancellationToken ct);
    Task<AffiliatePlatformRecord?> Find(int id, CancellationToken ct);
    Task<AffiliatePlatformRecord?> FindByName(string normalizedName, int? excludingId, CancellationToken ct);
    Task<AffiliatePlatformRecord> Save(int? id, AffiliatePlatformNormalizedInput input, DateTime nowUtc, CancellationToken ct);
    Task<bool> Delete(int id, CancellationToken ct);
    Task<IReadOnlyList<AffiliatePlatformDeleteResult>> BulkDelete(IReadOnlyList<int> ids, CancellationToken ct);
    Task<IReadOnlyDictionary<string, AffiliatePlatformRecord>> FindByNames(IReadOnlyCollection<string> normalizedNames, CancellationToken ct);
    Task<AffiliatePlatformImportWriteResult> CommitImport(IReadOnlyList<AffiliatePlatformNormalizedInput> inputs, DateTime nowUtc, CancellationToken ct);
}

public sealed class AffiliatePlatformService(IAffiliatePlatformStore store, IAdminPermissionReader permissions, TimeProvider clock)
{
    private static readonly string[] ImportPermissions =
        [AdminPermissions.AffiliatePlatformsCreate, AdminPermissions.AffiliatePlatformsUpdate];

    public async Task<IReadOnlyList<AffiliatePlatformRecord>> List(string? subject, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.AffiliatePlatformsRead, ct);
        return await store.List(ct);
    }

    public async Task<AffiliatePlatformRecord> Create(string? subject, AffiliatePlatformInput input, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.AffiliatePlatformsCreate, ct);
        var normalized = Validate(input, 0);
        if (await store.FindByName(normalized.NormalizedName, null, ct) is not null) throw new AffiliatePlatformDuplicateException();
        return await SaveUnique(null, normalized, ct);
    }

    public async Task<AffiliatePlatformRecord?> Update(string? subject, int id, AffiliatePlatformInput input, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.AffiliatePlatformsUpdate, ct);
        if (await store.Find(id, ct) is null) return null;
        var normalized = Validate(input, 0);
        if (await store.FindByName(normalized.NormalizedName, id, ct) is not null) throw new AffiliatePlatformDuplicateException();
        return await SaveUnique(id, normalized, ct);
    }

    public async Task<bool> Delete(string? subject, int id, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.AffiliatePlatformsDelete, ct);
        return await store.Delete(id, ct);
    }

    public async Task<IReadOnlyList<AffiliatePlatformDeleteResult>> BulkDelete(string? subject, IReadOnlyList<int>? ids, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.AffiliatePlatformsDelete, ct);
        if (ids is null) throw new ArgumentException("IDs are required.");
        if (ids.Any(x => x <= 0)) throw new ArgumentException("IDs must be positive.");
        return await store.BulkDelete(ids.Distinct().ToArray(), ct);
    }

    public async Task<AffiliatePlatformImportPreview> Preview(string? subject, IReadOnlyList<AffiliatePlatformInput>? rows, CancellationToken ct)
    {
        await AuthorizeAll(subject, ImportPermissions, ct);
        var validated = ValidateRows(rows);
        var existing = await store.FindByNames(validated.Valid.Select(x => x.NormalizedName).Distinct(StringComparer.Ordinal).ToArray(), ct);
        return new(validated.Valid.Count(x => !existing.ContainsKey(x.NormalizedName)),
            validated.Valid.Count(x => existing.ContainsKey(x.NormalizedName)), validated.Errors);
    }

    public async Task<AffiliatePlatformImportResult> Commit(string? subject, IReadOnlyList<AffiliatePlatformInput>? rows, CancellationToken ct)
    {
        await AuthorizeAll(subject, ImportPermissions, ct);
        var validated = ValidateRows(rows);
        var write = await store.CommitImport(validated.Valid, clock.GetUtcNow().UtcDateTime, ct);
        return new(write.CreatedCount, write.UpdatedCount, validated.Errors.Count, validated.Errors);
    }

    private Task<AffiliatePlatformRecord> SaveUnique(int? id, AffiliatePlatformNormalizedInput input, CancellationToken ct) =>
        store.Save(id, input, clock.GetUtcNow().UtcDateTime, ct);

    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        if (!await permissions.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }

    private async Task AuthorizeAll(string? subject, IReadOnlyList<string> required, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        foreach (var permission in required)
            if (!await permissions.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
    }

    private static (IReadOnlyList<AffiliatePlatformNormalizedInput> Valid, IReadOnlyList<AffiliatePlatformImportError> Errors) ValidateRows(IReadOnlyList<AffiliatePlatformInput>? rows)
    {
        if (rows is null) throw new ArgumentException("The request body must be a JSON array.");
        var valid = new List<AffiliatePlatformNormalizedInput>();
        var errors = new List<AffiliatePlatformImportError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            try
            {
                var item = Validate(rows[index], index);
                if (!seen.Add(item.NormalizedName)) throw new ArgumentException("Name duplicates another row in this import.");
                valid.Add(item);
            }
            catch (ArgumentException ex) { errors.Add(new(index, [ex.Message])); }
        }
        return (valid, errors);
    }

    private static AffiliatePlatformNormalizedInput Validate(AffiliatePlatformInput? input, int index)
    {
        if (input is null) throw new ArgumentException("A row is required.");
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name is required.");
        if (name.Length > 300) throw new ArgumentException("Name must be at most 300 characters.");
        var normalized = name.ToUpperInvariant();
        if (normalized.Length > 300) throw new ArgumentException("The normalized name must be at most 300 characters.");
        var homeUrl = string.IsNullOrWhiteSpace(input.HomeUrl) ? null : input.HomeUrl.Trim();
        if (homeUrl?.Length > 2048) throw new ArgumentException("HomeUrl must be at most 2048 characters.");
        if (homeUrl is not null && (!Uri.TryCreate(homeUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Scheme) || !Uri.IsWellFormedUriString(homeUrl, UriKind.Absolute)))
            throw new ArgumentException("HomeUrl must be an absolute URI.");
        return new(index, name, normalized, homeUrl, input.Notes);
    }
}
