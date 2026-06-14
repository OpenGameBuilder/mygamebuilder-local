using ZstdSharp;

namespace MyGameBuilder.Local.Api.Archives;

public static class SplitArchiveAssembler
{
    private const int BufferSize = 1024 * 1024;
    private const long ProgressByteInterval = 512L * 1024 * 1024;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromSeconds(5);

    public static void EnsureSqliteArchiveReady(string sqlitePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        ArgumentNullException.ThrowIfNull(logger);

        var fullSqlitePath = Path.GetFullPath(sqlitePath);
        if (File.Exists(fullSqlitePath))
        {
            return;
        }

        logger.LogInformation(
            "SQLite archive {SqlitePath} was not found. Checking for split or compressed archive files; any extraction is a one-time startup setup and future launches will reuse the SQLite file.",
            fullSqlitePath);

        if (TryCombineParts(fullSqlitePath, logger))
        {
            logger.LogInformation("One-time archive setup completed. Future launches will reuse {SqlitePath}.", fullSqlitePath);
            return;
        }

        var compressedPath = fullSqlitePath + ".zst";
        if (!File.Exists(compressedPath))
        {
            TryCombineParts(compressedPath, logger);
        }

        if (File.Exists(compressedPath))
        {
            DecompressZstd(compressedPath, fullSqlitePath, logger);
        }
    }

    private static bool TryCombineParts(string outputPath, ILogger logger)
    {
        if (File.Exists(outputPath))
        {
            return true;
        }

        var parts = FindParts(outputPath);
        if (parts.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        var tempPath = outputPath + ".assembling";
        File.Delete(tempPath);
        try
        {
            var totalBytes = parts.Sum(static part => new FileInfo(part.Path).Length);
            logger.LogInformation(
                "One-time archive setup: combining {Count} split archive part(s) ({TotalBytes}) into {OutputPath}",
                parts.Count,
                FormatBytes(totalBytes),
                outputPath);
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                var copiedBytes = 0L;
                var progress = new ProgressReporter(logger, "Combining archive parts", totalBytes);
                foreach (var part in parts)
                {
                    var partInfo = new FileInfo(part.Path);
                    logger.LogInformation(
                        "Combining part {PartNumber}/{PartCount}: {PartPath} ({PartBytes})",
                        part.Index + 1,
                        parts.Count,
                        part.Path,
                        FormatBytes(partInfo.Length));
                    using var input = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        output.Write(buffer, 0, read);
                        copiedBytes += read;
                        progress.Report(copiedBytes);
                    }
                }

                progress.Complete(copiedBytes);
            }

            File.Move(tempPath, outputPath);
            logger.LogInformation("Created combined archive file {OutputPath} ({OutputBytes})", outputPath, FormatBytes(new FileInfo(outputPath).Length));
            return true;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static IReadOnlyList<ArchivePart> FindParts(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(outputPath);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var parts = Directory
            .EnumerateFiles(directory, fileName + ".part-*", SearchOption.TopDirectoryOnly)
            .Select(path => new ArchivePart(path, TryParsePartIndex(path)))
            .Where(part => part.Index is not null)
            .Select(part => new ArchivePart(part.Path, part.Index!.Value))
            .OrderBy(part => part.Index)
            .ToArray();
        if (parts.Length == 0)
        {
            return [];
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Index != index)
            {
                throw new InvalidOperationException(
                    $"Split archive parts for '{outputPath}' are not contiguous. Missing part index {index:000}.");
            }
        }

        return parts;
    }

    private static int? TryParsePartIndex(string path)
    {
        var marker = ".part-";
        var fileName = Path.GetFileName(path);
        var markerIndex = fileName.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var suffix = fileName[(markerIndex + marker.Length)..];
        return int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= 0
            ? index
            : null;
    }

    private static void DecompressZstd(string compressedPath, string sqlitePath, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? Environment.CurrentDirectory);
        var tempPath = sqlitePath + ".decompressing";
        File.Delete(tempPath);
        try
        {
            var compressedBytes = new FileInfo(compressedPath).Length;
            logger.LogInformation(
                "One-time archive setup: decompressing {CompressedPath} ({CompressedBytes}) into {SqlitePath}",
                compressedPath,
                FormatBytes(compressedBytes),
                sqlitePath);
            using (var input = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var zstd = new DecompressionStream(input))
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                var outputBytes = 0L;
                var progress = new ProgressReporter(logger, "Decompressing archive", compressedBytes);
                while (true)
                {
                    var read = zstd.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    outputBytes += read;
                    progress.Report(input.Position, $"wrote {FormatBytes(outputBytes)}");
                }

                progress.Complete(input.Position, $"wrote {FormatBytes(outputBytes)}");
            }

            File.Move(tempPath, sqlitePath);
            logger.LogInformation("Created SQLite archive {SqlitePath} ({OutputBytes})", sqlitePath, FormatBytes(new FileInfo(sqlitePath).Length));
            logger.LogInformation("One-time archive setup completed. Future launches will reuse {SqlitePath}.", sqlitePath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after failed archive preparation.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var suffixIndex = 0;
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B"
            : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + suffixes[suffixIndex];
    }

    private sealed record ArchivePart(string Path, int? Index);

    private sealed class ProgressReporter
    {
        private readonly ILogger _logger;
        private readonly string _operation;
        private readonly long _totalBytes;
        private long _lastReportedBytes;
        private DateTimeOffset _lastReportedAt = DateTimeOffset.UtcNow;

        public ProgressReporter(ILogger logger, string operation, long totalBytes)
        {
            _logger = logger;
            _operation = operation;
            _totalBytes = totalBytes;
        }

        public void Report(long processedBytes, string? detail = null)
        {
            var now = DateTimeOffset.UtcNow;
            if (processedBytes - _lastReportedBytes < ProgressByteInterval &&
                now - _lastReportedAt < ProgressTimeInterval)
            {
                return;
            }

            Log(processedBytes, detail);
            _lastReportedBytes = processedBytes;
            _lastReportedAt = now;
        }

        public void Complete(long processedBytes, string? detail = null)
        {
            Log(processedBytes, detail);
        }

        private void Log(long processedBytes, string? detail)
        {
            var percent = _totalBytes <= 0 ? 100 : Math.Min(100, processedBytes * 100.0 / _totalBytes);
            if (string.IsNullOrWhiteSpace(detail))
            {
                _logger.LogInformation(
                    "{Operation}: {ProcessedBytes} / {TotalBytes} ({Percent:0.0}%)",
                    _operation,
                    FormatBytes(processedBytes),
                    FormatBytes(_totalBytes),
                    percent);
            }
            else
            {
                _logger.LogInformation(
                    "{Operation}: {ProcessedBytes} / {TotalBytes} ({Percent:0.0}%), {Detail}",
                    _operation,
                    FormatBytes(processedBytes),
                    FormatBytes(_totalBytes),
                    percent,
                    detail);
            }
        }
    }
}
