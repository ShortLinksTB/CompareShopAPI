using CompareShopAPI.Application.Files;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CompareShopAPI.Infrastructure.Files;

public sealed class EfFileRepository(CompareShopDbContext db) : IFileRepository
{
    public async Task<FilePage> List(int page, int pageSize, string? query, string? type, CancellationToken ct)
    {
        var files = db.Files.AsNoTracking().Where(file => file.LifecycleState == FileLifecycleState.Active);
        if (!string.IsNullOrWhiteSpace(query)) files = files.Where(file => file.OriginalFileName.Contains(query));
        if (type == "images") files = files.Where(file => file.ContentType.StartsWith("image/"));
        if (type == "other") files = files.Where(file => !file.ContentType.StartsWith("image/"));
        var count = await files.CountAsync(ct);
        var rows = await files.OrderByDescending(file => file.UploadedAtUtc).ThenBy(file => file.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(file => new
            {
                file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Width, file.Height, file.UploadedBy, file.UploadedAtUtc,
                file.DerivedFromFileId, SourceName = file.DerivedFromFile == null ? null : file.DerivedFromFile.OriginalFileName,
                file.DerivedPreset, file.DerivativeSpecJson
            }).ToListAsync(ct);
        var usages = await LoadUsages(rows.Select(x => x.Id).ToArray(), ct);
        var items = rows.Select(file => new FileMetadata(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Width,
            file.Height, file.UploadedBy, file.UploadedAtUtc,
            file.DerivedFromFileId is null ? null : new DerivedFromMetadata(file.DerivedFromFileId.Value, file.SourceName ?? string.Empty),
            file.DerivedPreset, file.DerivativeSpecJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<DerivativeSpec>(file.DerivativeSpecJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), usages.GetValueOrDefault(file.Id, []))).ToArray();
        return new(items, page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize));
    }

    public Task<FileAsset?> Get(Guid id, CancellationToken ct) => db.Files.AsNoTracking().Include(file => file.DerivedFromFile)
        .SingleOrDefaultAsync(file => file.Id == id && file.LifecycleState == FileLifecycleState.Active, ct);
    public Task<FileAsset?> FindDerivative(Guid sourceId, string preset, string derivativeSpecJson, CancellationToken ct) => db.Files.AsNoTracking()
        .Include(file => file.DerivedFromFile).Where(file => file.LifecycleState == FileLifecycleState.Active
            && file.DerivedFromFileId == sourceId && file.DerivedPreset == preset && file.DerivativeSpecJson == derivativeSpecJson)
        .OrderBy(file => file.UploadedAtUtc).ThenBy(file => file.Id).FirstOrDefaultAsync(ct);
    public async Task Add(FileAsset file, CancellationToken ct) { db.Files.Add(file); await db.SaveChangesAsync(ct); }
    public async Task ReserveDerivativeAndAdd(FileAsset file, string candidateName, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (file.DerivedFromFileId is not null)
        {
            await AcquireFileLocks([file.DerivedFromFileId.Value], ct);
            await VerifyActiveFiles([file.DerivedFromFileId.Value], ct);
        }
        var normalized = candidateName.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
        var lockName = "FileName:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @result;";
        var resource = command.CreateParameter(); resource.ParameterName = "@resource"; resource.Value = lockName; command.Parameters.Add(resource);
        var lockResult = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (lockResult < 0) throw new TimeoutException("Could not reserve the derivative filename.");
        var allNames = await db.Files.Select(x => x.OriginalFileName).ToListAsync(ct);
        if (allNames.Any(name => string.Equals(name.Normalize(System.Text.NormalizationForm.FormC), candidateName.Normalize(System.Text.NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)))
            throw new DerivativeNameCollisionException();
        try
        {
            db.Files.Add(file);
            db.AdminAuditLogs.Add(new CompareShopAPI.Domain.Admin.AdminAuditLog
            {
                ActorSubject = file.UploadedBy, Action = "create_derivative", ResourceType = "file", ResourceId = file.Id.ToString(),
                CreatedAtUtc = file.UploadedAtUtc, AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    sourceFileId = file.DerivedFromFileId, file.Id, file.OriginalFileName, file.Width, file.Height,
                    file.DerivedPreset, parameters = file.DerivativeSpecJson, result = "success"
                })
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
    public async Task AuditDerivative(string actor, Guid sourceId, int variantIndex, string parameters, string? resultFileId, string? errorCode, CancellationToken ct)
    {
        db.AdminAuditLogs.Add(new CompareShopAPI.Domain.Admin.AdminAuditLog
        {
            ActorSubject = actor, Action = "create_derivative", ResourceType = "file", ResourceId = sourceId.ToString(),
            CreatedAtUtc = DateTime.UtcNow, AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceFileId = sourceId, variantIndex, parameters, resultFileId, error = errorCode,
                result = errorCode is null ? "success" : "failure"
            })
        });
        await db.SaveChangesAsync(ct);
    }
    public async Task<FileDeletePreparation> BeginDelete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AcquireFileLocks([id], ct);
        var file = await db.Files.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (file is null) { await tx.CommitAsync(ct); return new("not_found", null, []); }
        if (file.LifecycleState == FileLifecycleState.Deleted) { await tx.CommitAsync(ct); return new("deleted", file.ObjectKey, []); }
        if (file.LifecycleState == FileLifecycleState.Deleting) { await tx.CommitAsync(ct); return new("deleting", file.ObjectKey, []); }
        var usedBy = await LoadUsages([id], ct);
        if (usedBy.TryGetValue(id, out var references) && references.Count > 0)
        {
            await tx.CommitAsync(ct);
            return new("in_use", file.ObjectKey, references);
        }
        file.BeginDeletion();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new("deleting", file.ObjectKey, []);
    }

    public async Task<bool> FinalizeDelete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AcquireFileLocks([id], ct);
        var file = await db.Files.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (file is null) { await tx.CommitAsync(ct); return true; }
        if (file.LifecycleState == FileLifecycleState.Deleted) { await tx.CommitAsync(ct); return true; }
        if (file.LifecycleState != FileLifecycleState.Deleting) { await tx.RollbackAsync(ct); return false; }
        file.CompleteDeletion();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<(Guid Id, string ObjectKey)>> DeletingFiles(CancellationToken ct) =>
        await db.Files.AsNoTracking().Where(x => x.LifecycleState == FileLifecycleState.Deleting)
            .OrderBy(x => x.UploadedAtUtc).Select(x => new ValueTuple<Guid, string>(x.Id, x.ObjectKey)).ToListAsync(ct);

    internal async Task AcquireFileLocks(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        foreach (var id in ids.Where(x => x != Guid.Empty).Distinct().Order())
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT [Id] FROM [Files] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id;";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@id"; parameter.Value = id; command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync(ct);
        }
    }

    internal async Task VerifyActiveFiles(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        var distinct = ids.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0) return;
        var active = await db.Files.Where(x => distinct.Contains(x.Id) && x.LifecycleState == FileLifecycleState.Active).Select(x => x.Id).ToListAsync(ct);
        if (active.Count != distinct.Length) throw new InvalidOperationException("A referenced file is unavailable or is being deleted.");
    }

    private async Task<Dictionary<Guid, IReadOnlyList<FileUsage>>> LoadUsages(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var usage = ids.ToDictionary(x => x, _ => new List<FileUsage>());
        var products = await (from image in db.ProductImages.AsNoTracking()
                              join product in db.Products.AsNoTracking() on image.ProductId equals product.Id
                              where ids.Contains(image.FileId ?? Guid.Empty) || ids.Contains(image.Card600FileId ?? Guid.Empty)
                                  || ids.Contains(image.Detail1000FileId ?? Guid.Empty)
                              select new { image.ProductId, image.FileId, image.Card600FileId, image.Detail1000FileId, ProductName = product.Name })
            .ToListAsync(ct);
        foreach (var row in products)
        foreach (var fileId in new[] { row.FileId, row.Card600FileId, row.Detail1000FileId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct())
            if (usage.TryGetValue(fileId, out var list)) list.Add(new("product", row.ProductId.ToString(), $"Sản phẩm #{row.ProductId}: {row.ProductName}"));
        var derivatives = await db.Files.AsNoTracking().Where(x => x.LifecycleState != FileLifecycleState.Deleted
                && x.DerivedFromFileId.HasValue && ids.Contains(x.DerivedFromFileId.Value))
            .Select(x => new { x.Id, x.OriginalFileName, x.DerivedFromFileId }).ToListAsync(ct);
        foreach (var row in derivatives)
            if (usage.TryGetValue(row.DerivedFromFileId!.Value, out var list)) list.Add(new("derivative", row.Id.ToString("D"), $"Derivative: {row.OriginalFileName}"));
        var articles = await db.Articles.AsNoTracking().Where(x => x.CoverFileId.HasValue && ids.Contains(x.CoverFileId.Value))
            .Select(x => new { x.Id, x.Title, x.CoverFileId }).ToListAsync(ct);
        foreach (var row in articles)
            if (usage.TryGetValue(row.CoverFileId!.Value, out var list)) list.Add(new("blog", row.Id.ToString(), $"Blog: {row.Title}"));
        return usage.ToDictionary(x => x.Key, x => (IReadOnlyList<FileUsage>)x.Value.Distinct().ToArray());
    }
}

public sealed class AdminFilePermissionGate(IAdminPermissionReader permissions) : IFilePermissionGate
{
    public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => permissions.HasPermission(subject, permission, ct);
}
