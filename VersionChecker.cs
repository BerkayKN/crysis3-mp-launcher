#define ENABLE_LOGGING
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace Crysis3_MP_Launcher
{
    public class VersionChecker
    {
        private static string GetServerBaseUrl()
        {
            if (Application.Current?.MainWindow is MainWindow mw && !string.IsNullOrWhiteSpace(mw.ServerBaseUrl))
                return mw.ServerBaseUrl;
            return MainWindow.DEFAULT_SERVER_URL;
        }

        private static string GetVersionCheckUrl() => GetServerBaseUrl() + "/C3MP/Launcher/index.php";
        private static string GetUpdateSiteUrl() => GetServerBaseUrl() + "/C3MP/Launcher/Crysis%203%20Multiplayer%20Launcher.exe";

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

                if (!updateRequired && updateAvailable)
                {
                    string skippedVersion = GetSkippedVersion();
                    if (skippedVersion == versionInfo.LatestVersion)
                    {
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
                        Application.Current.Shutdown();
                    }
                    else if (updateRequired)
                    {
                        Application.Current.Shutdown();
                    }
                    else
                    {
                        SaveSkippedVersion(versionInfo.LatestVersion);
                    }
                }
            }
            catch (Exception ex)
            {
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
                    string response = await client.GetStringAsync(GetVersionCheckUrl());
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
                return string.Compare(version1, version2, StringComparison.Ordinal);
            }
        }

        private static void OpenUpdateSite()
        {
            string updateUrl = GetUpdateSiteUrl();
            try
            {
                Process.Start(updateUrl);
            }
            catch
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = updateUrl,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open update page: {ex.Message}\nPlease visit {updateUrl} manually.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static void LogVersionCheckError(Exception ex)
        {
            Logger.LogError("Version check error", ex);
        }

        private class VersionInfo
        {
            public string LatestVersion { get; set; }
            public string RequiredVersion { get; set; }
            public string UpdateInfo { get; set; }
        }
    }
} 