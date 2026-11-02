using System.Buffers;
using System.Security.Cryptography;
using GachaBot.Application.Media;
using Microsoft.Extensions.Options;

namespace GachaBot.Infrastructure.Media;

public sealed class SafeRemoteMediaArchive(
    HttpClient httpClient,
    IHostAddressResolver addressResolver,
    IOptions<MediaArchiveOptions> options,
    IOptions<S3MediaOptions> s3Options,
    IMediaObjectStore objectStore) : IMediaArchive
{
    public async Task<ArchivedMedia> ArchiveAsync(
        MediaArchiveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentIdentity);
        ArgumentNullException.ThrowIfNull(request.SourceUrl);
        await RemoteMediaValidator.ValidateUriAsync(
            request.SourceUrl,
            addressResolver,
            cancellationToken).ConfigureAwait(false);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.SourceUrl);
        httpRequest.Headers.UserAgent.ParseAdd("GachaBot/1.0 (+media archive)");
        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var maximumDownloadBytes = checked(options.Value.MaximumDownloadSizeMegabytes * 1_024L * 1_024L);
        if (response.Content.Headers.ContentLength > maximumDownloadBytes)
        {
            throw new MediaSizeLimitExceededException(
                response.Content.Headers.ContentLength.Value,
                maximumDownloadBytes);
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var signature = new byte[12];
        var signatureLength = 0;
        while (signatureLength < signature.Length)
        {
            var read = await input.ReadAsync(
                signature.AsMemory(signatureLength, signature.Length - signatureLength),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            signatureLength += read;
        }

        var declaredContentType = response.Content.Headers.ContentType?.MediaType;
        var contentType = RemoteMediaValidator.ResolveContentType(
            declaredContentType,
            signature.AsSpan(0, signatureLength));

        var directory = Path.GetFullPath(options.Value.StagingPath);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using var output = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    Share = FileShare.None,
                });
            var buffer = ArrayPool<byte>.Shared.Rent(81_920);
            long total = signatureLength;
            try
            {
                await output.WriteAsync(signature.AsMemory(0, signatureLength), cancellationToken)
                    .ConfigureAwait(false);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maximumDownloadBytes)
                    {
                        throw new MediaSizeLimitExceededException(total, maximumDownloadBytes);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            CompressedImage compressed;
            MediaArchiveState state;
            string? processingNote = null;
            try
            {
                compressed = ArchivedImageCompressor.Compress(
                    temporaryPath,
                    contentType,
                    checked(options.Value.MaximumStoredImageSizeMegabytes * 1_024L * 1_024L),
                    options.Value.MaximumImageDimension);
                state = compressed.WasCompressed
                    ? MediaArchiveState.Compressed
                    : MediaArchiveState.StoredOriginal;
            }
            catch (MediaCompressionException exception)
            {
                compressed = new CompressedImage(
                    await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false),
                    contentType,
                    ArchivedImageCompressor.ExtensionFor(contentType),
                    false);
                state = MediaArchiveState.Uncompressable;
                processingNote = exception.Message;
            }
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(compressed.Bytes));
            var prefix = s3Options.Value.Prefix.Trim('/');
            var objectKey = string.IsNullOrWhiteSpace(prefix)
                ? $"{sha256}{compressed.Extension}"
                : $"{prefix}/{sha256}{compressed.Extension}";
            await objectStore.PutIfAbsentAsync(
                objectKey,
                compressed.Bytes,
                compressed.ContentType,
                cancellationToken).ConfigureAwait(false);

            var archived = new ArchivedMedia(
                objectKey,
                compressed.ContentType,
                compressed.Bytes.LongLength,
                sha256,
                total,
                compressed.WasCompressed,
                state,
                processingNote,
                objectKey);
            return archived;
        }
        finally
        {
            // The archived copy lives in object storage; the staged download is only needed until upload.
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

}

internal sealed class UnsupportedMediaTypeException(string contentType)
    : InvalidOperationException($"Media type '{contentType}' is not allowed.")
{
    public string ContentType { get; } = contentType;
}

public sealed class MediaSizeLimitExceededException(long actualBytes, long maximumBytes)
    : InvalidOperationException(
        $"Remote media size ({actualBytes} bytes) exceeds the configured limit ({maximumBytes} bytes).")
{
    public long ActualBytes { get; } = actualBytes;

    public long MaximumBytes { get; } = maximumBytes;
}
