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
        var files = db.Files.AsNoTracking();
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
        var items = rows.Select(file => new FileMetadata(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Width,
            file.Height, file.UploadedBy, file.UploadedAtUtc,
            file.DerivedFromFileId is null ? null : new DerivedFromMetadata(file.DerivedFromFileId.Value, file.SourceName ?? string.Empty),
            file.DerivedPreset, file.DerivativeSpecJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<DerivativeSpec>(file.DerivativeSpecJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))).ToArray();
        return new(items, page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize));
    }

    public Task<FileAsset?> Get(Guid id, CancellationToken ct) => db.Files.AsNoTracking().Include(file => file.DerivedFromFile).SingleOrDefaultAsync(file => file.Id == id, ct);
    public async Task Add(FileAsset file, CancellationToken ct) { db.Files.Add(file); await db.SaveChangesAsync(ct); }
    public async Task ReserveDerivativeAndAdd(FileAsset file, string candidateName, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
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
    public async Task<bool> Delete(Guid id, CancellationToken ct) => await db.Files.Where(file => file.Id == id).ExecuteDeleteAsync(ct) > 0;
}

public sealed class AdminFilePermissionGate(IAdminPermissionReader permissions) : IFilePermissionGate
{
    public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => permissions.HasPermission(subject, permission, ct);
}
