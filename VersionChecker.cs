using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace C2COMMUNITY_Mod_Launcher
{
    public class VersionChecker
    {
        private const string VERSION_CHECK_URL = MainWindow.DEFAULT_SERVER_URL + "/C3MP/Launcher/index.php";
        private const string UPDATE_SITE_URL = "http://lb.crysis2.epicgamer.org/C3MP/Launcher/Crysis%203%20Multiplayer%20Launcher.exe";

        public static async Task CheckForUpdates()
        {
            try
            {
                var versionInfo = await GetVersionInfoAsync();
                if (versionInfo == null)
                    return;

                string currentVersion = GetCurrentVersion();
                bool updateAvailable = IsUpdateAvailable(currentVersion, versionInfo.LatestVersion);
                bool updateRequired = IsUpdateRequired(currentVersion, versionInfo.RequiredVersion);

                // Check if this update was already skipped
                if (!updateRequired && updateAvailable)
                {
                    string skippedVersion = GetSkippedVersion();
                    if (skippedVersion == versionInfo.LatestVersion)
                    {
                        // User already skipped this version, don't prompt again
                        return;
                    }
                }

                if (updateAvailable)
                {
                    string updateInfoSection = string.IsNullOrWhiteSpace(versionInfo.UpdateInfo)
                        ? ""
                        : $"\n\nUpdate Information:\n{versionInfo.UpdateInfo}";

                    string message = updateRequired
                        ? $"This version of the launcher ({currentVersion}) is outdated and no longer supported.\nLatest version: {versionInfo.LatestVersion}{updateInfoSection}\n\nDo you want to update now?"
                        : $"A new version of the launcher is available: {versionInfo.LatestVersion}\nYour current version: {currentVersion}\n\nUpdate Information:\n{versionInfo.UpdateInfo}\n\nDo you want to update?";

                    var result = MessageBox.Show(
                        message,
                        "Launcher Update Available",
                        MessageBoxButton.YesNo,
                        updateRequired ? MessageBoxImage.Warning : MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        OpenUpdateSite();
                        // Update page opened, close launcher
                        Application.Current.Shutdown();
                    }
                    else if (updateRequired)
                    {
                        // Force close if update is required but user declined
                        Application.Current.Shutdown();
                    }
                    else
                    {
                        // User declined a non-required update, remember this choice
                        SaveSkippedVersion(versionInfo.LatestVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log exception but don't show to user - silently continue if version check fails
                LogVersionCheckError(ex);
            }
        }

        private static string GetSkippedVersion()
        {
            return LauncherDataManager.GetValue("skippedupdate");
        }

        private static void SaveSkippedVersion(string version)
        {
            LauncherDataManager.SetValue("skippedupdate", version);
        }

        private static async Task<VersionInfo> GetVersionInfoAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    string response = await client.GetStringAsync(VERSION_CHECK_URL);
                    return ParseVersionInfo(response);
                }
            }
            catch (Exception ex)
            {
                LogVersionCheckError(ex);
                return null;
            }
        }

        private static VersionInfo ParseVersionInfo(string response)
        {
            try
            {
                var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                    return null;

                string latestVersion = lines[0].Replace("LatestVer:", "").Trim();
                string requiredVersion = lines[1].Replace("ReqVer:", "").Trim();
                string updateInfo = "";

                // Find and collect UpdateInfo section
                bool foundUpdateInfo = false;
                for (int i = 2; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("UpdateInfo:"))
                    {
                        foundUpdateInfo = true;
                        continue;
                    }
                    if (foundUpdateInfo)
                    {
                        updateInfo += lines[i] + "\n";
                    }
                }

                return new VersionInfo
                {
                    LatestVersion = latestVersion,
                    RequiredVersion = requiredVersion,
                    UpdateInfo = updateInfo.Replace("<br>", "\n").Replace("<br />", "\n").TrimEnd()
                };
            }
            catch
            {
                return null;
            }
        }

        private static string GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
                return fileVersionInfo.FileVersion ?? "1.0.0.0";
            }
            catch
            {
                return "1.0.0.0";
            }
        }

        private static bool IsUpdateAvailable(string currentVersion, string latestVersion)
        {
            if (string.IsNullOrEmpty(latestVersion))
                return false;

            return CompareVersions(currentVersion, latestVersion) < 0;
        }

        private static bool IsUpdateRequired(string currentVersion, string requiredVersion)
        {
            if (string.IsNullOrEmpty(requiredVersion))
                return false;

            return CompareVersions(currentVersion, requiredVersion) < 0;
        }

        private static int CompareVersions(string version1, string version2)
        {
            try
            {
                Version v1 = new Version(version1);
                Version v2 = new Version(version2);
                return v1.CompareTo(v2);
            }
            catch
            {
                // Fall back to string comparison if version parsing fails
                return string.Compare(version1, version2, StringComparison.Ordinal);
            }
        }

        private static void OpenUpdateSite()
        {
            try
            {
                Process.Start(UPDATE_SITE_URL);
            }
            catch
            {
                // On newer .NET versions, Process.Start might not work directly with URLs
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = UPDATE_SITE_URL,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open update page: {ex.Message}\nPlease visit {UPDATE_SITE_URL} manually.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static void LogVersionCheckError(Exception ex)
        {
            #if ENABLE_LOGGING
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version_check.log");
                string logMessage = $"[{DateTime.Now}] Version check error: {ex.Message}\r\n{ex.StackTrace}\r\n";
                File.AppendAllText(logPath, logMessage);
            }
            catch
            {
                // Ignore logging errors
            }
            #endif
        }

        private class VersionInfo
        {
            public string LatestVersion { get; set; }
            public string RequiredVersion { get; set; }
            public string UpdateInfo { get; set; }
        }
    }
} 