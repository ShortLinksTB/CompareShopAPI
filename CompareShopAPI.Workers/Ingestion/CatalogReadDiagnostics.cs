using System.Diagnostics;
using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Workers.Ingestion;

// Only compile-time reason codes and method frames may enter scrape diagnostics.
internal enum CatalogReadFailure { RobotsPolicyDenied, ProductCountInvalid, ProductSchemaInvalid, ProductUrlDenied, EmptyCatalog }

internal static class CatalogReadDiagnostics
{
    private static readonly object ReasonKey = new();

    internal static InvalidDataException InvalidData(CatalogReadFailure reason)
    {
        var exception = new InvalidDataException("Catalog source validation failed.");
        exception.Data[ReasonKey] = reason;
        return exception;
    }

    internal static async Task<T> ReadAsync<T>(ILogger logger, Func<Task<T>> read)
    {
        try { return await read(); }
        catch (SourceAccessBlockedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var reason = exception.Data[ReasonKey] is CatalogReadFailure code && Enum.IsDefined(code)
                ? code.ToString() : "Unclassified";
            // Never pass the exception object: providers would serialize its message/inner exceptions.
            logger.LogWarning("Catalog scrape failed. Reason={Reason}; ExceptionType={ExceptionType}; Stack={DiagnosticStack}",
                reason, exception.GetType().FullName, FormatStack(exception));
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
