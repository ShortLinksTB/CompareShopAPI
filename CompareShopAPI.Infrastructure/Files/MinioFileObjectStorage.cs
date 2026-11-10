using CompareShopAPI.Application.Files;
using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace CompareShopAPI.Infrastructure.Files;

public sealed class MinioFileObjectStorage : IFileObjectStorage
{
    private readonly IMinioClient client;
    private readonly string bucket;

    public MinioFileObjectStorage(IConfiguration configuration)
    {
        var endpointValue = configuration["Minio:Endpoint"] ?? throw new InvalidOperationException("Minio:Endpoint is required.");
        var endpoint = Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri)
            ? endpointUri
            : new Uri($"http://{endpointValue}", UriKind.Absolute);
        if (endpoint.Scheme is not ("http" or "https")) throw new InvalidOperationException("Minio:Endpoint must use HTTP or HTTPS.");
        var accessKey = configuration["Minio:AccessKey"] ?? throw new InvalidOperationException("Minio:AccessKey is required.");
        var secretKey = configuration["Minio:SecretKey"] ?? throw new InvalidOperationException("Minio:SecretKey is required.");
        bucket = configuration["Minio:Bucket"] ?? throw new InvalidOperationException("Minio:Bucket is required.");
        var secure = bool.TryParse(configuration["Minio:UseSsl"], out var configuredSsl)
            ? configuredSsl
            : endpoint.Scheme == Uri.UriSchemeHttps;
        client = new MinioClient().WithEndpoint(endpoint.Authority).WithSSL(secure).WithCredentials(accessKey, secretKey).Build();
    }

    public async Task Put(string key, Stream content, long size, string contentType, CancellationToken ct)
    {
        var storedType = contentType == "image/svg+xml" ? "application/octet-stream" : contentType;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store",
            ["Content-Type"] = storedType
        };
        if (contentType == "image/svg+xml")
            headers["Content-Disposition"] = "attachment; filename=\"download.svg\"";
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key).WithStreamData(content)
            .WithObjectSize(size).WithContentType(storedType).WithHeaders(headers), ct);
    }

    public async Task<StoredObject?> Get(string key, CancellationToken ct)
    {
        var output = new MemoryStream();
        try
        {
            await client.GetObjectAsync(new GetObjectArgs().WithBucket(bucket).WithObject(key)
                .WithCallbackStream((input, token) => input.CopyToAsync(output, token)), ct);
            output.Position = 0;
            return new(output, "application/octet-stream");
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            await output.DisposeAsync();
            return null;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public Task Delete(string key, CancellationToken ct) => client.RemoveObjectAsync(
        new RemoveObjectArgs().WithBucket(bucket).WithObject(key), ct);
}
