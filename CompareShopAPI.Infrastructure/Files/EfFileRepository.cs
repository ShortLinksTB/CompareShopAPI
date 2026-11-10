using CompareShopAPI.Application.Files;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
        var items = await files.OrderByDescending(file => file.UploadedAtUtc).ThenBy(file => file.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(file => new FileMetadata(file.Id, file.OriginalFileName,
                file.ContentType, file.SizeBytes, file.Width, file.Height, file.UploadedBy, file.UploadedAtUtc)).ToListAsync(ct);
        return new(items, page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize));
    }

    public Task<FileAsset?> Get(Guid id, CancellationToken ct) => db.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id, ct);
    public async Task Add(FileAsset file, CancellationToken ct) { db.Files.Add(file); await db.SaveChangesAsync(ct); }
    public async Task<bool> Delete(Guid id, CancellationToken ct) => await db.Files.Where(file => file.Id == id).ExecuteDeleteAsync(ct) > 0;
}

public sealed class AdminFilePermissionGate(IAdminPermissionReader permissions) : IFilePermissionGate
{
    public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => permissions.HasPermission(subject, permission, ct);
}
