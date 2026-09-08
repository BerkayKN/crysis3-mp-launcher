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
            get => _totalFiles;
            set => _totalFiles = value;
        }

        public int DownloadedFiles => _downloadedFiles;

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
            _httpClient.DefaultRequestHeaders.Add("Keep-Alive", "true");
        }

        public void ResetCounters(long totalBytes = 0, int totalFiles = 0)
        {
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

        public async Task<byte[]> DownloadByteArrayAsync(string url, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public async Task DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var response = await _httpClient.GetAsync(Uri.EscapeUriString(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072]; // 128KB buffer

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _downloadedBytes, bytesRead);
                ReportProgress();
            }
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

            const int bufferSize = 262144; // 256KB buffer

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

        private async Task CombinePartsAsync(string[] partFiles, string outputPath)
        {
            using var outputStream = new FileStream(outputPath, FileMode.Create);
            foreach (var partFile in partFiles)
            {
                using var inputStream = new FileStream(partFile, FileMode.Open);
                await inputStream.CopyToAsync(outputStream).ConfigureAwait(false);
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
                                        Task.Delay(1000).Wait();
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

                double percentage = currentTotal > 0 ? (currentDownloaded * 100.0) / currentTotal : 0;
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
