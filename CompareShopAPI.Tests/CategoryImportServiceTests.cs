using System.Text;
using System.Text.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Api.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Security.Claims;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CategoryImportServiceTests
{
    [Fact]
    public async Task Export_includes_inactive_categories_and_parent_slug()
    {
        await using var db = await CreateDatabase();
        var parent = new Category { Slug = "parent", Name = "Parent", IsActive = false, SortOrder = 12 };
        db.Categories.AddRange(parent, new Category { Slug = "child", Name = "Child", ParentCategory = parent, Level = 2, SortOrder = 4 });
        await db.SaveChangesAsync();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));

        var document = await service.Export("category-reader", default);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("parent", Assert.Single(document.Categories, x => x.Slug == "child").ParentSlug);
        Assert.Equal(12, Assert.Single(document.Categories, x => x.Slug == "parent").SortOrder);
        Assert.Equal(4, Assert.Single(document.Categories, x => x.Slug == "child").SortOrder);
        var exportContext = CreateUploadContext("{}", subject: "category-reader");
        var exportResult = await AdminEndpoints.RunCategoryExport(exportContext, service, default);
        var fileResult = Assert.IsType<FileContentHttpResult>(exportResult);
        Assert.Equal("application/json", fileResult.ContentType);
        Assert.EndsWith(".json", fileResult.FileDownloadName, StringComparison.Ordinal);
        var unauthorizedContext = CreateUploadContext("{}");
        unauthorizedContext.User = new ClaimsPrincipal();
        var unauthorized = await AdminEndpoints.RunCategoryExport(unauthorizedContext, service, default);
        Assert.Equal(StatusCodes.Status401Unauthorized, ((IStatusCodeHttpResult)unauthorized).StatusCode);
        var forbidden = await AdminEndpoints.RunCategoryExport(CreateUploadContext("{}", subject: "category-writer"), service, default);
        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)forbidden).StatusCode);
    }

    [Fact]
    public async Task Dry_run_reports_product_blocker_without_writing()
    {
        await using var db = await CreateDatabase();
        var root = new Category { Slug = "root", Name = "Root" };
        var used = new Category { Slug = "used", Name = "Used", ParentCategory = root, Level = 2 };
        db.Categories.AddRange(root, used);
        db.Products.Add(new Product { Slug = "product", Name = "Product", Category = used });
        await db.SaveChangesAsync();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[{"id":999,"slug":"root","name":"Root","parentSlug":null}]}""");
        var before = await db.Categories.CountAsync();

        var preview = await service.DryRun("category-writer", json, default);

        Assert.True(preview.Valid);
        Assert.True(preview.Blocked);
        Assert.Equal(new CategoryImportSummary(0, 0, 1), preview.Summary);
        Assert.Equal(new CategoryImportBlocker("used", "Used", 1), Assert.Single(preview.Blockers));
        Assert.Equal(before, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Dry_run_rejects_cycles_and_duplicate_slugs()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[{"slug":"a","name":"A","parentSlug":"b"},{"slug":"a","name":"Duplicate","parentSlug":null},{"slug":"b","name":"B","parentSlug":"a"}]}""");

        var preview = await service.DryRun("category-writer", json, default);

        Assert.False(preview.Valid);
        Assert.Contains(preview.Errors, x => x.Contains("duplicated", StringComparison.Ordinal));
        Assert.Contains(preview.Errors, x => x.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sort_order_is_optional_and_only_changes_when_supplied()
    {
        await using var db = await CreateDatabase();
        db.Categories.Add(new Category { Slug = "existing", Name = "Existing", SortOrder = 7 });
        await db.SaveChangesAsync();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var omitted = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[{"slug":"existing","name":"Existing","parentSlug":null}]}""");
        var supplied = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[{"slug":"existing","name":"Existing","parentSlug":null,"sortOrder":9}]}""");

        var preserved = await service.DryRun("category-writer", omitted, default);
        var changed = await service.DryRun("category-writer", supplied, default);

        Assert.Equal(0, preserved.Summary.Update);
        Assert.Equal(1, changed.Summary.Update);
        Assert.Contains("sortOrder", System.Text.Json.JsonSerializer.Serialize(Assert.Single(changed.Changes).After));
    }

    [Fact]
    public async Task Sort_order_rejects_negative_and_non_integer_values()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        foreach (var value in new[] { "-1", "1.5", "null", "\"bad\"" })
        {
            var json = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"categories\":[{{\"slug\":\"new\",\"name\":\"New\",\"sortOrder\":{value}}}]}}");
            var preview = await service.DryRun("category-writer", json, default);
            Assert.False(preview.Valid);
            Assert.Contains(preview.Errors, x => x.Contains("sortOrder", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Dry_run_rejects_files_over_one_mebibyte()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var content = new byte[CategoryImportService.MaximumFileBytes + 1];

        await Assert.ThrowsAsync<CategoryImportFileTooLargeException>(() => service.DryRun("category-writer", content, default));
    }

    [Fact]
    public async Task Commit_replaces_atomically_and_audits_filename_counts_and_slugs()
    {
        await using var db = await CreateDatabase();
        var kept = new Category { Slug = "kept", Name = "Old name", IsActive = false, SortOrder = 8 };
        db.Categories.AddRange(kept, new Category { Slug = "removed", Name = "Removed" });
        await db.SaveChangesAsync();
        var keptId = kept.Id;
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[{"slug":"kept","name":"Kept","parentSlug":null},{"slug":"created","name":"Created","parentSlug":null,"sortOrder":3},{"slug":"created-child","name":"Created Child","parentSlug":"created"}]}""");

        var result = await service.Commit("category-writer", "my-categories.json", "GHI ĐÈ", json, default);

        Assert.Equal(new CategoryImportSummary(2, 1, 1), result.Summary);
        var keptAfter = await db.Categories.SingleAsync(x => x.Slug == "kept");
        Assert.Equal(keptId, keptAfter.Id);
        Assert.False(keptAfter.IsActive);
        Assert.Equal(8, keptAfter.SortOrder);
        var created = await db.Categories.SingleAsync(x => x.Slug == "created");
        Assert.Equal(3, created.SortOrder);
        Assert.Equal(created.Id, (await db.Categories.SingleAsync(x => x.Slug == "created-child")).ParentCategoryId);
        Assert.False(await db.Categories.AnyAsync(x => x.Slug == "removed"));
        var audit = await db.AdminAuditLogs.SingleAsync(x => x.Action == "replace_all");
        Assert.Equal("category-writer", audit.ActorSubject);
        var details = JsonSerializer.Deserialize<CategoryImportAudit>(audit.AfterJson!);
        Assert.Equal("my-categories.json", details!.OriginalFilename);
        Assert.Equal(new[] { "created", "created-child" }, details.CreatedSlugs);
        Assert.Equal(new[] { "kept" }, details.UpdatedSlugs);
        Assert.Equal(new[] { "removed" }, details.DeletedSlugs);
    }

    [Fact]
    public async Task Commit_rechecks_blockers_and_confirmation_before_mutation()
    {
        await using var db = await CreateDatabase();
        var used = new Category { Slug = "used", Name = "Used" };
        db.Categories.Add(used);
        db.Products.Add(new Product { Slug = "product", Name = "Product", Category = used });
        await db.SaveChangesAsync();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"categories":[]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => service.Commit("category-writer", "file.json", "ghi đè", json, default));
        var conflict = await Assert.ThrowsAsync<CategoryImportConflictException>(() => service.Commit("category-writer", "file.json", "GHI ĐÈ", json, default));
        Assert.Equal("used", Assert.Single(conflict.Blockers).Slug);
        Assert.True(await db.Categories.AnyAsync(x => x.Slug == "used"));
        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Commit_rejects_invalid_file_without_writing()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":2,"categories":[]}""");

        var exception = await Assert.ThrowsAsync<CategoryImportValidationException>(() =>
            service.Commit("category-writer", "file.json", "GHI ĐÈ", json, default));

        Assert.Contains(exception.Errors, error => error.Contains("schemaVersion", StringComparison.Ordinal));
        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Multipart_endpoint_returns_preview_and_validation_statuses()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var validContext = CreateUploadContext("""{"schemaVersion":1,"categories":[]}""");
        var valid = await AdminEndpoints.RunCategoryImport(validContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)valid).StatusCode);

        var invalidContext = CreateUploadContext("""{"schemaVersion":5,"categories":[]}""");
        var invalid = await AdminEndpoints.RunCategoryImport(invalidContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)invalid).StatusCode);
    }

    [Fact]
    public async Task Multipart_endpoint_enforces_size_limit_and_reports_commit_blockers()
    {
        await using var db = await CreateDatabase();
        var used = new Category { Slug = "used", Name = "Used" };
        db.Categories.Add(used);
        db.Products.Add(new Product { Slug = "product", Name = "Product", Category = used });
        await db.SaveChangesAsync();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var tooLargeContext = CreateUploadContext(new string(' ', CategoryImportService.MaximumFileBytes + 1));
        var tooLarge = await AdminEndpoints.RunCategoryImport(tooLargeContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ((IStatusCodeHttpResult)tooLarge).StatusCode);

        var conflictContext = CreateUploadContext("""{"schemaVersion":1,"categories":[]}""", new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["confirmation"] = "GHI ĐÈ" });
        var conflict = await AdminEndpoints.RunCategoryImport(conflictContext, service, default, commit: true);
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)conflict).StatusCode);
    }

    [Fact]
    public async Task Multipart_endpoint_rejects_missing_file_and_enforces_permissions_and_confirmation()
    {
        await using var db = await CreateDatabase();
        var service = new CategoryImportService(new EfAdminData(db, TimeProvider.System));
        var missingFileContext = CreateUploadContext("{}", includeFile: false);
        var missingFile = await AdminEndpoints.RunCategoryImport(missingFileContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)missingFile).StatusCode);

        var unauthorizedContext = CreateUploadContext("{}");
        unauthorizedContext.User = new ClaimsPrincipal();
        var unauthorized = await AdminEndpoints.RunCategoryImport(unauthorizedContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status401Unauthorized, ((IStatusCodeHttpResult)unauthorized).StatusCode);

        var forbiddenContext = CreateUploadContext("{}", subject: "category-reader");
        var forbidden = await AdminEndpoints.RunCategoryImport(forbiddenContext, service, default, commit: false);
        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)forbidden).StatusCode);

        var wrongConfirmationContext = CreateUploadContext("{}", new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["confirmation"] = "overwrite" });
        var wrongConfirmation = await AdminEndpoints.RunCategoryImport(wrongConfirmationContext, service, default, commit: true);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)wrongConfirmation).StatusCode);

        var invalidCommitContext = CreateUploadContext("""{"schemaVersion":7,"categories":[]}""", new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["confirmation"] = "GHI ĐÈ" });
        var invalidCommit = await AdminEndpoints.RunCategoryImport(invalidCommitContext, service, default, commit: true);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)invalidCommit).StatusCode);

        var successfulCommitContext = CreateUploadContext("""{"schemaVersion":1,"categories":[]}""", new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["confirmation"] = "GHI ĐÈ" });
        var successfulCommit = await AdminEndpoints.RunCategoryImport(successfulCommitContext, service, default, commit: true);
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)successfulCommit).StatusCode);
    }

    private static async Task<CompareShopDbContext> CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new CompareShopDbContext(options);
        var user = new User("category-reader", DateTime.UtcNow);
        var writer = new User("category-writer", DateTime.UtcNow);
        var readRole = new AdminRole { Name = "category-reader" };
        var writeRole = new AdminRole { Name = "category-writer" };
        var permissions = new[]
        {
            new AdminPermission { Key = AdminPermissions.CategoriesRead },
            new AdminPermission { Key = AdminPermissions.CategoriesCreate },
            new AdminPermission { Key = AdminPermissions.CategoriesUpdate },
            new AdminPermission { Key = AdminPermissions.CategoriesDelete }
        };
        db.Users.AddRange(user, writer);
        db.AdminRoles.AddRange(readRole, writeRole);
        db.AdminPermissions.AddRange(permissions);
        await db.SaveChangesAsync();
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = readRole.Id, PermissionId = permissions[0].Id });
        db.AdminRolePermissions.AddRange(permissions.Skip(1).Select(permission => new AdminRolePermission { RoleId = writeRole.Id, PermissionId = permission.Id }));
        db.AdminUserRoles.AddRange(new AdminUserRole { UserId = user.Id, RoleId = readRole.Id }, new AdminUserRole { UserId = writer.Id, RoleId = writeRole.Id });
        await db.SaveChangesAsync();
        return db;
    }

    private static DefaultHttpContext CreateUploadContext(string content, Dictionary<string, Microsoft.Extensions.Primitives.StringValues>? fields = null, bool includeFile = true, string subject = "category-writer")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"));
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var file = new FormFile(stream, 0, stream.Length, "file", "categories.json");
        context.Request.Form = new FormCollection(fields ?? new(), includeFile ? new FormFileCollection { file } : new FormFileCollection());
        return context;
    }
}
