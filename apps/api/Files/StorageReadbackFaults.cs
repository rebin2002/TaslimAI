using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Amazon.Runtime;
using Amazon.S3;

namespace Taslim.Api.Files;

/// <summary>
/// Sanitizes faults raised while a stored object body is still streaming out of
/// an S3-compatible provider such as Cloudflare R2.
///
/// A storage adapter can only wrap the exception produced by the object-open
/// call. Once the response headers are accepted, the adapter hands a live
/// network stream to the consumer, so a connection reset, TLS failure,
/// throttling response or premature end-of-body surfaces at the read site as a
/// raw provider or transport exception that was never converted to
/// FileStorageOperationException. Provider exception messages can embed the
/// storage endpoint, the account identifier and the object key, all of which
/// incident #459 requires to stay out of logs and API responses, so consumers
/// must classify and re-map them instead of logging or surfacing them.
/// </summary>
internal static class StorageReadbackFaults
{
    internal enum FailureCategory
    {
        /// <summary>The caller cancelled; must keep propagating as cancellation.</summary>
        Cancelled,

        /// <summary>The provider returned an S3 error response while streaming.</summary>
        ProviderResponse,

        /// <summary>The SDK failed before or during the request without a service response.</summary>
        ProviderClient,

        /// <summary>Connection, TLS or HTTP transport failure while streaming.</summary>
        Transport,

        /// <summary>Adapter-reported configuration or operation failure.</summary>
        StorageOperation,

        /// <summary>The body was empty or longer than the declared bound.</summary>
        InvalidContent,

        /// <summary>Anything else, which must stay visible as an unexpected defect.</summary>
        Unexpected,
    }

    /// <summary>
    /// True when the exception means the stored object could not be read.
    /// Caller cancellation and unexpected internal errors return false so they
    /// keep their existing propagation and diagnostics.
    /// </summary>
    internal static bool IsReadbackFault(Exception exception) => Classify(exception) switch
    {
        FailureCategory.Cancelled => false,
        FailureCategory.Unexpected => false,
        _ => true,
    };

    /// <summary>
    /// Walks the exception chain and returns the first recognized storage read
    /// category. The whole chain is inspected rather than only the outer
    /// exception because the SDK can wrap a transport failure inside a client
    /// or service exception.
    /// </summary>
    internal static FailureCategory Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException:
                    return FailureCategory.Cancelled;
                case FileStorageUnavailableException or FileStorageOperationException:
                    return FailureCategory.StorageOperation;
                case AmazonS3Exception:
                case AmazonServiceException:
                    return FailureCategory.ProviderResponse;
                case AmazonClientException:
                    return FailureCategory.ProviderClient;
                case InvalidDataException:
                    return FailureCategory.InvalidContent;
                case HttpRequestException:
                case SocketException:
                case AuthenticationException:
                case ProtocolViolationException:
                case IOException:
                case ObjectDisposedException:
                    return FailureCategory.Transport;
            }
        }

        return FailureCategory.Unexpected;
    }

    /// <summary>
    /// Reads a stored object into memory under an explicit ceiling and fails
    /// closed when the body is empty or exceeds that ceiling. A short body is
    /// not rejected here because callers compare the result against the size
    /// recorded when the object was stored.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The readback size bound must be positive.");

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("Stored object content exceeded the readback size bound.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
            throw new InvalidDataException("Stored object content was empty.");

        return buffer.ToArray();
    }
}
