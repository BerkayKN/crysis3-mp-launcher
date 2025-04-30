//#define DirectoryStructureCheck
//#define UpdateChangelog
//#define CleanFilesNotInMd5List
//#define EnableServerList
//#define ENABLE_LOGGING

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Principal;
using System.Windows.Controls;


namespace C2COMMUNITY_Mod_Launcher
{
    public partial class MainWindow : Window, IComponentConnector
    {
        //Start of Defines
        private const string MD5_FILE_PATH = "/C3MP/ModFiles/md5sum.php";
        private const string WEBVIEW_DLL_PATH = "/openspymod/WebView2Loader.dll";
        public const string DEFAULT_SERVER_URL = "http://lb.crysis2.epicgamer.org";
        private const string GAME_MOD_FOLDER = "C3MP";
        private const string SERVER_MOD_PATH = "/C3MP/ModFiles/";
        private const string SERVER_LIST_SOURCE_URL = "https://openspy-website.nyc3.digitaloceanspaces.com/servers/capricorn.json";
        private const string GAME_STARTER_FILE_NAME = "Crysis3.exe";
        private const string GET_SERVER_URL = "https://raw.githubusercontent.com/BerkayKN/crysis2-mp-launcher/main/server/server.txt";
        //End of Defines

        private string _serverBaseUrl;
        private readonly string _bin32Folder;
        private readonly string _gameFolder;
        private string _md5Url;
        private string _zipUrl;
        private string _WebViewDLLUrl;
        private readonly string _webView2LoaderPath;
        private long _totalDownloadSize;
        private bool _isAdministrator;
        private long _downloadedSize;
        private string _jsonVersion;
        private readonly DispatcherTimer _serverTimer;
        //internal WebView2 webView;
        public ObservableCollection<Server> Servers { get; }

        private List<string> _failedDownloads = new List<string>();

        private long _lastBytesRead = 0;
        private DateTime _lastSpeedUpdate = DateTime.Now;
        
        private static readonly SemaphoreSlim _pathSemaphore = new SemaphoreSlim(1, 1);
        public string Version
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return $"v{version}";
            }
        }

        private int _downloadedFileCount;

        public MainWindow()
        {
            #if ENABLE_LOGGING
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                File.WriteAllText(logPath, $"=== Launcher Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                    LogMessage("[INIT] Launcher initialized");
                }
                catch { /* Ignore log creation errors */ }
            }
            #endif

            InitializeComponent();
            _bin32Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin32");
            _gameFolder = AppDomain.CurrentDomain.BaseDirectory;
            _webView2LoaderPath = Path.Combine(_gameFolder, "WebView2Loader.dll");
            _isAdministrator = IsAdministrator();
            LogMessage($"[INIT] Mod folder: {_gameFolder}");
            LogMessage($"[INIT] Bin32 folder: {_bin32Folder}");
            LogMessage($"[INIT] Administrator: {_isAdministrator}");

            UpdateWindowTitle();
            Servers = new ObservableCollection<Server>();
            serverListView.ItemsSource = Servers;
            
            #if EnableServerList
            _ = UpdateServerList();
            _serverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _serverTimer.Tick += async (s, e) => await UpdateServerList();
            _serverTimer.Start();
            #else
            // Hide and disable server list tab if EnableServerList is not defined
            var serverListTab = this.FindName("ServerlistTab") as TabItem;
            if (serverListTab != null)
            {
                serverListTab.Visibility = Visibility.Collapsed;
                serverListTab.IsEnabled = false;
            }
            #endif
            
#if !UpdateChangelog
            // Hide and disable changelog tab if UpdateChangelog is not defined
            ChangelogTab.Visibility = Visibility.Collapsed;
            ChangelogTab.IsEnabled = false;
#endif
          
            CheckDirectoryStructure();
        }

        private async Task InitializeAsync()
        {
            try
            {
                using var client = new HttpClient();
                _serverBaseUrl = await client.GetStringAsync(GET_SERVER_URL);
                _serverBaseUrl = string.IsNullOrWhiteSpace(_serverBaseUrl) ? DEFAULT_SERVER_URL : _serverBaseUrl.Trim().TrimEnd('/');
            }
            catch
            {
                _serverBaseUrl = DEFAULT_SERVER_URL;
            }
            _md5Url = $"{_serverBaseUrl}" + MD5_FILE_PATH;
            //_zipUrl = $"{_serverBaseUrl}{ZIP_FILE_PATH}";
            _WebViewDLLUrl = $"{_serverBaseUrl}{WEBVIEW_DLL_PATH}";
        }

        private bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

                if (!isAdmin)
                {
                    string adminWarningShown = LauncherDataManager.GetValue("adminwarning");
                    if (string.IsNullOrEmpty(adminWarningShown) || adminWarningShown == "0")
                    {
                        MessageBox.Show(
                            "Warning: The launcher might not work properly without administrator privileges.\n" +
                            "If you experience any issues, please run the launcher as administrator.",
                            "Administrator Rights Warning",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        LauncherDataManager.SetValue("adminwarning", "1");
                    }
                }

                return isAdmin;
            }
        }

        private void UpdateWindowTitle()
        {
            if (!_isAdministrator)
            {
                this.Title += " (Not Administrator)";
            }
        }

        private async void CheckDirectoryStructure()
        {
            launchGameButton.IsEnabled = false;
#if UpdateChangelog
            ChangelogTab.IsEnabled = true; // Previously was false, new ui changes mostly fixed the freezing issue
#endif
            if (!Directory.Exists(_bin32Folder))
            {
                string message = File.Exists(Path.Combine(_gameFolder, "Crysis2.exe"))
                    ? "The launcher is inside Bin32 folder. Please place the launcher in the Crysis 3 root folder."
                    : "The launcher is outside the Crysis 3 root folder. Please place the launcher in the Crysis 3 root folder.";
                MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // First check for launcher updates and wait for it to complete
            await VersionChecker.CheckForUpdates();
            
            // After launcher update check is complete, then proceed with mod files check
            await UpdateStatusLabelAsync("Checking mod files...");
            await InitializeAsync();
            
            var progress = new Progress<string>(message => statusLabel.Content = message);
            await Task.Run(() => CheckAndUpdateModFiles(progress));
            
            launchGameButton.IsEnabled = true;
        }

        private async Task CheckAndUpdateModFiles(IProgress<string> progress)
        {
            try
            {
                _downloadedSize = 0;
                _failedDownloads.Clear();
                bool pathsCorrected = false;

                do
                {
                    pathsCorrected = false;
                    progress.Report("Downloading file list...");
                    string md5Data = await DownloadStringAsync(_md5Url);
                    progress.Report("Parsing file list data...");
                    var fileHashes = ParseMd5Data(md5Data);
                    
                    if (fileHashes == null || fileHashes.Count == 0)
                    {
                        progress.Report("Failed to load file list data. Aborting.");
                        return;
                    }
                   
                    // MD5 checks for total file count
                    int totalFiles = fileHashes.Count;
                    int checkedFiles = 0;

                    #if DirectoryStructureCheck
                    await UpdateStatusLabelAsync("Checking file paths...");
                    pathsCorrected = await CheckDirectoryCasings(fileHashes, progress);

                    if (pathsCorrected)
                    {
                        await UpdateStatusLabelAsync("File paths were corrected, rechecking...");
                        continue;
                    }
                    #endif

                    dynamic val = JsonConvert.DeserializeObject<object>(md5Data);
                    

                    await Dispatcher.InvokeAsync(() =>
                    {
                        progressBar.Value = 0;
                        //progressLabel.Content = $"Checking files: 0 / {totalFiles}";
                    });

                    long existingFilesSize = CalculateExistingAndValidFilesSize(fileHashes, progress, totalFiles);
                    _totalDownloadSize = _totalDownloadSize - existingFilesSize;
                    
                    progress.Report($"Checking file hashes (0/{fileHashes.Count})");

                    // Determine files to download
                    List<KeyValuePair<string, string>> filesToDownload = new List<KeyValuePair<string, string>>();
                    int hashesChecked = 0;

                    foreach(var fileHash in fileHashes)
                    {
                        string path = fileHash.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                        string localPath = Path.Combine(_gameFolder, path);
                        
                        hashesChecked++;
                        progress.Report($"Checking file hashes ({hashesChecked}/{fileHashes.Count})");
                        
                        if(!File.Exists(localPath) || ComputeMD5(localPath) != fileHash.Value)
                        {
                            filesToDownload.Add(fileHash);
                        }
                    }

                    int totalFilesToDownload = filesToDownload.Count;
                    int remainingFiles = totalFilesToDownload;

                    // Prepare for parallel download
                    int processorCount = Environment.ProcessorCount;
                    int concurrentDownloads = Math.Min(8, Math.Max(2, processorCount));
                    List<Task> downloadTasks = new List<Task>();
                    SemaphoreSlim semaphore = new SemaphoreSlim(concurrentDownloads);

                    progress.Report($"Using {concurrentDownloads} concurrent downloads for {totalFilesToDownload} files...");

                    // Create parallel download task for each file
                    foreach (var file in filesToDownload)
                    {
                        string url = file.Key;
                        string path = url.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                        string filePath = Path.Combine(_gameFolder, path);
                        
                        await semaphore.WaitAsync();
                        
                        downloadTasks.Add(Task.Run(async () => {
                            try
                            {
                                await DownloadFileWithRetryAsync(url, filePath, 5, progress, totalFilesToDownload);
                                var remaining = Interlocked.Decrement(ref remainingFiles);
                                //progress.Report($"Downloading files (Remaining: {remaining}/{totalFilesToDownload})...");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }

                    // Wait for all downloads to complete
                    await Task.WhenAll(downloadTasks);

                    // After downloads complete, delete files not in MD5 list

                    #if CleanFilesNotInMd5List
                    progress.Report("Cleaning up old files...");
                    DeleteFilesNotInMd5List(fileHashes, progress);
                    #endif

                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateProgressBar(0, 0, 0, 0, string.Empty);
                        HideDownloadLabels();
                        UpdateVersionLabel();
#if UpdateChangelog
                        ChangelogTab.IsEnabled = true;
#endif
                    });
                    
                    progress.Report("Ready to play");

                } while (pathsCorrected);

                #if UpdateChangelog
                // WebView2 DLL kontrolü
                if (!File.Exists(_webView2LoaderPath))
                {
                    using var client = new HttpClient();
                    try
                    {
                        byte[] dllBytes = await client.GetByteArrayAsync(_WebViewDLLUrl);
                        File.WriteAllBytes(_webView2LoaderPath, dllBytes);
                    }
                    catch { /* Ignore errors */ }
                }
                #endif

            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"HTTP request error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private long CalculateExistingAndValidFilesSize(Dictionary<string, string> fileHashes, 
            IProgress<string> progress, int totalFiles)
        {
            long totalExistingSize = 0;
            int checkedFiles = 0;

            foreach (var fileHash in fileHashes)
            {
                string relativePath = fileHash.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                string filePath = Path.Combine(_gameFolder, relativePath);
                
                checkedFiles++;
                
                // Progress bar'ı güncelle
                Dispatcher.Invoke(() =>
                {
                    double percentage = (checkedFiles * 100.0) / totalFiles;
                    progressBar.Value = percentage;
                    //progressLabel.Content = $"Checking files: {checkedFiles} / {totalFiles}";
                });
                
                if (File.Exists(filePath) && ComputeMD5(filePath) == fileHash.Value)
                {
                    totalExistingSize += new FileInfo(filePath).Length;
                }
            }
            return totalExistingSize;
        }

        private Task UpdateStatusLabelAsync(string message)
        {
            return Dispatcher.InvokeAsync(() => statusLabel.Content = message).Task;
        }

        private void HideDownloadLabels() => Dispatcher.Invoke(() =>
        {
            progressBar.Value = 0;
            netSpeedLabel.Content = string.Empty;
            progressLabel.Content = string.Empty;
        });
        

        private void DeleteFilesNotInMd5List(Dictionary<string, string> fileHashes, IProgress<string> progress)
        {
            string modSpyModFolder = Path.Combine(_gameFolder, "Mods", GAME_MOD_FOLDER);
            
            // First check and delete files
            foreach (string filePath in Directory.EnumerateFiles(modSpyModFolder, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Replace(modSpyModFolder, "")
                    .TrimStart(Path.DirectorySeparatorChar)
                    .Replace('\\', '/');
                    
                string url = Uri.UnescapeDataString($"{_serverBaseUrl}{SERVER_MOD_PATH}Mods/{GAME_MOD_FOLDER}/{Uri.EscapeDataString(relativePath)}");
                
                if (!fileHashes.ContainsKey(url))
                {
                    try
                    {
                        progress.Report($"Deleting file: {Path.GetFileName(filePath)}...");
                        LogMessage($"[DELETE] {filePath} - File deleted because it was not found in server's MD5 list (old/unnecessary file)");
                        File.Delete(filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error deleting file {filePath}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            // Then check and delete directories not in MD5 list
            foreach (string dirPath in Directory.EnumerateDirectories(modSpyModFolder, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                string relativePath = dirPath.Replace(modSpyModFolder, "")
                    .TrimStart(Path.DirectorySeparatorChar)
                    .Replace('\\', '/');

                string url = Uri.UnescapeDataString($"{_serverBaseUrl}{SERVER_MOD_PATH}Mods/{GAME_MOD_FOLDER}/{Uri.EscapeDataString(relativePath)}");

                bool hasMatchingFiles = false;
                foreach (var hash in fileHashes)
                {
                    if (hash.Key.StartsWith(url))
                    {
                        hasMatchingFiles = true;
                        break;
                    }
                }

                if (!hasMatchingFiles)
                {
                    try
                    {
                        progress.Report($"Deleting directory: {Path.GetFileName(dirPath)}...");
                        LogMessage($"[DELETE] {dirPath} - Directory deleted because it was not found in server's MD5 list");
                        Directory.Delete(dirPath, true);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error deleting directory {dirPath}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            return await client.GetStringAsync(url);
        }

        private Dictionary<string, string> ParseMd5Data(string md5Data)
        {
            try
            {
                var jsonData = JObject.Parse(md5Data);
                _jsonVersion = jsonData["version"]?.ToString() ?? "Unknown version";
                _totalDownloadSize = jsonData["totalSize"]?.ToObject<long>() ?? 0;
                return jsonData["files"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
            }
            catch (JsonReaderException ex)
            {
                MessageBox.Show($"Error parsing MD5 data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return new Dictionary<string, string>();
        }

        private string ComputeMD5(string filePath)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error computing MD5 for {filePath}: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task DownloadFileAsync(string url, string filePath, int totalFiles)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Keep-Alive", "true");
            client.Timeout = Timeout.InfiniteTimeSpan;
            
            try
            {
                using var response = await client.GetAsync(Uri.EscapeUriString(url), HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                long fileSize = response.Content.Headers.ContentLength ?? -1;

                Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                var buffer = new byte[131072]; // 128KB buffer
                long totalBytesRead = 0;

                while (true)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    
                    Interlocked.Add(ref _downloadedSize, bytesRead);
                    
                    double progressPercentage = (_downloadedSize * 100.0) / _totalDownloadSize;
                    
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateProgressBar(
                            (int)progressPercentage, 
                            _downloadedSize, 
                            _totalDownloadSize, 
                            0,
                            $"Downloading files... ({_downloadedFileCount + 1}/{totalFiles})"
                        );
                    });
                }
            }
            catch
            {
                throw;
            }
        }

        private async Task<bool> DownloadFileWithRetryAsync(string url, string filePath, int retryCount, IProgress<string> progress, int totalFiles)
        {
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.Expect100Continue = false;
            
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    await DownloadFileAsync(url, filePath, totalFiles);
                    var downloadedCount = Interlocked.Increment(ref _downloadedFileCount);
                    //progress.Report($"Downloading files ({downloadedCount}/{totalFiles})");
                    return true;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == retryCount)
                    {
                        _failedDownloads.Add(url);
                        progress.Report($"Failed to download: {Path.GetFileName(filePath)}");
                        return false;
                    }
                    await Task.Delay(1000 * attempt);
                }
                catch (Exception)
                {
                    _failedDownloads.Add(url);
                    progress.Report($"Failed to download: {Path.GetFileName(filePath)}");
                    return false;
                }
            }
            return false;
        }

        
     private async Task DownloadAndExtractZipAsync(string zipUrl, string destinationFolder, IProgress<string> progress)
        {
            string tempZipPath = Path.Combine(Path.GetTempPath(), $"openspy_temp_{Guid.NewGuid()}.zip");
            
            try
            {
                // Reset counters before download
                _downloadedSize = 0;
                _lastBytesRead = 0;
                _lastSpeedUpdate = DateTime.Now;

                using var client = new HttpClient();
                using var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var totalSize = response.Content.Headers.ContentLength ?? -1;
                _totalDownloadSize = totalSize;

                // Configure ServicePoint settings for better performance
                ServicePointManager.DefaultConnectionLimit = 100;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false; // Disable Nagle's algorithm
                
                // Calculate optimal part size based on file size
                int partCount = Environment.ProcessorCount;
                if (totalSize > 100 * 1024 * 1024) // If file is larger than 100MB
                {
                    partCount = Math.Min(partCount * 2, Environment.ProcessorCount); // Double the parts, max processor count
                }
                
                // Prepare for multi-part download
                var partSize = totalSize / partCount;
                var tasks = new List<Task>();
                var partFiles = new string[partCount];
                
                progress.Report($"Downloading with {partCount} threads...");

                // Create download task for each part
                for (int i = 0; i < partCount; i++)
                {
                    var partIndex = i;
                    var start = partSize * i;
                    var end = (i == partCount - 1) ? totalSize - 1 : start + partSize - 1;
                    partFiles[i] = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid()}.tmp");

                    tasks.Add(DownloadPartAsync(zipUrl, partFiles[i], start, end, totalSize, progress));
                }

                await Task.WhenAll(tasks);

                // Parts are ready, now extract
                progress.Report("Combining downloaded parts...");
                await CombinePartsAsync(partFiles, tempZipPath);

                // Part files are cleaned up
                foreach (var partFile in partFiles)
                {
                    try { File.Delete(partFile); } catch { }
                }

                // ZIP file is ready, now extract
                progress.Report("Starting extraction...");
                await ExtractZipParallelAsync(tempZipPath, destinationFolder, progress);

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateProgressBar(0, 0, 0, 0, string.Empty);
                    HideDownloadLabels();
                    UpdateVersionLabel();
#if UpdateChangelog
                    ChangelogTab.IsEnabled = true;
#endif
                });
                progress.Report("Ready");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading and extracting zip file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Reset counters after download
                _downloadedSize = 0;
                _lastBytesRead = 0;
                await CleanupTempFileAsync(tempZipPath);
            }
        }

        private async Task DownloadPartAsync(string url, string partPath, long start, long end, long totalSize, IProgress<string> progress)
        {
            using var client = new HttpClient();
            // Optimize connection settings
            client.DefaultRequestHeaders.ConnectionClose = false; // Keep connection alive
            client.DefaultRequestHeaders.Add("Range", $"bytes={start}-{end}");
            client.Timeout = Timeout.InfiniteTimeSpan;
            
            // Increase buffer size for better throughput
            const int bufferSize = 262144; // 256KB buffer (increased from 81920)
            
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await response.Content.ReadAsStreamAsync();
            // Use FileOptions.WriteThrough for better write performance
            using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, 
                FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous);
            
            var buffer = new byte[bufferSize];
            long downloadedBytes = 0;
            
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloadedBytes += bytesRead;
                
                var now = DateTime.Now;
                var newTotal = Interlocked.Add(ref _downloadedSize, bytesRead);
                
                // Reduce UI updates to every 500ms instead of every second
                var timeDiff = (now - _lastSpeedUpdate).TotalSeconds;
                if (timeDiff >= 0.5)
                {
                    var bytesDiff = newTotal - _lastBytesRead;
                    var currentSpeed = bytesDiff / timeDiff;
                    var progressPercentage = (newTotal * 100.0) / totalSize;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        progressBar.Value = progressPercentage;
                        netSpeedLabel.Content = $"Speed: {currentSpeed / 1048576.0:F2} MB/s";
                        progressLabel.Content = $"Downloaded: {newTotal / 1048576.0:F2} MB / {totalSize / 1048576.0:F2} MB";
                    }, DispatcherPriority.Normal); // Normal priority for UI updates

                    _lastBytesRead = newTotal;
                    _lastSpeedUpdate = now;
                }
            }
        }

        private async Task CombinePartsAsync(string[] partFiles, string outputPath)
        {
            using var outputStream = new FileStream(outputPath, FileMode.Create);
            foreach (var partFile in partFiles)
            {
                using var inputStream = new FileStream(partFile, FileMode.Open);
                await inputStream.CopyToAsync(outputStream);
            }
        }

        private async Task ExtractZipParallelAsync(string zipPath, string destinationFolder, IProgress<string> progress)
        {
            try
            {
                progress.Report("Extracting mod package...");
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
                                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
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
                            Dispatcher.Invoke(() =>
                            {
                                progressBar.Value = extractProgress;
                                progressLabel.Content = $"Extracting: {currentEntry}/{totalEntries} files";
                            });
                        }
                    }
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateProgressBar(0, 0, 0, 0, string.Empty);
                    HideDownloadLabels();
                    UpdateVersionLabel();
#if UpdateChangelog
                    ChangelogTab.IsEnabled = true;
#endif
                });
                progress.Report("Ready");
            }
            catch (Exception ex)
            {
                LogMessage($"[CRITICAL] ZIP extraction failed: {ex.Message}");
                throw new Exception("Error occurred while extracting ZIP file.", ex);
            }
        }

        private async Task UpdateDownloadProgressAsync(double percentage, long downloaded, long total)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                progressBar.Value = percentage;
                progressLabel.Content = $"Downloaded: {downloaded / 1048576.0:F2} MB / {total / 1048576.0:F2} MB";
            });
        }

        private async Task CleanupTempFileAsync(string tempFile)
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
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"Unable to delete temporary file: {tempFile}\nError: {ex.Message}", 
                            "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            }
        }

        private void UpdateProgressBar(int progressPercentage, long totalRead, long totalBytes, double downloadSpeed, string status)
        {
            Dispatcher.Invoke(() =>
            {
                var now = DateTime.Now;
                var timeDiff = (now - _lastSpeedUpdate).TotalSeconds;
                
                if (timeDiff >= 1) // Update speed only once per second
                {
                    var bytesDiff = totalRead - _lastBytesRead;
                    var currentSpeed = bytesDiff / timeDiff; // Bytes per second

                    _lastBytesRead = totalRead;
                    _lastSpeedUpdate = now;

                    progressBar.Value = progressPercentage;
                    statusLabel.Content = status;
                    netSpeedLabel.Content = $"Speed: {currentSpeed / 1048576.0:F2} MB/s";
                    progressLabel.Content = $"Downloaded: {totalRead / 1048576.0:F2} MB / {totalBytes / 1048576.0:F2} MB";
                }
                else
                {
                    // Only update progress info, not speed
                    progressBar.Value = progressPercentage;
                    statusLabel.Content = status;
                    progressLabel.Content = $"Downloaded: {totalRead / 1048576.0:F2} MB / {totalBytes / 1048576.0:F2} MB";
                }
            });
        }

        private void UpdateDownloadLabel() => Dispatcher.Invoke(() =>
            progressLabel.Content = $"Downloaded: {_downloadedSize / 1048576.0:F2} MB / {_totalDownloadSize / 1048576.0:F2} MB");

        private void UpdateVersionLabel() => Dispatcher.Invoke(() => versionLabel.Content = _jsonVersion ?? "");

        private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
        {
            string execPath = Path.Combine(_bin32Folder, GAME_STARTER_FILE_NAME);
            if (File.Exists(execPath))
            {
                try
                {
                    Process.Start(execPath);
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error launching the game: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Game executable not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task UpdateServerList()
        {
            try
            {
                using var client = new HttpClient();
                string json = await client.GetStringAsync(SERVER_LIST_SOURCE_URL);
                var serverList = JsonConvert.DeserializeObject<List<Server>>(json);
                Dispatcher.Invoke(() =>
                {
                    Servers.Clear();
                    foreach (var server in serverList)
                    {
                        Servers.Add(server);
                    }
                });
            }
            catch { /* Ignore errors */ }
        }



     
       private async Task<bool> CheckDirectoryCasings(Dictionary<string, string> fileHashes, IProgress<string> progress)
        {
            try
            {
                var uniqueDirs = new HashSet<string>();
                
                // First collect all unique folder paths
                foreach (var fileHash in fileHashes)
                {
                    string relativePath = fileHash.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                    string filePath = Path.Combine(_gameFolder, relativePath);
                    string dirPath = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dirPath))
                    {
                        uniqueDirs.Add(dirPath);
                    }
                }

                var dirList = uniqueDirs.ToList();
                var maxThreads = Environment.ProcessorCount;
                var batchSize = Math.Max(1, dirList.Count / maxThreads);
                var tasks = new List<Task<bool>>();
                var semaphore = new SemaphoreSlim(maxThreads);

                //progress.Report($"Checking directory casings with {maxThreads} threads...");
                progress.Report($"Checking directory casings...");

                for (int i = 0; i < dirList.Count; i += batchSize)
                {
                    var batch = dirList.Skip(i).Take(batchSize).ToList();
                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            bool anyCorrections = false;
                            foreach (var dir in batch)
                            {
                                // Create server URL for each folder
                                string relativePath = dir.Substring(_gameFolder.Length)
                                                       .TrimStart('\\')
                                                       .Replace('\\', '/');
                                string serverUrl = $"{_serverBaseUrl}{SERVER_MOD_PATH}{relativePath}";

                                if (await FixPathCasingAsync(dir, serverUrl))
                                {
                                    anyCorrections = true;
                                }
                            }
                            return anyCorrections;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                var results = await Task.WhenAll(tasks);
                return results.Any(x => x);
            }
            catch (Exception ex)
            {
                LogMessage($"[ERROR] CheckDirectoryCasings error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> FixPathCasingAsync(string currentPath, string serverUrl)
        {
            try
            {
                await _pathSemaphore.WaitAsync();

                string[] pathParts = currentPath.Substring(_gameFolder.Length)
                              .Trim('\\')
                              .Split('\\');
                
                string currentBasePath = _gameFolder;
                bool anyCorrections = false;

                LogMessage($"[PATH] Checking path: {currentPath}");

                foreach (string part in pathParts)
                {
                    if (string.IsNullOrEmpty(part)) continue;

                    LogMessage($"[PATH] Processing part: {part}");

                    var parentDir = new DirectoryInfo(currentBasePath);
                    if (!parentDir.Exists)
                    {
                        // If parent folder doesn't exist, create it
                        Directory.CreateDirectory(parentDir.FullName);
                        LogMessage($"[PATH] Created missing parent directory: {parentDir.FullName}");
                    }

                    var subDir = parentDir.GetDirectories()
                        .FirstOrDefault(d => d.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

                    if (subDir == null)
                    {
                        // If subfolder doesn't exist, create it
                        Directory.CreateDirectory(Path.Combine(currentBasePath, part));
                        LogMessage($"[PATH] Created missing directory: {part}");
                        currentBasePath = Path.Combine(currentBasePath, part);
                        continue;
                    }

                    if (!subDir.Name.Equals(part, StringComparison.Ordinal))
                    {
                        try
                        {
                            string tempPath = Path.Combine(
                                Path.GetDirectoryName(subDir.FullName),
                                $"{Path.GetFileName(subDir.FullName)}_temp_{Guid.NewGuid()}"
                            );
                            string targetPath = Path.Combine(currentBasePath, part);

                            // If target folder already exists
                            if (Directory.Exists(targetPath) && !targetPath.Equals(subDir.FullName, StringComparison.OrdinalIgnoreCase))
                            {
                                string backupPath = $"{targetPath}_backup_{Guid.NewGuid()}";
                                Directory.Move(targetPath, backupPath);
                                LogMessage($"[PATH] Backed up existing directory to: {backupPath}");
                            }

                            // If temp folder exists
                            if (Directory.Exists(tempPath))
                            {
                                Directory.Delete(tempPath, true);
                                LogMessage($"[PATH] Removed existing temp directory: {tempPath}");
                            }

                            LogMessage($"[PATH] Moving directory to temp path: {tempPath}");
                            Directory.Move(subDir.FullName, tempPath);
                            
                            // Ensure target folder's parent exists
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                            
                            Directory.Move(tempPath, targetPath);
                            LogMessage($"[PATH] Successfully fixed directory casing: {subDir.FullName} -> {targetPath}");
                            anyCorrections = true;
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"[ERROR] Failed to fix directory casing for {subDir.FullName}: {ex.Message}");
                            // Continue in case of error, try to fix other folders
                        }
                    }
                    else
                    {
                        LogMessage($"[PATH] Directory casing is correct: {subDir.FullName}");
                    }

                    currentBasePath = Path.Combine(currentBasePath, subDir.Name);
                }

                LogMessage($"[PATH] Finished checking path: {currentPath}. Corrections made: {anyCorrections}");
                return anyCorrections;
            }
            catch (Exception ex)
            {
                LogMessage($"[ERROR] FixPathCasing error for {currentPath}: {ex.Message}");
                return false;
            }
            finally
            {
                _pathSemaphore.Release();
            }
        }



     
      private void LogMessage(string message)
        {
            #if !ENABLE_LOGGING
            return;
            #endif
            
            try
            {
                string logPath = Path.Combine(_gameFolder, "launcher.log");
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logMessage = $"[{timestamp}] {message}";

                // Write to log file (append mode)
                File.AppendAllText(logPath, logMessage + Environment.NewLine);

        #if DEBUG
                Debug.WriteLine(logMessage);
        #endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logging failed: {ex.Message}");
            }
        }
  
  
  
  
  
  
  
  
  
   
    }


}
