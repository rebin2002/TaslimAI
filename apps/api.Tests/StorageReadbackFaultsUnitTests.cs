using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Unit coverage for the readback classifier and bound resolver. The AWS SDK
/// reports an aborted request through layer-specific wrappers (client layer:
/// AmazonClientException, transport layer: IOException) with the original
/// OperationCanceledException as the inner exception, so classification must
/// honor cancellation across the whole chain rather than by the first
/// recognized outer type, while genuine provider and transport faults keep
/// their existing categories. Synthetic exception text only.
/// </summary>
public sealed class StorageReadbackFaultsUnitTests
{
    [Fact]
    public void Raw_cancellation_is_cancelled()
    {
        Assert.Equal(StorageReadbackFaults.FailureCategory.Cancelled,
            StorageReadbackFaults.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void Cancellation_wrapped_by_the_client_layer_is_cancelled_not_a_provider_fault()
    {
        var wrapped = new AmazonClientException(
            "Synthetic: The request was aborted", new OperationCanceledException());
        Assert.Equal(StorageReadbackFaults.FailureCategory.Cancelled, StorageReadbackFaults.Classify(wrapped));
        Assert.False(StorageReadbackFaults.IsReadbackFault(wrapped));
    }

    [Fact]
    public void Cancellation_wrapped_by_the_transport_layer_is_cancelled_not_a_transport_fault()
    {
        var wrapped = new IOException(
            "Synthetic: Unable to read data from the transport connection", new OperationCanceledException());
        Assert.Equal(StorageReadbackFaults.FailureCategory.Cancelled, StorageReadbackFaults.Classify(wrapped));
        Assert.False(StorageReadbackFaults.IsReadbackFault(wrapped));
    }

    [Fact]
    public void Cancellation_nested_two_layers_deep_is_still_cancelled()
    {
        // The adapter re-throws inside its own exception types only when a
        // message is carried; the SDK chain shape here is the realistic one:
        // transport wrapper around client wrapper around the cancellation.
        var wrapped = new IOException("Synthetic transport wrapper",
            new AmazonClientException("Synthetic client layer", new OperationCanceledException()));
        Assert.True(StorageReadbackFaults.IsCancellation(wrapped));
        Assert.Equal(StorageReadbackFaults.FailureCategory.Cancelled, StorageReadbackFaults.Classify(wrapped));
    }

    [Fact]
    public void Genuine_client_fault_without_cancellation_keeps_its_category()
    {
        var fault = new AmazonClientException("Synthetic: Failed to resolve endpoint");
        Assert.Equal(StorageReadbackFaults.FailureCategory.ProviderClient, StorageReadbackFaults.Classify(fault));
        Assert.True(StorageReadbackFaults.IsReadbackFault(fault));
    }

    [Fact]
    public void Genuine_transport_fault_nested_in_an_unrelated_wrapper_keeps_transport_category()
    {
        var fault = new InvalidOperationException("Synthetic wrapper", new IOException("Synthetic reset"));
        Assert.Equal(StorageReadbackFaults.FailureCategory.Transport, StorageReadbackFaults.Classify(fault));
    }

    [Fact]
    public void Service_response_fault_keeps_provider_response_category()
    {
        var fault = new AmazonS3Exception("Synthetic: SlowDown") { StatusCode = HttpStatusCode.ServiceUnavailable };
        Assert.Equal(StorageReadbackFaults.FailureCategory.ProviderResponse, StorageReadbackFaults.Classify(fault));
        Assert.True(StorageReadbackFaults.IsReadbackFault(fault));
    }

    [Fact]
    public void Unexpected_exception_is_not_a_readback_fault()
    {
        var defect = new NullReferenceException("Synthetic defect");
        Assert.Equal(StorageReadbackFaults.FailureCategory.Unexpected, StorageReadbackFaults.Classify(defect));
        Assert.False(StorageReadbackFaults.IsReadbackFault(defect));
    }

    [Fact]
    public void Find_cancellation_returns_the_cancellation_hidden_behind_the_wrapper()
    {
        var cancellation = new OperationCanceledException("Synthetic cancellation");
        var wrapped = new IOException("Synthetic transport wrapper", cancellation);
        Assert.Same(cancellation, StorageReadbackFaults.FindCancellation(wrapped));
        Assert.Null(StorageReadbackFaults.FindCancellation(new AmazonClientException("Synthetic fault")));
    }

    [Fact]
    public void Bound_honors_the_current_configuration_when_it_is_the_larger_value()
    {
        Assert.Equal(25L * 1024 * 1024,
            StorageReadbackFaults.ResolveReadbackBound(25L * 1024 * 1024, 5L * 1024 * 1024));
    }

    [Fact]
    public void Bound_honors_a_legacy_recorded_size_above_the_current_configuration()
    {
        // Policy: objects lawful under an earlier, larger limit stay readable
        // after the configured limit is lowered.
        Assert.Equal(20L * 1024 * 1024,
            StorageReadbackFaults.ResolveReadbackBound(5L * 1024 * 1024, 20L * 1024 * 1024));
    }

    [Fact]
    public void Bound_clamps_a_recorded_size_above_the_absolute_ceiling()
    {
        // A migrated or corrupted row claiming 4 GiB must not drive allocation.
        Assert.Equal(StorageReadbackFaults.AbsoluteReadbackCeilingBytes,
            StorageReadbackFaults.ResolveReadbackBound(25L * 1024 * 1024, 4L * 1024 * 1024 * 1024));
        Assert.Equal(256L * 1024 * 1024, StorageReadbackFaults.AbsoluteReadbackCeilingBytes);
    }

    [Fact]
    public void Bound_tolerates_a_negative_recorded_size()
    {
        Assert.Equal(25L * 1024 * 1024,
            StorageReadbackFaults.ResolveReadbackBound(25L * 1024 * 1024, -7L));
    }

    [Fact]
    public void Bound_never_exceeds_the_absolute_ceiling_even_for_a_huge_configuration()
    {
        Assert.Equal(StorageReadbackFaults.AbsoluteReadbackCeilingBytes,
            StorageReadbackFaults.ResolveReadbackBound(8L * 1024 * 1024 * 1024, 0L));
    }

    [Fact]
    public async Task Bounded_reader_fails_closed_above_the_resolved_ceiling()
    {
        var content = new byte[StorageReadbackFaults.AbsoluteReadbackCeilingBytes / 4 + 16];
        await Assert.ThrowsAsync<InvalidDataException>(() => StorageReadbackFaults.ReadBoundedAsync(
            new ReadOnlyStream(content), StorageReadbackFaults.AbsoluteReadbackCeilingBytes / 8, CancellationToken.None));
    }

    /// <summary>Reads in slices so the ceiling check engages mid-body.</summary>
    private sealed class ReadOnlyStream(byte[] payload) : MemoryStream(payload, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chunk = buffer[..Math.Min(buffer.Length, 64 * 1024)];
            return await base.ReadAsync(chunk, cancellationToken);
        }
    }
}
