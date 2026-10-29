using System.Diagnostics;
using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Workers.Ingestion;

// Only compile-time reason codes and method frames may enter scrape diagnostics.
internal enum CatalogReadFailure { RobotsPolicyDenied, ProductCountInvalid, ProductSchemaInvalid, ProductUrlDenied, EmptyCatalog }

internal enum CatalogReadStage { Starting, Robots, BrowserLaunch, Navigation, AccessCheck, ProductCount, ProductFields }

internal sealed class CatalogReadContext(string source, int? jobId)
{
    internal string Source { get; } = source;
    internal int? JobId { get; } = jobId;
    internal CatalogReadStage Stage { get; set; }
    internal string? Operation { get; set; }
    internal string? WaitCondition { get; set; }
    internal int? WaitTimeoutMs { get; set; }
}

internal static class CatalogReadDiagnostics
{
    private static readonly object ReasonKey = new();

    internal static InvalidDataException InvalidData(CatalogReadFailure reason)
    {
        var exception = new InvalidDataException("Catalog source validation failed.");
        exception.Data[ReasonKey] = reason;
        return exception;
    }

    internal static async Task<T> ReadAsync<T>(ILogger logger, Func<Task<T>> read, CatalogReadContext? context = null)
    {
        try { return await read(); }
        catch (SourceAccessBlockedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var reason = exception.Data[ReasonKey] is CatalogReadFailure code && Enum.IsDefined(code)
                ? code.ToString() : "Unclassified";
            // Never pass the exception object: providers would serialize its message/inner exceptions.
            logger.LogWarning("Catalog scrape failed. Reason={Reason}; ExceptionType={ExceptionType}; Stack={DiagnosticStack}; Source={Source}; JobId={JobId}; Stage={Stage}; Operation={Operation}; WaitCondition={WaitCondition}; WaitTimeoutMs={WaitTimeoutMs}",
                reason, exception.GetType().FullName, FormatStack(exception), context?.Source ?? "Unknown", context?.JobId, context?.Stage ?? CatalogReadStage.Starting,
                context?.Operation, context?.WaitCondition, context?.WaitTimeoutMs);
            throw;
        }
    }

    private static string FormatStack(Exception exception)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames().Take(8)
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}");
        var stack = string.Join(" | ", frames);
        return stack.Length <= 1200 ? stack : stack[..1200];
    }
}
