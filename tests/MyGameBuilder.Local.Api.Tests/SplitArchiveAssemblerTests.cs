using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyGameBuilder.Local.Api.Archives;
using ZstdSharp;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class SplitArchiveAssemblerTests
{
    [Fact]
    public void ReassemblesPlainSplitSqliteArchive()
    {
        var directory = CreateTempDirectory();
        var archivePath = Path.Combine(directory, "archive.sqlite");
        WriteParts(archivePath, [1, 2, 3, 4, 5, 6, 7], partSize: 3);

        SplitArchiveAssembler.EnsureSqliteArchiveReady(archivePath, NullLogger.Instance);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], File.ReadAllBytes(archivePath));
    }

    [Fact]
    public void ReassemblesAndDecompressesSplitZstdArchive()
    {
        var directory = CreateTempDirectory();
        var archivePath = Path.Combine(directory, "archive.sqlite");
        byte[] sqliteBytes = [9, 8, 7, 6, 5, 4, 3, 2];
        var compressedPath = archivePath + ".zst";
        WriteParts(compressedPath, CompressZstd(sqliteBytes), partSize: 5);
        var logger = new ListLogger();

        SplitArchiveAssembler.EnsureSqliteArchiveReady(archivePath, logger);

        Assert.Equal(sqliteBytes, File.ReadAllBytes(archivePath));
        Assert.True(File.Exists(compressedPath));
        Assert.Contains(logger.Messages, static message => message.Contains("one-time", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, static message => message.Contains("combining", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, static message => message.Contains("decompressing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, static message => message.Contains("future launches will reuse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingPartFailsClearly()
    {
        var directory = CreateTempDirectory();
        var archivePath = Path.Combine(directory, "archive.sqlite");
        File.WriteAllBytes(archivePath + ".part-001", [1]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SplitArchiveAssembler.EnsureSqliteArchiveReady(archivePath, NullLogger.Instance));

        Assert.Contains("not contiguous", ex.Message);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-split-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteParts(string outputPath, byte[] bytes, int partSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += partSize)
        {
            var partIndex = offset / partSize;
            var count = Math.Min(partSize, bytes.Length - offset);
            File.WriteAllBytes(
                outputPath + ".part-" + partIndex.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                bytes.AsSpan(offset, count).ToArray());
        }
    }

    private static byte[] CompressZstd(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, 1, 1024, leaveOpen: true))
        {
            zstd.Write(bytes);
        }

        return output.ToArray();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
