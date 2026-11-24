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
    ///
    /// Cancellation takes precedence over every fault category across the
    /// whole chain, not just as the outermost type. The AWS SDK client layer
    /// reports an aborted request as an AmazonClientException and the HTTP
    /// transport layer as an IOException, each carrying the original
    /// OperationCanceledException as the inner exception. Recognizing those
    /// wrappers by their outer type first would re-map a caller cancellation
    /// to a content-unavailable fault and record the wrong terminal state and
    /// a spurious cost anomaly for a request the user cancelled. A genuine
    /// provider fault never carries an OperationCanceledException in its
    /// chain, so this precedence only re-routes cancellations.
    /// </summary>
    internal static FailureCategory Classify(Exception exception)
    {
        if (IsCancellation(exception))
            return FailureCategory.Cancelled;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
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
    /// True when an OperationCanceledException appears anywhere in the
    /// exception chain, including the SDK-style case where a client or
    /// transport exception wraps the cancellation as an inner exception.
    /// </summary>
    internal static bool IsCancellation(Exception exception) => FindCancellation(exception) is not null;

    /// <summary>
    /// Returns the first OperationCanceledException in the chain, or null.
    /// Callers re-throw this inner instance instead of the wrapper so the
    /// cancellation propagates without the provider wrapper text that
    /// incident #459 requires to stay out of logs and responses.
    /// </summary>
    internal static OperationCanceledException? FindCancellation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException cancellation)
                return cancellation;
        }

        return null;
    }

    /// <summary>
    /// Hard ceiling for a single attachment readback. Recorded object sizes are
    /// data, not authority: a row migrated or corrupted above the legacy
    /// upload-era validation still cannot drive an unbounded in-memory read.
    /// Sized above every configured limit ever shipped for chat attachments so
    /// legitimate legacy objects stay readable.
    /// </summary>
    internal const long AbsoluteReadbackCeilingBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Resolves the readback size bound for a stored object.
    ///
    /// Legacy-object policy: the recorded SizeBytes was validated against the
    /// limit in force at upload time (FileValidationService). Lowering the
    /// configured maximum afterwards must not silently orphan objects that
    /// were lawful when stored, so the bound honors the larger of the current
    /// configuration and the recorded size. The recorded size is metadata,
    /// however, not a trusted authority, so the pair is additionally clamped
    /// to <see cref="AbsoluteReadbackCeilingBytes"/>; an object reported above
    /// that ceiling fails closed through the bounded reader's
    /// InvalidDataException into the content-unavailable contract.
    /// </summary>
    internal static long ResolveReadbackBound(long configuredMaxBytes, long recordedSizeBytes) =>
        Math.Min(AbsoluteReadbackCeilingBytes, Math.Max(configuredMaxBytes, Math.Max(0L, recordedSizeBytes)));

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
