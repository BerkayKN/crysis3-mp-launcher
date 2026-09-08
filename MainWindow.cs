//#define UpdateChangelog
#define CleanFilesNotInMd5List
//#define EnableServerList

using System;
using System.Collections.Concurrent;
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
using System.Windows.Media.Imaging;

namespace Crysis3_MP_Launcher
{
    public partial class MainWindow : Window, IComponentConnector
    {
        //Start of Defines
        private const string MD5_FILE_PATH = "/C3MP/ModFiles/md5sum.php";
        private const string WEBVIEW_DLL_PATH = "/openspymod/WebView2Loader.dll";
        public const string DEFAULT_SERVER_URL = "http://lb.crysis2.privatedns.org";
        private const string GAME_MOD_FOLDER = "C3MP";
        private const string SERVER_MOD_PATH = "/C3MP/ModFiles/";
        private const string SERVER_LIST_SOURCE_URL = "https://openspy-website.nyc3.digitaloceanspaces.com/servers/capricorn.json";
        private const string GAME_STARTER_FILE_NAME = "Crysis 3 - Mod C3MP.bat";
        private const string GET_SERVER_URL = "https://raw.githubusercontent.com/BerkayKN/crysis3-mp-launcher/main/server/server.txt";
        //End of Defines

        private string _serverBaseUrl;
        public string ServerBaseUrl => _serverBaseUrl;
        private readonly string _bin32Folder;
        private readonly string _gameFolder;
        private string _md5Url;
        private string _WebViewDLLUrl;
        private readonly string _webView2LoaderPath;
        private long _totalDownloadSize;
        private bool _isAdministrator;
        private string _jsonVersion;
#if EnableServerList
        private readonly DispatcherTimer _serverTimer;
#endif
        //internal WebView2 webView;
        public ObservableCollection<Serverlist> Servers { get; }

        private List<string> _failedDownloads = new List<string>();
        public string Version
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return $"v{version}";
            }
        }

        public string BackgroundImageUrl => ($"{_serverBaseUrl}/C3MP/background.png");

        public MainWindow()
        {
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            Logger.Initialize();

            InitializeComponent();
            DataContext = this;
            _bin32Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin32");
            _gameFolder = AppDomain.CurrentDomain.BaseDirectory;
            _webView2LoaderPath = Path.Combine(_gameFolder, "WebView2Loader.dll");
            _isAdministrator = IsAdministrator();
            Logger.IsEnabled = true;
            LogMessage($"[INIT] Mod folder: {_gameFolder}");
            LogMessage($"[INIT] Bin32 folder: {_bin32Folder}");
            LogMessage($"[INIT] Administrator: {_isAdministrator}");

            UpdateWindowTitle();
            Servers = new ObservableCollection<Serverlist>();
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
            var serverListTab = this.FindName("ServerlistTab") as TabItem;
            if (serverListTab != null)
            {
                serverListTab.Visibility = Visibility.Collapsed;
                serverListTab.IsEnabled = false;
            }
            #endif
            
#if !UpdateChangelog
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

            await InitializeAsync();

            await VersionChecker.CheckForUpdates();

            await UpdateStatusLabelAsync("Checking mod files...");
			_ = SetBackgroundImageAsync();
            var progress = new Progress<string>(message => statusLabel.Content = message);
            await Task.Run(() => CheckAndUpdateModFiles(progress));
            launchGameButton.IsEnabled = true;
        }

        private async Task CheckAndUpdateModFiles(IProgress<string> progress)
        {
            try
            {
                _failedDownloads.Clear();

                var downloadProgress = new Progress<DownloadProgressReport>(report =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            progressBar.Value = report.ProgressPercentage;
                            if (report.TotalBytes > 0)
                            {
                                progressLabel.Content = $"Downloaded: {report.DownloadedBytes / 1048576.0:F2} MB / {report.TotalBytes / 1048576.0:F2} MB";
                            }
                            else if (report.TotalFiles > 0)
                            {
                                progressLabel.Content = $"Processing: {report.DownloadedFiles} / {report.TotalFiles}";
                            }

                            if (report.SpeedBytesPerSecond > 0)
                            {
                                netSpeedLabel.Content = $"Speed: {report.SpeedBytesPerSecond / 1048576.0:F2} MB/s";
                            }

                            if (!string.IsNullOrEmpty(report.StatusMessage))
                            {
                                statusLabel.Content = report.StatusMessage;
                            }
                        }
                        catch { /* Ignore UI update errors */ }
                    });
                });

                using (var downloadManager = new DownloadManager(downloadProgress, LogMessage, _serverBaseUrl))
                {
                    progress.Report("Downloading file list...");
                    string md5Data = await downloadManager.DownloadStringAsync(_md5Url, TimeSpan.FromMinutes(2));
                    progress.Report("Parsing file list data...");
                    var fileHashes = ParseMd5Data(md5Data);
                        
                        if (fileHashes == null || fileHashes.Count == 0)
                        {
                            progress.Report("Failed to load file list data. Aborting.");
                            return;
                        }
                       
                        int totalFiles = fileHashes.Count;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            progressBar.Value = 0;
                            progressLabel.Content = $"Checking files: 0 / {totalFiles}";
                        });

                        progress.Report($"Checking file hashes (0/{totalFiles})...");

                        var filesToDownload = new ConcurrentBag<KeyValuePair<string, string>>();
                        long existingFilesSize = 0;
                        int hashesChecked = 0;
                        DateTime lastUiUpdate = DateTime.MinValue;
                        object uiLock = new object();

                        var parallelOptions = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Environment.ProcessorCount
                        };

                        await Task.Run(() =>
                        {
                            Parallel.ForEach(fileHashes, parallelOptions, fileHash =>
                            {
                                string path = fileHash.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                                string localPath = Path.Combine(_gameFolder, path);

                                bool isValid = false;
                                try
                                {
                                    if (File.Exists(localPath))
                                    {
                                        string computedHash = ComputeMD5(localPath);
                                        if (string.Equals(computedHash, fileHash.Value, StringComparison.OrdinalIgnoreCase))
                                        {
                                            isValid = true;
                                            long fileSize = new FileInfo(localPath).Length;
                                            Interlocked.Add(ref existingFilesSize, fileSize);
                                        }
                                    }
                                }
                                catch { }

                                if (!isValid)
                                {
                                    filesToDownload.Add(fileHash);
                                }

                                int currentChecked = Interlocked.Increment(ref hashesChecked);

                                bool shouldUpdateUi = false;
                                lock (uiLock)
                                {
                                    if ((DateTime.Now - lastUiUpdate).TotalMilliseconds >= 100 || currentChecked == totalFiles)
                                    {
                                        lastUiUpdate = DateTime.Now;
                                        shouldUpdateUi = true;
                                    }
                                }

                                if (shouldUpdateUi)
                                {
                                    double percentage = (currentChecked * 100.0) / totalFiles;
                                    progress.Report($"Checking file hashes...");
                                    Dispatcher.InvokeAsync(() =>
                                    {
                                        progressBar.Value = percentage;
                                        progressLabel.Content = $"{currentChecked} / {totalFiles}";
                                    });
                                }
                            });
                        });

                        _totalDownloadSize = Math.Max(0, _totalDownloadSize - existingFilesSize);

                        var filesToDownloadList = filesToDownload.ToList();
                        int totalFilesToDownload = filesToDownloadList.Count;

                        if (totalFilesToDownload == 0)
                        {
                            LogMessage("[UPDATE] All files are up to date. No files to download.");
#if CleanFilesNotInMd5List
                            progress.Report("Cleaning up old files...");
                            await DeleteFilesNotInMd5List(fileHashes, progress);
#endif
                            await Dispatcher.InvokeAsync(() =>
                            {
                                HideDownloadLabels();
                                UpdateVersionLabel();
#if UpdateChangelog
                                ChangelogTab.IsEnabled = true;
#endif
                            });
                            progress.Report("Ready to play");

#if UpdateChangelog
                            if (!File.Exists(_webView2LoaderPath))
                            {
                                try
                                {
                                    byte[] dllBytes = await downloadManager.DownloadByteArrayAsync(_WebViewDLLUrl);
                                    File.WriteAllBytes(_webView2LoaderPath, dllBytes);
                                }
                                catch { /* Ignore errors */ }
                            }
#endif
                            return;
                        }

                        int remainingFiles = totalFilesToDownload;

                        downloadManager.ResetCounters(_totalDownloadSize, totalFilesToDownload);

                        int processorCount = Environment.ProcessorCount;
                        int concurrentDownloads = Math.Min(8, Math.Max(2, processorCount));
                        List<Task> downloadTasks = new List<Task>();
                        using (var semaphore = new SemaphoreSlim(concurrentDownloads))
                        {
                            progress.Report($"Downloading files ( 0/{totalFilesToDownload} )...");
                            LogMessage($"Using {concurrentDownloads} concurrent downloads for {totalFilesToDownload} files...");

                            foreach (var file in filesToDownloadList)
                            {
                                string url = file.Key;
                                string path = url.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                                string filePath = Path.Combine(_gameFolder, path);
                                
                                await semaphore.WaitAsync();
                                
                                downloadTasks.Add(Task.Run(async () => {
                                    try
                                    {
                                        await downloadManager.DownloadFileWithRetryAsync(url, filePath, 5, progress);
                                        var remaining = Interlocked.Decrement(ref remainingFiles);
                                        progress.Report($"Downloading files ( {totalFilesToDownload - remaining}/{totalFilesToDownload} )...");
                                    }
                                    finally
                                    {
                                        semaphore.Release();
                                    }
                                }));
                            }

                            await Task.WhenAll(downloadTasks);
                        }

                        _failedDownloads = new List<string>(downloadManager.FailedDownloads);

                        #if CleanFilesNotInMd5List
                        progress.Report("Cleaning up old files...");
                        await DeleteFilesNotInMd5List(fileHashes, progress);
                        #endif

                        await Dispatcher.InvokeAsync(() =>
                        {
                            HideDownloadLabels();
                            UpdateVersionLabel();
                        #if UpdateChangelog
                            ChangelogTab.IsEnabled = true;
                        #endif
                        });
                        
                        progress.Report("Ready to play");

                    #if UpdateChangelog
                    // WebView2 DLL kontrolü
                    if (!File.Exists(_webView2LoaderPath))
                    {
                        try
                        {
                            byte[] dllBytes = await downloadManager.DownloadByteArrayAsync(_WebViewDLLUrl);
                            File.WriteAllBytes(_webView2LoaderPath, dllBytes);
                        }
                        catch { /* Ignore errors */ }
                    }
                    #endif
                }
            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"HTTP request error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
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
		

        private string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }

        private async Task DeleteFilesNotInMd5List(Dictionary<string, string> fileHashes, IProgress<string> progress)
        {
            await UpdateStatusLabelAsync("Cleaning up old files...");

            string modFolder = Path.Combine(_gameFolder, "Mods", GAME_MOD_FOLDER);
            if (!Directory.Exists(modFolder)) return;

            var validFiles = new HashSet<string>(
                fileHashes.Select(fh =>
                    NormalizePath(Path.Combine(_gameFolder, fh.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', Path.DirectorySeparatorChar)))
                ),
                StringComparer.OrdinalIgnoreCase
            );

            await Task.Run(() =>
            {
                var allFiles = Directory.GetFiles(modFolder, "*", SearchOption.AllDirectories);
                foreach (var filePath in allFiles)
                {
                    if (!validFiles.Contains(NormalizePath(filePath)))
                    {
                        try { File.Delete(filePath); } catch { }
                    }
                }

                var allDirs = Directory.GetDirectories(modFolder, "*", SearchOption.AllDirectories)
                    .OrderByDescending(x => x.Length);
                foreach (var dirPath in allDirs)
                {
                    string relativePath = dirPath.Replace(modFolder, "")
                        .TrimStart(Path.DirectorySeparatorChar)
                        .Replace('\\', '/');

                    string url = Uri.UnescapeDataString($"{_serverBaseUrl}{SERVER_MOD_PATH}Mods/{GAME_MOD_FOLDER}/{Uri.EscapeDataString(relativePath)}");

                    bool hasMatchingFiles = fileHashes.Keys.Any(key => key.StartsWith(url, StringComparison.OrdinalIgnoreCase));
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
                            LogMessage($"[ERROR] Error deleting directory {dirPath}: {ex.Message}");
                            Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show($"Error deleting directory {dirPath}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                        }
                    }
                }
            });
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
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"[ERROR] Error computing MD5 for {filePath}: {ex.Message}");
                return string.Empty;
            }
        }

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
                var serverList = JsonConvert.DeserializeObject<List<Serverlist>>(json);
                Dispatcher.Invoke(() =>
                {
                    Servers.Clear();
                    foreach (var server in serverList)
                    {
                        Servers.Add(server);
                    }
                });
            }
           catch (Exception ex)
            {
                LogMessage($"[ERROR] Error updating server list: {ex.Message}");
            }
        }

        public static void LogMessage(string message) => Logger.LogMessage(message);
		private async Task SetBackgroundImageAsync()
        {
            string embeddedPath = "pack://application:,,,/resources/background.jpg";

            if (!string.IsNullOrWhiteSpace(_serverBaseUrl))
            {
                string remoteUrl = $"{_serverBaseUrl}/openspymod/background.png";
                try
                {
                    using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, remoteUrl)).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                BackgroundImage.Source = new BitmapImage(new Uri(remoteUrl));
                            });
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"[ERROR] Background image check failed: {ex.Message}");
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    BackgroundImage.Source = new BitmapImage(new Uri(embeddedPath));
                }
                catch {  }
            });
        }
    }


}
