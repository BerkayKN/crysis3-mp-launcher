using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Crysis3_MP_Launcher
{
    public class DownloadProgressReport
    {
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public double ProgressPercentage { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public int DownloadedFiles { get; set; }
        public int TotalFiles { get; set; }
        public string StatusMessage { get; set; }
    }

    public class DownloadManager : IDisposable
    {
        private HttpClient _httpClient;
        private readonly IProgress<DownloadProgressReport> _progressReporter;
        private readonly Action<string> _logger;

        private long _totalBytes;
        private long _downloadedBytes;
        private int _totalFiles;
        private int _downloadedFiles;

        private double _currentSpeed;
        private long _lastSpeedBytes;
        private DateTime _lastSpeedCheckTime = DateTime.Now;
        private DateTime _lastProgressReportTime = DateTime.MinValue;
        private readonly object _progressLock = new object();

        private readonly List<string> _failedDownloads = new List<string>();
        private bool _disposed;

        public long TotalBytes
        {
            get => Interlocked.Read(ref _totalBytes);
            set => Interlocked.Exchange(ref _totalBytes, value);
        }

        public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

        public int TotalFiles
        {
            get => Volatile.Read(ref _totalFiles);
            set => Volatile.Write(ref _totalFiles, value);
        }

        public int DownloadedFiles => Volatile.Read(ref _downloadedFiles);

        public IReadOnlyList<string> FailedDownloads
        {
            get
            {
                lock (_failedDownloads)
                {
                    return _failedDownloads.ToArray();
                }
            }
        }

        public DownloadManager(IProgress<DownloadProgressReport> progressReporter = null, Action<string> logger = null, string serverBaseUrl = null)
        {
            _progressReporter = progressReporter;
            _logger = logger ?? Logger.LogMessage;

            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            if (!string.IsNullOrEmpty(serverBaseUrl) && Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var serverUri))
            {
                try
                {
                    var sp = ServicePointManager.FindServicePoint(serverUri);
                    sp.ConnectionLimit = 100;
                    sp.UseNagleAlgorithm = false;
                    sp.Expect100Continue = false;
                }
                catch { /* Ignore */ }
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = false 
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
        }

        private bool _totalBytesExplicitlySet;

        public void ResetCounters(long totalBytes = 0, int totalFiles = 0)
        {
            _totalBytesExplicitlySet = totalBytes > 0 && totalFiles > 1;
            Interlocked.Exchange(ref _totalBytes, totalBytes);
            Interlocked.Exchange(ref _downloadedBytes, 0);
            _downloadedFiles = 0;
            _totalFiles = totalFiles;
            lock (_progressLock)
            {
                _lastSpeedBytes = 0;
                _lastSpeedCheckTime = DateTime.Now;
                _currentSpeed = 0;
                _lastProgressReportTime = DateTime.MinValue;
            }
            lock (_failedDownloads)
            {
                _failedDownloads.Clear();
            }
        }

        private void UpdateTotalBytes(long contentLength)
        {
            if (Volatile.Read(ref _totalFiles) <= 1)
            {
                Interlocked.Exchange(ref _totalBytes, contentLength);
            }
            else if (!_totalBytesExplicitlySet)
            {
                Interlocked.Add(ref _totalBytes, contentLength);
            }
        }

        public async Task<string> DownloadStringAsync(string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (cts != null && timeout.HasValue)
            {
                cts.CancelAfter(timeout.Value);
            }

            var token = cts?.Token ?? cancellationToken;
            using var response = await _httpClient.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public async Task<byte[]> DownloadByteArrayAsync(string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (cts != null && timeout.HasValue)
            {
                cts.CancelAfter(timeout.Value);
            }

            var token = cts?.Token ?? cancellationToken;
            using var response = await _httpClient.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public const long SegmentedDownloadThreshold = 20 * 1024 * 1024L; // 20 MB

        private static int CalculatePartCount(long totalSize)
        {
            if (totalSize < SegmentedDownloadThreshold) return 1;
            if (totalSize < 60 * 1024 * 1024L) return 4;
            if (totalSize < 150 * 1024 * 1024L) return Math.Min(6, Math.Max(4, Environment.ProcessorCount));
            return Math.Min(8, Math.Max(4, Environment.ProcessorCount));
        }

        public async Task DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var response = await _httpClient.GetAsync(Uri.EscapeUriString(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            bool supportsRanges = response.Headers.AcceptRanges != null &&
                (response.Headers.AcceptRanges.Contains("bytes") || response.Content.Headers.Contains("Content-Range"));

            if (contentLength.HasValue)
            {
                UpdateTotalBytes(contentLength.Value);
            }

            if (contentLength.HasValue && contentLength.Value >= SegmentedDownloadThreshold && supportsRanges)
            {
                response.Dispose();
                try
                {
                    await DownloadSegmentedAsync(url, filePath, contentLength.Value, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[DOWNLOAD] Multi-part download failed for {Path.GetFileName(filePath)} ({ex.Message}). Retrying with single stream...");
                    await DownloadSingleStreamAsync(url, filePath, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await StreamResponseToFileAsync(response, filePath, cancellationToken).ConfigureAwait(false);
        }

        private async Task DownloadSingleStreamAsync(string url, string filePath, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(Uri.EscapeUriString(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue)
            {
                UpdateTotalBytes(contentLength.Value);
            }

            await StreamResponseToFileAsync(response, filePath, cancellationToken).ConfigureAwait(false);
        }

        private async Task StreamResponseToFileAsync(HttpResponseMessage response, string filePath, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = filePath + ".tmp";
            const int bufferSize = 81920; // 80KB buffer (stays below 85,000 bytes LOH limit)

            long bytesThisAttempt = 0;
            try
            {
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true))
                {
                    var buffer = new byte[bufferSize];
                    while (true)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0) break;

                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        bytesThisAttempt += bytesRead;
                        Interlocked.Add(ref _downloadedBytes, bytesRead);
                        ReportProgress();
                    }
                }

                await SafeReplaceFileAsync(tempFilePath, filePath).ConfigureAwait(false);
            }
            catch
            {
                if (bytesThisAttempt > 0)
                {
                    Interlocked.Add(ref _downloadedBytes, -bytesThisAttempt);
                    ReportProgress();
                }

                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch { }

                throw;
            }
        }

        private async Task DownloadSegmentedAsync(string url, string filePath, long totalSize, CancellationToken cancellationToken)
        {
            UpdateTotalBytes(totalSize);

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = filePath + ".tmp";
            int partCount = CalculatePartCount(totalSize);

            long partSize = totalSize / partCount;
            var partFiles = new string[partCount];
            var tasks = new Task[partCount];

            long totalBytesAddedAcrossSegments = 0;

            for (int i = 0; i < partCount; i++)
            {
                partFiles[i] = $"{tempFilePath}.part{i}";
            }

            // Ensure any stale part or temp files from prior aborted attempts are cleaned up
            DeletePartFiles(partFiles);
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }

            string fileName = Path.GetFileName(filePath);
            _logger?.Invoke($"[DOWNLOAD] Multi-part download started for {fileName} ({partCount} parts, {totalSize:N0} bytes)");
            ReportProgress(force: true);

            try
            {
                for (int i = 0; i < partCount; i++)
                {
                    long start = partSize * i;
                    long end = (i == partCount - 1) ? totalSize - 1 : start + partSize - 1;
                    string partPath = partFiles[i];

                    tasks[i] = DownloadSegmentWithRetryAsync(
                        url,
                        partPath,
                        start,
                        end,
                        bytesRead =>
                        {
                            Interlocked.Add(ref _downloadedBytes, bytesRead);
                            Interlocked.Add(ref totalBytesAddedAcrossSegments, bytesRead);
                            ReportProgress();
                        },
                        bytesToRollback =>
                        {
                            Interlocked.Add(ref _downloadedBytes, -bytesToRollback);
                            Interlocked.Add(ref totalBytesAddedAcrossSegments, -bytesToRollback);
                            ReportProgress();
                        },
                        cancellationToken);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Combine downloaded parts into tempFilePath
                await CombinePartsAsync(partFiles, tempFilePath, cancellationToken).ConfigureAwait(false);

                // Verify assembled file size
                var tempFi = new FileInfo(tempFilePath);
                if (tempFi.Length != totalSize)
                {
                    throw new IOException($"Combined file size mismatch for {Path.GetFileName(filePath)}. Expected {totalSize} bytes, got {tempFi.Length} bytes.");
                }

                DeletePartFiles(partFiles);
                await SafeReplaceFileAsync(tempFilePath, filePath).ConfigureAwait(false);
                _logger?.Invoke($"[DOWNLOAD] Successfully downloaded and assembled {Path.GetFileName(filePath)}");
            }
            catch
            {
                long rollback = Interlocked.Read(ref totalBytesAddedAcrossSegments);
                if (rollback > 0)
                {
                    Interlocked.Add(ref _downloadedBytes, -rollback);
                    ReportProgress();
                }

                DeletePartFiles(partFiles);
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch { }

                throw;
            }
        }

        private async Task DownloadSegmentWithRetryAsync(
            string url,
            string partPath,
            long start,
            long end,
            Action<long> onBytesRead,
            Action<long> onRollback,
            CancellationToken cancellationToken)
        {
            const int maxSegmentRetries = 3;
            const int bufferSize = 81920; // 80KB buffer (stays below 85,000 bytes LOH limit)

            for (int attempt = 1; attempt <= maxSegmentRetries; attempt++)
            {
                long bytesThisAttempt = 0;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Uri.EscapeUriString(url));
                    request.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new HttpRequestException($"Server does not support partial content. Expected 206 Partial Content, received {response.StatusCode}.");
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true))
                    {
                        var buffer = new byte[bufferSize];
                        while (true)
                        {
                            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                            if (bytesRead == 0) break;

                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                            bytesThisAttempt += bytesRead;
                            onBytesRead(bytesRead);
                        }
                    }

                    long expectedSize = end - start + 1;
                    var fi = new FileInfo(partPath);
                    if (fi.Length != expectedSize)
                    {
                        throw new IOException($"Downloaded segment size mismatch. Expected {expectedSize} bytes, got {fi.Length} bytes.");
                    }

                    return;
                }
                catch (OperationCanceledException)
                {
                    if (bytesThisAttempt > 0)
                    {
                        onRollback(bytesThisAttempt);
                    }
                    try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    if (bytesThisAttempt > 0)
                    {
                        onRollback(bytesThisAttempt);
                    }
                    try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }

                    if (attempt == maxSegmentRetries)
                    {
                        _logger?.Invoke($"[DOWNLOAD] Segment {start}-{end} failed after {maxSegmentRetries} attempts: {ex.Message}");
                        throw;
                    }

                    _logger?.Invoke($"[DOWNLOAD] Segment {start}-{end} attempt {attempt} failed ({ex.Message}), retrying in {attempt}s...");
                    await Task.Delay(1000 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task SafeReplaceFileAsync(string sourceFile, string targetFile)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(targetFile))
                    {
                        var attr = File.GetAttributes(targetFile);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(targetFile, attr & ~FileAttributes.ReadOnly);
                        }
                        File.Delete(targetFile);
                    }
                    File.Move(sourceFile, targetFile);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < 4)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }

        private static void DeletePartFiles(IEnumerable<string> partFiles)
        {
            if (partFiles == null) return;
            foreach (var partFile in partFiles)
            {
                try
                {
                    if (File.Exists(partFile))
                    {
                        var attr = File.GetAttributes(partFile);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(partFile, attr & ~FileAttributes.ReadOnly);
                        }
                        File.Delete(partFile);
                    }
                }
                catch { }
            }
        }

        private static void CleanupTempAndPartFiles(string filePath)
        {
            try
            {
                string tempFilePath = filePath + ".tmp";
                if (File.Exists(tempFilePath))
                {
                    var attr = File.GetAttributes(tempFilePath);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(tempFilePath, attr & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(tempFilePath);
                }

                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string searchPattern = Path.GetFileName(filePath) + ".tmp.part*";
                    foreach (var part in Directory.GetFiles(dir, searchPattern))
                    {
                        try
                        {
                            var attr = File.GetAttributes(part);
                            if ((attr & FileAttributes.ReadOnly) != 0)
                            {
                                File.SetAttributes(part, attr & ~FileAttributes.ReadOnly);
                            }
                            File.Delete(part);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public async Task<bool> DownloadFileWithRetryAsync(string url, string filePath, int retryCount = 5, IProgress<string> statusProgress = null, CancellationToken cancellationToken = default)
        {
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    await DownloadFileAsync(url, filePath, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _downloadedFiles);
                    ReportProgress(force: true);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger?.Invoke($"[DOWNLOAD] Retry {attempt}/{retryCount} for {Path.GetFileName(filePath)} failed: {ex.Message}");
                    if (attempt == retryCount)
                    {
                        CleanupTempAndPartFiles(filePath);
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        lock (_failedDownloads)
                        {
                            _failedDownloads.Add(url);
                        }
                        statusProgress?.Report($"Failed to download: {Path.GetFileName(filePath)}");
                        return false;
                    }
                    await Task.Delay(1000 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[DOWNLOAD] Error downloading {Path.GetFileName(filePath)}: {ex.Message}");
                    if (attempt == retryCount)
                    {
                        CleanupTempAndPartFiles(filePath);
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        lock (_failedDownloads)
                        {
                            _failedDownloads.Add(url);
                        }
                        statusProgress?.Report($"Failed to download: {Path.GetFileName(filePath)}");
                        return false;
                    }
                    await Task.Delay(1000 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
            return false;
        }

        public async Task DownloadAndExtractZipAsync(string zipUrl, string destinationFolder, IProgress<string> progress = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            string tempZipPath = Path.Combine(Path.GetTempPath(), $"openspy_temp_{Guid.NewGuid()}.zip");
            try
            {
                using var response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long totalSize = response.Content.Headers.ContentLength ?? -1;
                ResetCounters(totalSize, 1);

                int partCount = Environment.ProcessorCount;
                if (totalSize > 100 * 1024 * 1024)
                {
                    partCount = Math.Min(partCount * 2, Environment.ProcessorCount);
                }

                long partSize = totalSize / partCount;
                var tasks = new List<Task>();
                var partFiles = new string[partCount];

                progress?.Report($"Downloading with {partCount} threads...");

                for (int i = 0; i < partCount; i++)
                {
                    long start = partSize * i;
                    long end = (i == partCount - 1) ? totalSize - 1 : start + partSize - 1;
                    partFiles[i] = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid()}.tmp");

                    tasks.Add(DownloadPartAsync(zipUrl, partFiles[i], start, end, cancellationToken));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                progress?.Report("Combining downloaded parts...");
                await CombinePartsAsync(partFiles, tempZipPath).ConfigureAwait(false);

                foreach (var partFile in partFiles)
                {
                    try { File.Delete(partFile); } catch { }
                }

                progress?.Report("Starting extraction...");
                await ExtractZipParallelAsync(tempZipPath, destinationFolder, progress).ConfigureAwait(false);

                progress?.Report("Ready");
            }
            finally
            {
                await CleanupTempFileAsync(tempZipPath).ConfigureAwait(false);
            }
        }

        private async Task DownloadPartAsync(string url, string partPath, long start, long end, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);

            const int bufferSize = 81920; // 80KB buffer (stays below 85,000 bytes LOH limit)

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous);

            var buffer = new byte[bufferSize];

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _downloadedBytes, bytesRead);
                ReportProgress();
            }
        }

        private static async Task CombinePartsAsync(string[] partFiles, string outputPath, CancellationToken cancellationToken = default)
        {
            const int bufferSize = 81920; // 80KB buffer
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);
            foreach (var partFile in partFiles)
            {
                using var inputStream = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
                await inputStream.CopyToAsync(outputStream, bufferSize, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task ExtractZipParallelAsync(string zipPath, string destinationFolder, IProgress<string> progress = null)
        {
            try
            {
                progress?.Report("Extracting mod package...");
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        int totalEntries = archive.Entries.Count;
                        int currentEntry = 0;

                        foreach (var entry in archive.Entries)
                        {
                            currentEntry++;
                            string fullPath = Path.GetFullPath(Path.Combine(destinationFolder, entry.FullName));

                            if (entry.FullName.EndsWith("/"))
                            {
                                Directory.CreateDirectory(fullPath);
                            }
                            else
                            {
                                string dir = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(dir))
                                {
                                    Directory.CreateDirectory(dir);
                                }

                                for (int retries = 0; retries < 3; retries++)
                                {
                                    try
                                    {
                                        entry.ExtractToFile(fullPath, true);
                                        break;
                                    }
                                    catch (IOException) when (retries < 2)
                                    {
                                        Thread.Sleep(1000);
                                    }
                                }
                            }

                            var extractProgress = (double)currentEntry / totalEntries * 100;
                            _progressReporter?.Report(new DownloadProgressReport
                            {
                                ProgressPercentage = extractProgress,
                                DownloadedFiles = currentEntry,
                                TotalFiles = totalEntries,
                                StatusMessage = $"Extracting: {currentEntry}/{totalEntries} files"
                            });
                        }
                    }
                }).ConfigureAwait(false);

                progress?.Report("Ready");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[CRITICAL] ZIP extraction failed: {ex.Message}");
                throw new Exception("Error occurred while extracting ZIP file.", ex);
            }
        }

        public static async Task CleanupTempFileAsync(string tempFile, Action<string> onError = null)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                    break;
                }
                catch when (i < 4)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Unable to delete temporary file: {tempFile}\nError: {ex.Message}");
                }
            }
        }

        private void ReportProgress(string statusMessage = null, bool force = false)
        {
            if (_progressReporter == null) return;

            var now = DateTime.Now;

            if (!force && (now - _lastProgressReportTime).TotalMilliseconds < 100)
            {
                return;
            }

            lock (_progressLock)
            {
                now = DateTime.Now;
                if (!force && (now - _lastProgressReportTime).TotalMilliseconds < 100)
                {
                    return;
                }
                _lastProgressReportTime = now;

                long currentDownloaded = Interlocked.Read(ref _downloadedBytes);
                long currentTotal = Interlocked.Read(ref _totalBytes);
                int currentFileIndex = _downloadedFiles;
                int currentTotalFiles = _totalFiles;

                // Sample speed strictly on a stable time window of at least 500ms
                // 'force' must NEVER recalculate or reset speed on micro-intervals!
                var speedTimeDiff = (now - _lastSpeedCheckTime).TotalSeconds;
                if (speedTimeDiff >= 0.5)
                {
                    long bytesDiff = currentDownloaded - _lastSpeedBytes;
                    if (bytesDiff >= 0 && speedTimeDiff > 0)
                    {
                        double instantSpeed = bytesDiff / speedTimeDiff;
                        _currentSpeed = _currentSpeed <= 0 ? instantSpeed : (_currentSpeed * 0.6) + (instantSpeed * 0.4);
                    }
                    _lastSpeedBytes = currentDownloaded;
                    _lastSpeedCheckTime = now;
                }

                double percentage = 0;
                if (currentTotal > 0)
                {
                    percentage = (currentDownloaded * 100.0) / currentTotal;
                }
                else if (currentTotalFiles > 0)
                {
                    percentage = (currentFileIndex * 100.0) / currentTotalFiles;
                }
                if (percentage > 100.0) percentage = 100.0;

                try
                {
                    _progressReporter.Report(new DownloadProgressReport
                    {
                        DownloadedBytes = currentDownloaded,
                        TotalBytes = currentTotal,
                        ProgressPercentage = percentage,
                        SpeedBytesPerSecond = _currentSpeed,
                        DownloadedFiles = currentFileIndex,
                        TotalFiles = currentTotalFiles,
                        StatusMessage = statusMessage
                    });
                }
                catch
                {
                    // Ignore reporter errors
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed || _httpClient == null)
            {
                throw new ObjectDisposedException(nameof(DownloadManager));
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                    _httpClient = null;
                }
                _disposed = true;
            }
        }
    }
}
