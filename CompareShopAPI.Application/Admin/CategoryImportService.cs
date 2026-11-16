using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompareShopAPI.Application.Admin;

public sealed class CategoryImportFileTooLargeException() : Exception("Import file exceeds the 1 MiB limit.");
public sealed class CategoryImportConflictException(IReadOnlyList<CategoryImportBlocker> blockers) : Exception("One or more omitted categories still contain products.")
{
    public IReadOnlyList<CategoryImportBlocker> Blockers { get; } = blockers;
}

public sealed class CategoryImportValidationException(IReadOnlyList<string> errors) : Exception("Category import validation failed.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class CategoryImportService(IAdminData data, TimeProvider? clock = null)
{
    public const int MaximumFileBytes = 1024 * 1024;
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public async Task<CategoryExportDocument> Export(string? subject, CancellationToken ct)
    {
        await Authorize(subject, [AdminPermissions.CategoriesRead], ct);
        var rows = await data.CategoryImportSnapshot(ct);
        return new(1, Clock.GetUtcNow(), rows.OrderBy(x => x.Slug, StringComparer.Ordinal)
            .Select(x => new CategoryExportRow(x.Id, x.Slug, x.Name, x.ParentSlug, x.SortOrder)).ToArray());
    }

    public async Task<CategoryImportPreview> DryRun(string? subject, byte[] content, CancellationToken ct)
    {
        await Authorize(subject, WritePermissions, ct);
        var parsed = Parse(content);
        if (parsed.Errors.Count != 0) return EmptyInvalid(parsed.Errors);
        return BuildPreview(parsed.Document!, await data.CategoryImportSnapshot(ct));
    }

    public async Task<CategoryImportCommitResult> Commit(string? subject, string filename, string? confirmation, byte[] content, CancellationToken ct)
    {
        var actor = await Authorize(subject, WritePermissions, ct);
        if (!string.Equals(confirmation, "GHI ĐÈ", StringComparison.Ordinal)) throw new ArgumentException("Confirmation must exactly match GHI ĐÈ.");
        var parsed = Parse(content);
        if (parsed.Errors.Count != 0) throw new CategoryImportValidationException(parsed.Errors);
        return await data.ExecuteAudited(actor, "replace_all", "categories", null, async token =>
        {
            var snapshot = await data.CategoryImportSnapshot(token);
            var preview = BuildPreview(parsed.Document!, snapshot);
            if (!preview.Valid) throw new CategoryImportValidationException(preview.Errors);
            if (preview.Blocked) throw new CategoryImportConflictException(preview.Blockers);
            var before = snapshot.Select(x => new { x.Slug, x.Name, x.ParentSlug }).ToArray();
            var omitted = snapshot.Where(x => !parsed.Document!.Categories!.Any(row => row.Slug == x.Slug)).ToArray();
            await data.ApplyCategoryImport(parsed.Document!.Categories!, omitted.Select(x => x.Id).ToArray(), token);
            var creates = preview.Changes.Where(x => x.Action == "create").Select(x => x.Slug).ToArray();
            var updates = preview.Changes.Where(x => x.Action == "update").Select(x => x.Slug).ToArray();
            var deletes = preview.Changes.Where(x => x.Action == "delete").Select(x => x.Slug).ToArray();
            var result = new CategoryImportCommitResult(preview.Summary, preview.Changes);
            var audit = new CategoryImportAudit(creates.Length, updates.Length, deletes.Length, creates, updates, deletes, filename);
            return (result, before, audit);
        }, ct);
    }

    private static readonly string[] WritePermissions = [AdminPermissions.CategoriesCreate, AdminPermissions.CategoriesUpdate, AdminPermissions.CategoriesDelete];
    private async Task<string> Authorize(string? subject, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        foreach (var permission in permissions)
            if (!await data.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }

    private static (CategoryImportJson? Document, IReadOnlyList<string> Errors) Parse(byte[] content)
    {
        if (content.Length > MaximumFileBytes) throw new CategoryImportFileTooLargeException();
        try
        {
            var document = JsonSerializer.Deserialize<CategoryImportJson>(content, JsonOptions);
            if (document is null) return (null, ["JSON root must be an object."]);
            return (document, Validate(document));
        }
        catch (JsonException) { return (null, ["File must contain valid JSON."]); }
    }

    private static IReadOnlyList<string> Validate(CategoryImportJson document)
    {
        var errors = new List<string>();
        if (document.SchemaVersion != 1) errors.Add("schemaVersion must be 1.");
        if (document.Categories is null) { errors.Add("categories must be an array."); return errors; }
        var categories = document.Categories;
        for (var i = 0; i < categories.Count; i++)
        {
            var row = categories[i];
            if (row is null) { errors.Add($"categories[{i}] must be an object."); continue; }
            if (string.IsNullOrWhiteSpace(row.Slug) || row.Slug.Length > 160 || !SlugPattern.IsMatch(row.Slug)) errors.Add($"categories[{i}].slug has an invalid format.");
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Trim().Length > 200) errors.Add($"categories[{i}].name must be nonempty and at most 200 characters.");
            if (row.SortOrder.ValueKind != JsonValueKind.Undefined &&
                (row.SortOrder.ValueKind != JsonValueKind.Number || !row.SortOrder.TryGetInt32(out var sortOrder) || sortOrder < 0))
                errors.Add($"categories[{i}].sortOrder must be a non-negative integer when supplied.");
        }
        var validRows = categories.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Slug)).ToArray();
        foreach (var duplicate in validRows.GroupBy(x => x.Slug, StringComparer.Ordinal).Where(x => x.Count() > 1)) errors.Add($"Slug '{duplicate.Key}' is duplicated.");
        var bySlug = validRows.GroupBy(x => x.Slug, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var row in validRows)
            if (row.ParentSlug is not null && !bySlug.ContainsKey(row.ParentSlug)) errors.Add($"Category '{row.Slug}' references missing parentSlug '{row.ParentSlug}'.");
        foreach (var row in validRows)
        {
            var depth = 1;
            var current = row;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (current.ParentSlug is not null && bySlug.TryGetValue(current.ParentSlug, out var parent))
            {
                if (!visited.Add(current.Slug) || visited.Contains(parent.Slug)) { errors.Add($"Category hierarchy contains a cycle at '{current.Slug}'."); break; }
                depth++;
                if (depth > 2) { errors.Add($"Category '{row.Slug}' exceeds the maximum depth of 2."); break; }
                current = parent;
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static CategoryImportPreview BuildPreview(CategoryImportJson document, IReadOnlyList<CategoryImportStoredCategory> existing)
    {
        var errors = Validate(document);
        if (errors.Count > 0) return EmptyInvalid(errors);
        var incoming = document.Categories!;
        var oldBySlug = existing.ToDictionary(x => x.Slug, StringComparer.Ordinal);
        var newBySlug = incoming.ToDictionary(x => x.Slug, StringComparer.Ordinal);
        var changes = new List<CategoryImportChange>();
        foreach (var row in incoming)
        {
            var effectiveSortOrder = row.SortOrder.ValueKind == JsonValueKind.Number && row.SortOrder.TryGetInt32(out var suppliedSortOrder)
                ? suppliedSortOrder
                : oldBySlug.TryGetValue(row.Slug, out var existingCategory) ? existingCategory.SortOrder : 0;
            if (!oldBySlug.TryGetValue(row.Slug, out var old))
                changes.Add(new("create", row.Slug, null, new { row.Slug, row.Name, row.ParentSlug, sortOrder = effectiveSortOrder }));
            else if (old.Name != row.Name || old.ParentSlug != row.ParentSlug || old.SortOrder != effectiveSortOrder)
                changes.Add(new("update", row.Slug, new { old.Slug, old.Name, old.ParentSlug, sortOrder = old.SortOrder }, new { row.Slug, row.Name, row.ParentSlug, sortOrder = effectiveSortOrder }));
        }
        foreach (var old in existing.Where(x => !newBySlug.ContainsKey(x.Slug)).OrderByDescending(x => x.ParentCategoryId.HasValue))
            changes.Add(new("delete", old.Slug, new { old.Slug, old.Name, old.ParentSlug }, null));
        var blockers = existing.Where(x => !newBySlug.ContainsKey(x.Slug) && x.ProductCount > 0)
            .Select(x => new CategoryImportBlocker(x.Slug, x.Name, x.ProductCount)).ToArray();
        return new(true, blockers.Length > 0, [], blockers,
            new(changes.Count(x => x.Action == "create"), changes.Count(x => x.Action == "update"), changes.Count(x => x.Action == "delete")), changes);
    }

    private static CategoryImportPreview EmptyInvalid(IReadOnlyList<string> errors) => new(false, false, errors, [], new(0, 0, 0), []);
}
