using CompareShopAPI.Application.Files;
using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace CompareShopAPI.Infrastructure.Files;

public sealed class MinioFileObjectStorage : IFileObjectStorage
{
    private readonly IMinioClient client;
    private readonly string bucket;
    private readonly Uri publicEndpoint;

    public MinioFileObjectStorage(IConfiguration configuration)
    {
        var endpointValue = configuration["Minio:Endpoint"] ?? throw new InvalidOperationException("Minio:Endpoint is required.");
        var endpoint = Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri)
            ? endpointUri
            : new Uri($"http://{endpointValue}", UriKind.Absolute);
        if (endpoint.Scheme is not ("http" or "https")) throw new InvalidOperationException("Minio:Endpoint must use HTTP or HTTPS.");
        var publicEndpointValue = configuration["Minio:PublicEndpoint"] ?? endpointValue;
        publicEndpoint = Uri.TryCreate(publicEndpointValue, UriKind.Absolute, out var publicEndpointUri)
            ? publicEndpointUri
            : new Uri($"http://{publicEndpointValue}", UriKind.Absolute);
        if (publicEndpoint.Scheme is not ("http" or "https")) throw new InvalidOperationException("Minio:PublicEndpoint must use HTTP or HTTPS.");
        var accessKey = configuration["Minio:AccessKey"] ?? throw new InvalidOperationException("Minio:AccessKey is required.");
        var secretKey = configuration["Minio:SecretKey"] ?? throw new InvalidOperationException("Minio:SecretKey is required.");
        bucket = configuration["Minio:Bucket"] ?? throw new InvalidOperationException("Minio:Bucket is required.");
        var secure = bool.TryParse(configuration["Minio:UseSsl"], out var configuredSsl)
            ? configuredSsl
            : endpoint.Scheme == Uri.UriSchemeHttps;
        client = new MinioClient().WithEndpoint(endpoint.Authority).WithSSL(secure).WithCredentials(accessKey, secretKey).Build();
    }

    public string PublicUrl(string key) => new UriBuilder(publicEndpoint.Scheme, publicEndpoint.Host, publicEndpoint.IsDefaultPort ? -1 : publicEndpoint.Port,
        $"{publicEndpoint.AbsolutePath.TrimEnd('/')}/{Uri.EscapeDataString(bucket)}/{Uri.EscapeDataString(key)}").Uri.AbsoluteUri;

    public async Task Put(string key, Stream content, long size, string contentType, CancellationToken ct)
    {
        var storedType = StoredContentType(contentType);
        var headers = BuildObjectHeaders(contentType);
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key).WithStreamData(content)
            .WithObjectSize(size).WithContentType(storedType).WithHeaders(headers), ct);
    }

    internal static Dictionary<string, string> BuildObjectHeaders(string contentType)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store"
        };
        if (contentType == "image/svg+xml")
            headers["Content-Disposition"] = "attachment; filename=\"download.svg\"";
        return headers;
    }

    internal static string StoredContentType(string contentType) =>
        contentType == "image/svg+xml" ? "application/octet-stream" : contentType;

    public Task Delete(string key, CancellationToken ct) => client.RemoveObjectAsync(
        new RemoveObjectArgs().WithBucket(bucket).WithObject(key), ct);
}
