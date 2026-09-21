using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public interface IS3CompatibleObjectClient : IDisposable
{
    Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side object storage adapter for Cloudflare R2 and other S3-compatible providers.
/// </summary>
public sealed class S3CompatibleFileStorageService : IFileStorageService, IDisposable
{
    private readonly IS3CompatibleObjectClient client;
    private readonly FileOptions settings;
    private readonly ILogger<S3CompatibleFileStorageService> logger;

    public S3CompatibleFileStorageService(
        IOptions<FileOptions> options,
        ILogger<S3CompatibleFileStorageService> logger)
        : this(CreateClient(options.Value), options, logger)
    {
    }

    public S3CompatibleFileStorageService(
        IS3CompatibleObjectClient client,
        IOptions<FileOptions> options,
        ILogger<S3CompatibleFileStorageService> logger)
    {
        this.client = client;
        settings = options.Value;
        this.logger = logger;
    }

    public string ProviderKey => FileStorageProviders.S3Compatible;

    public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.PutAsync(settings.S3Bucket, storageKey, content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("put", exception);
            throw new FileStorageOperationException();
        }
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetAsync(settings.S3Bucket, storageKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("get", exception);
            throw new FileStorageOperationException();
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.DeleteAsync(settings.S3Bucket, storageKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("delete", exception);
            throw new FileStorageOperationException();
        }
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.ExistsAsync(settings.S3Bucket, storageKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("head", exception);
            throw new FileStorageOperationException();
        }
    }

    public void Dispose() => client.Dispose();

    private void LogFailure(string operation, Exception exception)
    {
        if (exception is AmazonS3Exception s3Exception)
        {
            logger.LogError(
                "S3-compatible storage operation failed. Operation={Operation}; StatusCode={StatusCode}; ErrorCode={ErrorCode}; ErrorType={ErrorType}; RequestId={RequestId}",
                operation,
                (int)s3Exception.StatusCode,
                s3Exception.ErrorCode,
                exception.GetType().Name,
                s3Exception.RequestId);
            return;
        }

        logger.LogError(
            "S3-compatible storage operation failed. Operation={Operation}; ErrorType={ErrorType}",
            operation,
            exception.GetType().Name);
    }

    private static IS3CompatibleObjectClient CreateClient(FileOptions settings)
    {
        if (!settings.IsS3Configured)
            throw new InvalidOperationException("S3-compatible storage configuration is incomplete.");

        var config = new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            AuthenticationRegion = settings.S3Region,
            ForcePathStyle = true,
            UseHttp = false,
        };
        var s3 = new AmazonS3Client(settings.S3AccessKey, settings.S3SecretKey, config);
        return new AwsS3CompatibleObjectClient(s3);
    }
}

internal sealed class AwsS3CompatibleObjectClient : IS3CompatibleObjectClient
{
    private readonly IAmazonS3? client;
    private readonly Func<GetObjectRequest, CancellationToken, Task<GetObjectResponse>> getObject;

    internal AwsS3CompatibleObjectClient(IAmazonS3 client)
    {
        this.client = client;
        getObject = (request, cancellationToken) => client.GetObjectAsync(request, cancellationToken);
    }

    internal AwsS3CompatibleObjectClient(Func<GetObjectRequest, CancellationToken, Task<GetObjectResponse>> getObject)
    {
        this.getObject = getObject;
    }

    public async Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
    {
        await client!.PutObjectAsync(CreatePutObjectRequest(bucket, key, content), cancellationToken);
    }

    internal static PutObjectRequest CreatePutObjectRequest(string bucket, string key, Stream content) => new()
    {
        BucketName = bucket,
        Key = key,
        InputStream = content,
        AutoCloseStream = false,
        AutoResetStreamPosition = false,
        DisablePayloadSigning = true,
        DisableDefaultChecksumValidation = true,
    };

    public async Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await getObject(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);

            if (response.ResponseStream is null) return null;

            var buffered = new MemoryStream();
            try
            {
                await response.ResponseStream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                return buffered;
            }
            catch
            {
                await buffered.DisposeAsync();
                throw;
            }
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        await client!.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = key,
        }, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await client!.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return false;
        }
    }

    public void Dispose() => client?.Dispose();

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);
}
