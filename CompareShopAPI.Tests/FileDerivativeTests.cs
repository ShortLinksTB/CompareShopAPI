using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Files;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class FileDerivativeTests
{
    [Fact]
    public void Processor_outputs_requested_real_dimensions_and_format_without_stretching_contain()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var source = MakeImage(80, 40);
        var result = processor.Process(source, "png", 60, 60, "contain", 0.5, 0.5, "#ffffff", 90);

        Assert.Equal((60, 60), (result.Width, result.Height));
        Assert.Equal("image/png", result.ContentType);
        using var decoded = SKBitmap.Decode(result.Bytes);
        Assert.Equal((60, 60), (decoded.Width, decoded.Height));
        Assert.Equal(SKColors.White, decoded.GetPixel(0, 0));
    }

    [Fact]
    public void Processor_rejects_non_image_data_and_preserves_aspect_in_cover_crop()
    {
        var processor = new SkiaImageDerivativeProcessor();
        Assert.Equal("decode_failed", Assert.Throws<DerivativeFailure>(() =>
            processor.Process(new byte[] { 1, 2, 3 }, "keep", 32, 32, "cover", 0.5, 0.5, "transparent", 80)).Code);

        var result = processor.Process(MakeImage(80, 40), "jpg", 32, 32, "cover", 1, 0.5, "transparent", 80);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("jpg", result.Extension);
        Assert.Equal((32, 32), (result.Width, result.Height));
    }

    [Fact]
    public async Task Derivative_source_type_uses_encoded_bytes_instead_of_persisted_mime()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var imageBytes = MakeImage(20, 20);
        var request = new DerivativeRequest(new[]
        {
            new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null)
        });

        var sourceWithMisleadingMime = new FileAsset(Guid.NewGuid(), "source.pdf", "application/pdf", imageBytes.Length,
            "source", "admin", DateTime.UtcNow);
        var supportedService = new FileService(new Repository(sourceWithMisleadingMime), new Storage(imageBytes),
            new Permissions(), TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var supported = Assert.Single(await supportedService.CreateDerivatives("admin", sourceWithMisleadingMime.Id, request, default));
        Assert.True(supported.Succeeded);

        var unsupportedSource = new FileAsset(Guid.NewGuid(), "source.png", "image/png", 4, "source", "admin", DateTime.UtcNow);
        var unsupportedStorage = new Storage([.. "%PDF"u8.ToArray()]);
        var unsupportedService = new FileService(new Repository(unsupportedSource), unsupportedStorage,
            new Permissions(), TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var unsupported = Assert.Single(await unsupportedService.CreateDerivatives("admin", unsupportedSource.Id, request, default));
        Assert.Equal("unsupported_source_type", unsupported.Error);
        Assert.Equal(0, unsupportedStorage.PutCount);

        var svgBytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        var svgSource = new FileAsset(Guid.NewGuid(), "source.png", "image/png", svgBytes.Length, "svg", "admin", DateTime.UtcNow);
        var svgService = new FileService(new Repository(svgSource), new Storage(svgBytes), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var svg = Assert.Single(await svgService.CreateDerivatives("admin", svgSource.Id, request, default));
        Assert.Equal("unsupported_source_type", svg.Error);
    }

    [Fact]
    public void Processor_accepts_24_megapixels_and_rejects_more_from_header()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var atLimit = MakeImage(6000, 4000);
        var result = processor.Process(atLimit, "png", 16, 16, "contain", 0.5, 0.5, "transparent", 70);
        Assert.Equal((16, 16), (result.Width, result.Height));

        var aboveLimit = MakeImage(6001, 4000);
        var failure = Assert.Throws<DerivativeFailure>(() => processor.Process(aboveLimit, "png", 16, 16,
            "contain", 0.5, 0.5, "transparent", 70));
        Assert.Equal("source_pixel_limit_exceeded", failure.Code);
    }

    [Fact]
    public void Exif_orientations_transform_pixels_and_swap_dimensions_correctly()
    {
        var origins = new[]
        {
            SKEncodedOrigin.TopLeft, SKEncodedOrigin.TopRight, SKEncodedOrigin.BottomRight, SKEncodedOrigin.BottomLeft,
            SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop, SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom
        };
        var expected = new SKColor[][]
        {
            new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow, SKColors.Cyan, SKColors.Magenta },
            new[] { SKColors.Green, SKColors.Red, SKColors.Yellow, SKColors.Blue, SKColors.Magenta, SKColors.Cyan },
            new[] { SKColors.Magenta, SKColors.Cyan, SKColors.Yellow, SKColors.Blue, SKColors.Green, SKColors.Red },
            new[] { SKColors.Cyan, SKColors.Magenta, SKColors.Blue, SKColors.Yellow, SKColors.Red, SKColors.Green },
            new[] { SKColors.Red, SKColors.Blue, SKColors.Cyan, SKColors.Green, SKColors.Yellow, SKColors.Magenta },
            new[] { SKColors.Cyan, SKColors.Blue, SKColors.Red, SKColors.Magenta, SKColors.Yellow, SKColors.Green },
            new[] { SKColors.Magenta, SKColors.Yellow, SKColors.Green, SKColors.Cyan, SKColors.Blue, SKColors.Red },
            new[] { SKColors.Green, SKColors.Yellow, SKColors.Magenta, SKColors.Red, SKColors.Blue, SKColors.Cyan }
        };
        using var source = new SKBitmap(2, 3);
        source.SetPixel(0, 0, SKColors.Red); source.SetPixel(1, 0, SKColors.Green);
        source.SetPixel(0, 1, SKColors.Blue); source.SetPixel(1, 1, SKColors.Yellow);
        source.SetPixel(0, 2, SKColors.Cyan); source.SetPixel(1, 2, SKColors.Magenta);
        for (var i = 0; i < origins.Length; i++)
        {
            using var transformed = SkiaImageDerivativeProcessor.Orient(source, origins[i]);
            Assert.Equal(i >= 4 ? (3, 2) : (2, 3), (transformed.Width, transformed.Height));
            for (var y = 0; y < transformed.Height; y++)
                for (var x = 0; x < transformed.Width; x++)
                    Assert.Equal(expected[i][y * transformed.Width + x], transformed.GetPixel(x, y));
        }
    }

    [Fact]
    public async Task Each_variant_fails_independently_and_success_carries_source_metadata()
    {
        var source = new FileAsset(Guid.NewGuid(), "original.png", "image/png", 3, "source", "admin", DateTime.UtcNow, 80, 40);
        var repository = new Repository(source);
        var storage = new Storage(MakeImage(80, 40));
        var service = new FileService(repository, storage, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());

        var outcomes = await service.CreateDerivatives("admin", source.Id, new(new[]
        {
            new DerivativeVariant(600, 600, "cover", null, null, "transparent", "png", 80, null),
            new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null)
        }), default);

        Assert.Equal("decode_failed", outcomes[0].Error);
        var derivative = Assert.IsType<FileMetadata>(outcomes[1].File);
        Assert.Equal(source.Id, derivative.DerivedFrom!.Id);
        Assert.Equal(source.OriginalFileName, derivative.DerivedFrom.OriginalFileName);
        Assert.Equal("custom", derivative.DerivedPreset);
        Assert.Equal(32, derivative.Width);
        Assert.Equal(1, repository.AddCount);
        Assert.Equal(1, storage.PutCount);
    }

    [Fact]
    public async Task Variant_boundaries_and_contain_focal_fields_return_stable_errors()
    {
        var source = new FileAsset(Guid.NewGuid(), "original.png", "image/png", 3, "source", "admin", DateTime.UtcNow);
        var service = new FileService(new Repository(source), new Storage(MakeImage(10, 10)), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, new Processor());
        var outcomes = await service.CreateDerivatives("admin", source.Id, new(new[]
        {
            new DerivativeVariant(15, 32, "cover", null, null, "transparent", "png", 80, null),
            new DerivativeVariant(32, 32, "contain", 0.5, null, "transparent", "png", 80, null),
            new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 75, null),
            new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, "../bad")
        }), default);
        Assert.Equal("invalid_dimensions", outcomes[0].Error);
        Assert.Equal("invalid_variant", outcomes[1].Error);
        Assert.Equal("invalid_variant", outcomes[2].Error);
        Assert.Equal("invalid_variant", outcomes[3].Error);
    }

    [Fact]
    public async Task Storage_read_and_write_failures_return_stable_variant_errors()
    {
        var source = new FileAsset(Guid.NewGuid(), "original.png", "image/png", 3, "source", "admin", DateTime.UtcNow);
        var request = new DerivativeRequest(new[] { new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null) });
        var readFailure = new Storage(MakeImage(10, 10)) { FailRead = true };
        var readService = new FileService(new Repository(source), readFailure, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("storage_failed", Assert.Single(await readService.CreateDerivatives("admin", source.Id, request, default)).Error);

        var oversizedRead = new Storage(MakeImage(10, 10)) { ReportedLength = FileUploadPolicy.MaxBytes + 1 };
        var sizeService = new FileService(new Repository(source), oversizedRead, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("source_pixel_limit_exceeded", Assert.Single(await sizeService.CreateDerivatives("admin", source.Id, request, default)).Error);

        var writeFailure = new Storage(MakeImage(10, 10)) { FailPut = true };
        var writeService = new FileService(new Repository(source), writeFailure, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("storage_failed", Assert.Single(await writeService.CreateDerivatives("admin", source.Id, request, default)).Error);

        var processorService = new FileService(new Repository(source), new Storage(MakeImage(10, 10)), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, new Processor());
        var decoderError = new DerivativeRequest(new[] { new DerivativeVariant(31, 32, "cover", null, null, "transparent", "png", 80, null) });
        Assert.Equal("decode_failed", Assert.Single(await processorService.CreateDerivatives("admin", source.Id, decoderError, default)).Error);
        var tooLarge = new DerivativeRequest(new[] { new DerivativeVariant(30, 32, "cover", null, null, "transparent", "png", 80, null) });
        Assert.Equal("output_too_large", Assert.Single(await processorService.CreateDerivatives("admin", source.Id, tooLarge, default)).Error);

        var actualOversize = new Storage(new byte[FileUploadPolicy.MaxBytes + 1]) { ReportedLength = 3 };
        var actualSizeService = new FileService(new Repository(source), actualOversize, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("source_pixel_limit_exceeded", Assert.Single(await actualSizeService.CreateDerivatives("admin", source.Id, request, default)).Error);
    }

    [Fact]
    public async Task Derivative_source_type_uses_encoded_bytes_instead_of_stored_content_type()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var imageBytes = MakeImage(20, 20);
        var request = new DerivativeRequest(new[]
        {
            new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null)
        });
        var sourceWithMisleadingMime = new FileAsset(Guid.NewGuid(), "source.pdf", "application/pdf", imageBytes.Length,
            "source", "admin", DateTime.UtcNow);
        var supportedService = new FileService(new Repository(sourceWithMisleadingMime), new Storage(imageBytes),
            new Permissions(), TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var supported = Assert.Single(await supportedService.CreateDerivatives("admin", sourceWithMisleadingMime.Id, request, default));
        Assert.True(supported.Succeeded);

        var pdfBytes = "%PDF-1.7\n"u8.ToArray();
        var unsupportedSource = new FileAsset(Guid.NewGuid(), "source.png", "image/png", pdfBytes.Length, "source", "admin", DateTime.UtcNow);
        var unsupportedService = new FileService(new Repository(unsupportedSource), new Storage(pdfBytes),
            new Permissions(), TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var unsupported = Assert.Single(await unsupportedService.CreateDerivatives("admin", unsupportedSource.Id, request, default));
        Assert.Equal("unsupported_source_type", unsupported.Error);

        var svgBytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        var svgSource = new FileAsset(Guid.NewGuid(), "source.png", "image/png", svgBytes.Length, "svg", "admin", DateTime.UtcNow);
        var svgService = new FileService(new Repository(svgSource), new Storage(svgBytes), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, processor);
        var svg = Assert.Single(await svgService.CreateDerivatives("admin", svgSource.Id, request, default));
        Assert.Equal("unsupported_source_type", svg.Error);

        await Assert.ThrowsAsync<CompareShopAPI.Application.Admin.AdminUnauthorizedException>(() =>
            supportedService.CreateDerivatives(null, sourceWithMisleadingMime.Id, request, default));
        await Assert.ThrowsAsync<ArgumentException>(() => supportedService.CreateDerivatives("admin", sourceWithMisleadingMime.Id,
            new(Enumerable.Repeat(request.Variants[0], 6).ToArray()), default));
    }

    [Fact]
    public void Processor_rejects_svg_with_comment_and_doctype_before_root_without_xml_declaration()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var svgBytes = System.Text.Encoding.UTF8.GetBytes(
            "<!-- exported SVG -->\n<!DOCTYPE svg [<!ENTITY label 'image'>]>\n<svg xmlns=\"http://www.w3.org/2000/svg\"><title>&label;</title></svg>");

        var failure = Assert.Throws<DerivativeFailure>(() => processor.Process(svgBytes, "png", 32, 32,
            "cover", 0.5, 0.5, "transparent", 80));

        Assert.Equal("unsupported_source_type", failure.Code);
    }

    [Fact]
    public void Processor_does_not_treat_pdf_marker_in_png_trailing_metadata_as_pdf_header()
    {
        var processor = new SkiaImageDerivativeProcessor();
        var png = MakeImage(20, 20);
        var sourceWithMarkerInMetadata = png.Concat("\nmetadata:%PDF-not-a-header"u8.ToArray()).ToArray();

        var result = processor.Process(sourceWithMarkerInMetadata, "png", 32, 32,
            "cover", 0.5, 0.5, "transparent", 80);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal((32, 32), (result.Width, result.Height));
    }

    [Fact]
    public async Task Name_collision_retries_with_suffix_and_metadata_failure_cleans_orphan()
    {
        var source = new FileAsset(Guid.NewGuid(), "original.png", "image/png", 3, "source", "admin", DateTime.UtcNow);
        var repository = new Repository(source);
        await repository.Add(new FileAsset(Guid.NewGuid(), "original-32x32.png", "image/png", 3, "occupied", "admin", DateTime.UtcNow), default);
        var storage = new Storage(MakeImage(10, 10));
        var service = new FileService(repository, storage, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        var request = new DerivativeRequest(new[] { new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null) });

        var outcome = Assert.Single(await service.CreateDerivatives("admin", source.Id, request, default));
        Assert.True(outcome.Succeeded);
        Assert.Equal("original-32x32-2.png", outcome.File!.OriginalFileName);
        Assert.Equal("/files/" + outcome.File.Id.ToString("D"), outcome.PublicUrl);

        var failingRepository = new Repository(source) { FailReservation = true };
        var failingStorage = new Storage(MakeImage(10, 10));
        var failingService = new FileService(failingRepository, failingStorage, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        var failed = Assert.Single(await failingService.CreateDerivatives("admin", source.Id, request, default));
        Assert.Equal("metadata_persist_failed", failed.Error);
        Assert.Equal(1, failingStorage.DeleteCount);
    }

    [Fact]
    public async Task Suffix_exhaustion_and_audit_or_cleanup_failures_do_not_break_variant_results()
    {
        var source = new FileAsset(Guid.NewGuid(), "original.png", "image/png", 3, "source", "admin", DateTime.UtcNow);
        var request = new DerivativeRequest(new[] { new DerivativeVariant(32, 32, "cover", null, null, "transparent", "png", 80, null) });
        var repository = new Repository(source) { AlwaysCollide = true };
        var storage = new Storage(MakeImage(10, 10));
        var service = new FileService(repository, storage, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("name_suffix_exhausted", Assert.Single(await service.CreateDerivatives("admin", source.Id, request, default)).Error);
        Assert.Equal(1000, storage.DeleteCount);

        var auditRepository = new Repository(source) { FailAudit = true };
        var auditService = new FileService(auditRepository, new Storage(MakeImage(10, 10)), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, new Processor());
        var invalid = new DerivativeRequest(new[] { new DerivativeVariant(15, 32, "cover", null, null, "transparent", "png", 80, null) });
        Assert.Equal("invalid_dimensions", Assert.Single(await auditService.CreateDerivatives("admin", source.Id, invalid, default)).Error);

        var failingRepository = new Repository(source) { FailReservation = true };
        var cleanupFailureStorage = new Storage(MakeImage(10, 10)) { FailDelete = true };
        var cleanupService = new FileService(failingRepository, cleanupFailureStorage, new Permissions(), TimeProvider.System,
            NullLogger<FileService>.Instance, new Processor());
        Assert.Equal("metadata_persist_failed", Assert.Single(await cleanupService.CreateDerivatives("admin", source.Id, request, default)).Error);
    }

    [Fact]
    public async Task Source_derived_name_truncates_at_a_grapheme_boundary()
    {
        var sourceName = new string('a', 180) + "👩‍👩‍👧‍👦" + new string('b', 50) + ".png";
        var source = new FileAsset(Guid.NewGuid(), sourceName, "image/png", 3, "source", "admin", DateTime.UtcNow);
        var service = new FileService(new Repository(source), new Storage(MakeImage(10, 10)), new Permissions(),
            TimeProvider.System, NullLogger<FileService>.Instance, new Processor());
        var request = new DerivativeRequest(new[] { new DerivativeVariant(4000, 4000, "cover", null, null, "transparent", "png", 80, null) });
        var outcome = Assert.Single(await service.CreateDerivatives("admin", source.Id, request, default));
        Assert.True(outcome.Succeeded);
        var basename = Path.GetFileNameWithoutExtension(outcome.File!.OriginalFileName);
        Assert.True(basename.Length <= 200);
        Assert.False(char.IsHighSurrogate(basename[^1]));
    }

    private static byte[] MakeImage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class Processor : IImageDerivativeProcessor
    {
        private readonly SkiaImageDerivativeProcessor inner = new();
        public string? DetectSourceContentType(ReadOnlyMemory<byte> source) => inner.DetectSourceContentType(source);
        public ProcessedDerivative Process(ReadOnlyMemory<byte> source, string format, int width, int height, string fit,
            double focalX, double focalY, string background, int quality)
        {
            if (width == 600) throw new DerivativeFailure("decode_failed");
            if (width == 31) throw new InvalidOperationException("decode exception");
            if (width == 30) return new(new byte[FileUploadPolicy.MaxBytes + 1], width, height, "image/png", "png", []);
            if (width == 4000) return new([1, 2, 3], width, height, "image/png", "png", []);
            return inner.Process(source, format, width, height, fit, focalX, focalY, background, quality);
        }
    }

    private sealed class Permissions : IFilePermissionGate
    {
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(permission == FilePermissions.Write);
    }
    private sealed class Repository(FileAsset source) : IFileRepository
    {
        public int AddCount { get; private set; }
        public bool FailReservation { get; init; }
        public bool FailAudit { get; init; }
        public bool AlwaysCollide { get; init; }
        private readonly Dictionary<Guid, FileAsset> files = [];
        public Task<FilePage> List(int page, int size, string? query, string? type, CancellationToken ct) => Task.FromResult(new FilePage([], page, size, 0, 0));
        public Task<FileAsset?> Get(Guid id, CancellationToken ct) => Task.FromResult<FileAsset?>(id == source.Id ? source : files.GetValueOrDefault(id));
        public Task Add(FileAsset file, CancellationToken ct) { AddCount++; files[file.Id] = file; return Task.CompletedTask; }
        public Task ReserveDerivativeAndAdd(FileAsset file, string candidateName, CancellationToken ct)
        {
            if (FailReservation) return Task.FromException(new InvalidOperationException("metadata failure"));
            if (AlwaysCollide || files.Values.Append(source).Any(x => string.Equals(x.OriginalFileName.Normalize(), candidateName.Normalize(), StringComparison.OrdinalIgnoreCase)))
                return Task.FromException(new DerivativeNameCollisionException());
            AddCount++; files[file.Id] = file; return Task.CompletedTask;
        }
        public Task AuditDerivative(string actor, Guid sourceId, int variantIndex, string parameters, string? resultFileId, string? errorCode, CancellationToken ct) =>
            FailAudit ? Task.FromException(new InvalidOperationException("audit unavailable")) : Task.CompletedTask;
        public Task<bool> Delete(Guid id, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class Storage(byte[] source) : IFileObjectStorage
    {
        public int PutCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool FailPut { get; init; }
        public bool FailRead { get; init; }
        public bool FailDelete { get; init; }
        public long? ReportedLength { get; init; }
        public int ReadCount { get; private set; }
        public string PublicUrl(string key) => "https://files.example.test/" + key;
        public Task Put(string key, Stream content, long size, string contentType, CancellationToken ct)
        { PutCount++; return FailPut ? Task.FromException(new InvalidOperationException("storage unavailable")) : Task.CompletedTask; }
        public Task Delete(string key, CancellationToken ct)
        { DeleteCount++; return FailDelete ? Task.FromException(new InvalidOperationException("delete unavailable")) : Task.CompletedTask; }
        public Task<StoredFileContent> OpenRead(string key, CancellationToken ct)
        {
            ReadCount++;
            if (FailRead) return Task.FromException<StoredFileContent>(new InvalidOperationException("storage unavailable"));
            return Task.FromResult(new StoredFileContent(new MemoryStream(source), ReportedLength ?? source.Length));
        }
    }
}
