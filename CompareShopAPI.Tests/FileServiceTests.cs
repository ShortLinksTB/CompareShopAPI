using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class FileServiceTests
{
    [Fact]
    public void Svg_object_headers_force_attachment_and_keep_content_type_in_sdk_argument()
    {
        var headers = MinioFileObjectStorage.BuildObjectHeaders("image/svg+xml");

        Assert.Equal("no-store", headers["Cache-Control"]);
        Assert.Equal("attachment; filename=\"download.svg\"", headers["Content-Disposition"]);
        Assert.DoesNotContain("Content-Type", headers.Keys);
        Assert.Equal("application/octet-stream", MinioFileObjectStorage.StoredContentType("image/svg+xml"));
        Assert.Equal("image/png", MinioFileObjectStorage.StoredContentType("image/png"));
    }

    [Fact]
    public async Task Minio_put_sends_svg_type_and_response_headers_on_the_actual_request()
    {
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requestTask = listener.GetContextAsync();
        var storage = new MinioFileObjectStorage(MinioConfiguration($"http://127.0.0.1:{port}"));
        var putTask = storage.Put("svg-key", new MemoryStream([1, 2, 3]), 3, "image/svg+xml", default);
        var context = await requestTask.WaitAsync(TimeSpan.FromSeconds(10));
        var contentType = context.Request.ContentType;
        var disposition = context.Request.Headers["Content-Disposition"];
        var cacheControl = context.Request.Headers["Cache-Control"];
        await context.Request.InputStream.CopyToAsync(Stream.Null);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength64 = 0;
        context.Response.Close();
        await putTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("PUT", context.Request.HttpMethod);
        Assert.Equal("application/octet-stream", contentType);
        Assert.Equal("attachment; filename=\"download.svg\"", disposition);
        Assert.Equal("no-store", cacheControl);
    }

    [Fact]
    public async Task Svg_upload_is_accepted_and_creates_public_target()
    {
        var repository = new FakeRepository();
        var storage = new FakeStorage();
        var service = Create(repository, storage, new FakePermissions(FilePermissions.Write));

        var uploaded = await service.Upload("operator", new("diagram.svg", "image/svg+xml", 3,
            new MemoryStream([1, 2, 3])), default);

        Assert.Equal("diagram.svg", uploaded.OriginalFileName);
        Assert.Equal("image/svg+xml", uploaded.ContentType);
        Assert.Equal(1, storage.PutCount);
        Assert.Equal("image/svg+xml", storage.LastContentType);
        Assert.Equal($"https://files.example.test/compareshop/{uploaded.Id:N}",
            await service.PublicObjectUrl(uploaded.Id, default));
    }

    [Fact]
    public void Minio_storage_generates_stable_public_object_url_without_presigning()
    {
        var configuration = MinioConfiguration();

        var storage = new MinioFileObjectStorage(configuration);

        Assert.Equal("https://objects.example.test/storage/compareshop-files/abc123", storage.PublicUrl("abc123"));
    }

    [Fact]
    public async Task Upload_rejects_unsupported_type_and_files_above_10_mib_before_storage()
    {
        var storage = new FakeStorage();
        var service = Create(new FakeRepository(), storage, new FakePermissions(FilePermissions.Write));
        var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Upload("operator", new("file.exe", "application/octet-stream", 3, content), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Upload("operator", new("large.pdf", "application/pdf", FileUploadPolicy.MaxBytes + 1, content), default));
        Assert.Equal(0, storage.PutCount);
    }

    [Fact]
    public async Task Metadata_failure_cleans_up_uploaded_object()
    {
        var repository = new FakeRepository { FailAdd = true };
        var storage = new FakeStorage();
        var service = Create(repository, storage, new FakePermissions(FilePermissions.Write));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Upload("operator",
            new("photo.png", "image/png", 3, new MemoryStream([1, 2, 3])), default));
        Assert.Equal(1, storage.PutCount);
        Assert.Equal(1, storage.DeleteCount);
    }

    [Fact]
    public async Task Permissions_are_enforced_and_unknown_public_ids_are_missing()
    {
        var repository = new FakeRepository();
        var service = Create(repository, new FakeStorage(), new FakePermissions());
        await Assert.ThrowsAsync<AdminUnauthorizedException>(() => service.List(null, 1, 20, null, null, default));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.List("viewer", 1, 20, null, null, default));
        Assert.Null(await service.PublicObjectUrl(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Authorized_upload_list_detail_redirect_and_delete_work()
    {
        var repository = new FakeRepository();
        var storage = new FakeStorage();
        var service = Create(repository, storage, new FakePermissions(FilePermissions.Read, FilePermissions.Write));
        var uploaded = await service.Upload("operator", new("photo.png", "image/png", 3, new MemoryStream([1, 2, 3])), default);
        Assert.Equal("photo.png", uploaded.OriginalFileName);
        Assert.NotNull(await service.Get("operator", uploaded.Id, default));
        Assert.Single((await service.List("operator", 1, 20, " photo ", "IMAGES", default)).Items);
        Assert.Equal($"https://files.example.test/compareshop/{uploaded.Id:N}", await service.PublicObjectUrl(uploaded.Id, default));
        Assert.True(await service.Delete("operator", uploaded.Id, default));
        Assert.Equal(1, storage.DeleteCount);
        Assert.Null(await service.Get("operator", uploaded.Id, default));
    }

    [Fact]
    public void File_asset_enforces_id_filename_type_size_and_dimensions()
    {
        var now = DateTime.UtcNow;
        Assert.Throws<ArgumentException>(() => NewAsset("../escape.png", "image/png", 1, null, null));
        Assert.Throws<ArgumentException>(() => NewAsset("file.bin", "application/octet-stream", 1, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewAsset("large.png", "image/png", FileUploadPolicy.MaxBytes + 1, null, null));
        Assert.Throws<ArgumentException>(() => NewAsset("image.png", "image/png", 1, 640, null));
        Assert.Equal(now.Date, new FileAsset(Guid.NewGuid(), "ok.png", "image/png", 1, "key", "admin", now).UploadedAtUtc.Date);
    }

    [Fact]
    public async Task Admin_and_public_endpoints_apply_permissions_redirect_and_cache_policies()
    {
        var repository = new FakeRepository();
        var storage = new FakeStorage();
        var permissions = new FakePermissions(FilePermissions.Read, FilePermissions.Write);
        await using var app = new ApiFactory(signing: true, configureServices: services =>
        {
            services.RemoveAll<IFileRepository>(); services.AddSingleton<IFileRepository>(repository);
            services.RemoveAll<IFileObjectStorage>(); services.AddSingleton<IFileObjectStorage>(storage);
            services.RemoveAll<IFilePermissionGate>(); services.AddSingleton<IFilePermissionGate>(permissions);
        });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{Guid.NewGuid():D}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: "operator"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/files?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/files", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/files/00000000-0000-0000-0000-000000000000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/admin/files/00000000-0000-0000-0000-000000000000")).StatusCode);
        storage.FailNextPut = true;
        using var form = new MultipartFormDataContent();
        var storageFailureBytes = new ByteArrayContent([1]);
        storageFailureBytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(storageFailureBytes, "files", "storage-error.png");
        var bytes = new ByteArrayContent([1, 2, 3]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        form.Add(bytes, "files", "test.svg");
        var invalidBytes = new ByteArrayContent([1, 2, 3]);
        invalidBytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(invalidBytes, "files", "bad.exe");
        using var uploadResponse = await client.PostAsync("/admin/files", form);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var outcomes = await uploadResponse.Content.ReadFromJsonAsync<FileUploadOutcome[]>();
        Assert.NotNull(outcomes);
        Assert.Equal(3, outcomes.Length);
        Assert.False(outcomes[0].Succeeded);
        Assert.Equal(StatusCodes.Status500InternalServerError, outcomes[0].StatusCode);
        Assert.True(outcomes[1].Succeeded);
        Assert.Equal(StatusCodes.Status201Created, outcomes[1].StatusCode);
        Assert.False(outcomes[2].Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, outcomes[2].StatusCode);
        var metadata = outcomes[1].File!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/files?page=1&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/files/{metadata.Id:D}")).StatusCode);
        using var redirect = await client.GetAsync($"/files/{metadata.Id:D}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", redirect.Headers.CacheControl?.ToString());
        Assert.Equal($"https://files.example.test/compareshop/{metadata.Id:N}", redirect.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/files/{metadata.Id:D}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{metadata.Id:D}")).StatusCode);
        permissions.Allowed.Remove(FilePermissions.Write);
        using var deniedForm = new MultipartFormDataContent();
        var deniedContent = new ByteArrayContent([1]);
        deniedContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        deniedForm.Add(deniedContent, "files", "denied.png");
        var putCountBeforeDeniedBatch = storage.PutCount;
        var deniedResponse = await client.PostAsync("/admin/files", deniedForm);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.Equal(putCountBeforeDeniedBatch, storage.PutCount);
    }

    private static FileAsset NewAsset(string name, string type, long size, int? width, int? height) =>
        new(Guid.NewGuid(), name, type, size, "key", "admin", DateTime.UtcNow, width, height);

    private static IConfiguration MinioConfiguration(string? endpoint = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Minio:Endpoint"] = endpoint ?? "http://minio.internal:9000",
        ["Minio:PublicEndpoint"] = "https://objects.example.test/storage",
        ["Minio:AccessKey"] = "test-access",
        ["Minio:SecretKey"] = "test-secret",
        ["Minio:Bucket"] = "compareshop-files"
    }).Build();

    private static FileService Create(FakeRepository repository, FakeStorage storage, FakePermissions permissions) =>
        new(repository, storage, permissions, TimeProvider.System, NullLogger<FileService>.Instance);

    private sealed class FakeRepository : IFileRepository
    {
        public bool FailAdd { get; init; }
        private readonly Dictionary<Guid, FileAsset> assets = [];
        public Task<FilePage> List(int page, int pageSize, string? query, string? type, CancellationToken ct)
        {
            var list = assets.Values.Where(asset => query is null || asset.OriginalFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(asset => type is null || (type == "images") == asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult(new FilePage(list.Select(asset => new FileMetadata(asset.Id, asset.OriginalFileName, asset.ContentType,
                asset.SizeBytes, asset.Width, asset.Height, asset.UploadedBy, asset.UploadedAtUtc)).ToArray(), page, pageSize, list.Length, list.Length == 0 ? 0 : 1));
        }
        public Task<FileAsset?> Get(Guid id, CancellationToken ct) => Task.FromResult(assets.GetValueOrDefault(id));
        public Task Add(FileAsset file, CancellationToken ct)
        {
            if (FailAdd) return Task.FromException(new InvalidOperationException("metadata failure"));
            assets[file.Id] = file;
            return Task.CompletedTask;
        }
        public Task<bool> Delete(Guid id, CancellationToken ct) => Task.FromResult(assets.Remove(id));
    }

    private sealed class FakeStorage : IFileObjectStorage
    {
        public int PutCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool FailNextPut { get; set; }
        public string? LastContentType { get; private set; }
        public string PublicUrl(string key) => $"https://files.example.test/compareshop/{key}";
        public Task Put(string key, Stream content, long size, string contentType, CancellationToken ct)
        {
            PutCount++;
            LastContentType = contentType;
            if (FailNextPut) { FailNextPut = false; return Task.FromException(new InvalidOperationException("storage failure")); }
            return Task.CompletedTask;
        }
        public Task Delete(string key, CancellationToken ct) { DeleteCount++; return Task.CompletedTask; }
    }

    private sealed class FakePermissions(params string[] grants) : IFilePermissionGate
    {
        public HashSet<string> Allowed { get; } = grants.ToHashSet(StringComparer.Ordinal);
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(Allowed.Contains(permission));
    }
}
