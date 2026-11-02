using System.Net;
using GachaBot.Application.Media;
using GachaBot.Infrastructure.Media;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class SafeRemoteMediaArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gachabot-media-{Guid.NewGuid():N}");

    [Fact]
    public void Options_Defaults_SeparateDownloadAndDiscordSafeLimits()
    {
        var options = new MediaArchiveOptions();

        Assert.Equal(50, options.MaximumDownloadSizeMegabytes);
        Assert.Equal(9, options.MaximumStoredImageSizeMegabytes);
        Assert.Equal(4096, options.MaximumImageDimension);
        Assert.Equal(60, options.AttemptTimeoutSeconds);
        Assert.Equal(180, options.TotalRequestTimeoutSeconds);
    }

    [Fact]
    public async Task ArchiveAsync_ForPublicImage_UploadsContentAddressedObject()
    {
        var bytes = TinyPng();
        using var client = new HttpClient(new ImageResponseHandler(bytes));
        var archive = CreateArchive(client, OneMegabyteOptions());

        var result = await archive.ArchiveAsync(
            Request("official", "42", "https://cdn.example.com/banner.png"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Length <= bytes.Length);
        Assert.Equal(bytes.Length, result.OriginalLength);
        Assert.True(File.Exists(Path.Combine(_root, result.RelativePath)));
        Assert.StartsWith("media/", result.RelativePath, StringComparison.Ordinal);
        Assert.Equal(result.RelativePath, result.ObjectKey);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "staging")));
        Assert.Contains(result.State, new[]
        {
            MediaArchiveState.Compressed,
            MediaArchiveState.StoredOriginal,
        });
    }

    [Fact]
    public async Task ArchiveAsync_WhenExternalIdIsUnsafe_DoesNotAffectObjectKey()
    {
        using var client = new HttpClient(new ImageResponseHandler(TinyPng()));
        var archive = CreateArchive(client, OneMegabyteOptions());

        var result = await archive.ArchiveAsync(
            Request("official", "news/../../42", "https://cdn.example.com/banner.png"),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("media/", result.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", result.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Compressor_LargeImage_ProducesDiscordSafeWebpAndResizesDimensions()
    {
        var inputPath = Path.Combine(_root, "large.png");
        Directory.CreateDirectory(_root);
        using (var bitmap = new SKBitmap(2048, 1024))
        using (var canvas = new SKCanvas(bitmap))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var output = File.Create(inputPath);
            encoded.SaveTo(output);
        }

        var result = ArchivedImageCompressor.Compress(inputPath, "image/png", 1_024 * 1_024, 320);

        Assert.True(result.WasCompressed);
        Assert.Equal("image/webp", result.ContentType);
        Assert.True(result.Bytes.LongLength <= 1_024 * 1_024);
        using var resized = SKBitmap.Decode(result.Bytes);
        Assert.True(Math.Max(resized.Width, resized.Height) <= 320);
    }

    [Fact]
    public async Task ArchiveAsync_OversizedAnimatedGif_StoresOriginalAsUncompressable()
    {
        var bytes = new byte[1_024 * 1_024 + 1];
        "GIF89a"u8.CopyTo(bytes);
        using var client = new HttpClient(new ImageResponseHandler(bytes, "image/gif"));
        var archive = CreateArchive(client, new MediaArchiveOptions
        {
            RootPath = _root,
            MaximumDownloadSizeMegabytes = 2,
            MaximumStoredImageSizeMegabytes = 1,
        });

        var result = await archive.ArchiveAsync(
            Request("official", "animated", "https://cdn.example.com/animated.gif"),
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaArchiveState.Uncompressable, result.State);
        Assert.Equal(bytes.LongLength, result.Length);
        Assert.Equal("image/gif", result.ContentType);
        Assert.NotNull(result.ProcessingNote);
        Assert.True(File.Exists(Path.Combine(_root, result.RelativePath)));
    }

    [Fact]
    public async Task ArchiveAsync_WhenDeclaredLengthExceedsDownloadLimit_ReportsSizes()
    {
        var bytes = new byte[1_024 * 1_024 + 1];
        using var client = new HttpClient(new ImageResponseHandler(bytes));
        var archive = CreateArchive(client, OneMegabyteOptions());

        var exception = await Assert.ThrowsAsync<MediaSizeLimitExceededException>(() => archive.ArchiveAsync(
            Request("official", "oversized", "https://cdn.example.com/oversized.png"),
            TestContext.Current.CancellationToken));

        Assert.Equal(bytes.LongLength, exception.ActualBytes);
        Assert.Equal(1_024L * 1_024L, exception.MaximumBytes);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ArchiveAsync_WhenStreamWithoutLengthExceedsLimit_RemovesPartialFile()
    {
        var bytes = new byte[1_024 * 1_024 + 1];
        TinyPng().AsSpan(0, 8).CopyTo(bytes);
        using var client = new HttpClient(new StreamingImageResponseHandler(bytes));
        var archive = CreateArchive(client, OneMegabyteOptions());

        var exception = await Assert.ThrowsAsync<MediaSizeLimitExceededException>(() => archive.ArchiveAsync(
            Request("official", "stream-oversized", "https://cdn.example.com/stream-oversized.png"),
            TestContext.Current.CancellationToken));

        Assert.True(exception.ActualBytes > exception.MaximumBytes);
        Assert.Empty(Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task ArchiveAsync_UsesSameObjectKeyForSameCompressedContent()
    {
        var mediaOptions = OneMegabyteOptions();
        using var client = new HttpClient(new ImageResponseHandler(TinyPng()));
        var archive = CreateArchive(client, mediaOptions);
        var url = new Uri("https://cdn.example.com/banner.png");
        var first = await archive.ArchiveAsync(
            Request("official", "42", url.AbsoluteUri),
            TestContext.Current.CancellationToken);
        var second = await archive.ArchiveAsync(
            Request("official", "other", url.AbsoluteUri),
            TestContext.Current.CancellationToken);

        Assert.Equal(first.ObjectKey, second.ObjectKey);
    }

    [Fact]
    public async Task ArchiveAsync_ForLoopbackAddress_RejectsBeforeRequest()
    {
        using var client = new HttpClient(new FailingHandler());
        var archive = CreateArchive(
            client,
            new MediaArchiveOptions { RootPath = _root },
            IPAddress.Loopback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => archive.ArchiveAsync(
            Request("official", "42", "https://127.0.0.1/image.png"),
            TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private MediaArchiveOptions OneMegabyteOptions() => new()
    {
        RootPath = _root,
        MaximumDownloadSizeMegabytes = 1,
        MaximumStoredImageSizeMegabytes = 1,
    };

    private static SafeRemoteMediaArchive CreateArchive(
        HttpClient client,
        MediaArchiveOptions archiveOptions,
        params IPAddress[] addresses)
    {
        var configured = Options.Create(new MediaArchiveOptions
        {
            RootPath = archiveOptions.RootPath,
            StagingPath = archiveOptions.StagingPath == "data/media-staging"
                ? Path.Combine(archiveOptions.RootPath, "staging")
                : archiveOptions.StagingPath,
            MaximumDownloadSizeMegabytes = archiveOptions.MaximumDownloadSizeMegabytes,
            MaximumStoredImageSizeMegabytes = archiveOptions.MaximumStoredImageSizeMegabytes,
            MaximumImageDimension = archiveOptions.MaximumImageDimension,
            AttemptTimeoutSeconds = archiveOptions.AttemptTimeoutSeconds,
            TotalRequestTimeoutSeconds = archiveOptions.TotalRequestTimeoutSeconds,
        });
        var s3 = Options.Create(new S3MediaOptions
        {
            Endpoint = "http://garage.test",
            Region = "garage",
            AccessKey = "test",
            SecretKey = "test",
            Bucket = "gachabot-media",
            Prefix = "media",
        });
        return new SafeRemoteMediaArchive(
            client,
            new StaticAddressResolver(addresses.Length == 0 ? [IPAddress.Parse("1.1.1.1")] : addresses),
            configured,
            s3,
            new TestObjectStore(archiveOptions.RootPath));
    }

    private static MediaArchiveRequest Request(string sourceKey, string externalId, string url) =>
        new(sourceKey, externalId, $"{sourceKey}:{externalId}", new Uri(url));

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed class StaticAddressResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }

    private sealed class ImageResponseHandler(
        byte[] bytes,
        string contentType = "image/png") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StreamingImageResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingImageContent(bytes),
            });
    }

    private sealed class StreamingImageContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The HTTP request should not be sent.");
    }

    private sealed class TestObjectStore(string root) : IMediaObjectStore
    {
        public async Task PutIfAbsentAsync(string objectKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
        {
            var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
            }
        }

        public Task<DownloadedMediaObject?> TryDownloadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<DownloadedMediaObject?>(null);

        public async IAsyncEnumerable<MediaObjectInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
