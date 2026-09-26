using System;
using System.Collections.Concurrent;
using System.IO;

namespace Crysis3_MP_Launcher
{
    public class HashCacheEntry
    {
        public long LastWriteTimeUtcTicks { get; set; }
        public long FileSize { get; set; }
        public string Hash { get; set; }
    }

    public static class HashCacheManager
    {
        private const string CACHE_FILE_NAME = "launcher_cache.dat";
        private static readonly ConcurrentDictionary<string, HashCacheEntry> _cache = 
            new ConcurrentDictionary<string, HashCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _fileLock = new object();
        private static string _baseDirectory;
        private static string _cacheFilePath;
        private static bool _isLoaded;
        private static bool _isDirty;

        public static void Initialize(string baseDirectory)
        {
            _baseDirectory = baseDirectory;
            _cacheFilePath = Path.Combine(baseDirectory, CACHE_FILE_NAME);
            Load();
        }

        private static string GetRelativePath(string fullPath)
        {
            if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(fullPath))
                return fullPath;

            if (fullPath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(_baseDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            }

            return fullPath.Replace('\\', '/');
        }

        public static void Load()
        {
            lock (_fileLock)
            {
                if (_isLoaded) return;
                _cache.Clear();

                if (!File.Exists(_cacheFilePath))
                {
                    _isLoaded = true;
                    return;
                }

                try
                {
                    string[] lines = File.ReadAllLines(_cacheFilePath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('|');
                        if (parts.Length == 4 &&
                            long.TryParse(parts[1], out long ticks) &&
                            long.TryParse(parts[2], out long size))
                        {
                            string relPath = parts[0].Trim();
                            string hash = parts[3].Trim();
                            _cache[relPath] = new HashCacheEntry
                            {
                                LastWriteTimeUtcTicks = ticks,
                                FileSize = size,
                                Hash = hash
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogMessage($"[WARNING] Failed to load hash cache: {ex.Message}");
                }
                finally
                {
                    _isLoaded = true;
                    _isDirty = false;
                }
            }
        }

        public static bool TryGetHash(string fullPath, long lastWriteTimeUtcTicks, long fileSize, out string cachedHash)
        {
            cachedHash = null;
            string relPath = GetRelativePath(fullPath);

            if (_cache.TryGetValue(relPath, out var entry))
            {
                if (entry.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks && entry.FileSize == fileSize)
                {
                    cachedHash = entry.Hash;
                    return true;
                }
            }

            return false;
        }

        public static void SetHash(string fullPath, long lastWriteTimeUtcTicks, long fileSize, string hash)
        {
            string relPath = GetRelativePath(fullPath);
            _cache[relPath] = new HashCacheEntry
            {
                LastWriteTimeUtcTicks = lastWriteTimeUtcTicks,
                FileSize = fileSize,
                Hash = hash
            };
            _isDirty = true;
        }

        public static void Remove(string fullPath)
        {
            string relPath = GetRelativePath(fullPath);
            if (_cache.TryRemove(relPath, out _))
            {
                _isDirty = true;
            }
        }

        public static void Save()
        {
            if (!_isDirty || string.IsNullOrEmpty(_cacheFilePath)) return;

            lock (_fileLock)
            {
                try
                {
                    string tempPath = _cacheFilePath + ".tmp";
                    using (var writer = new StreamWriter(tempPath, false))
                    {
                        foreach (var kvp in _cache)
                        {
                            writer.WriteLine($"{kvp.Key}|{kvp.Value.LastWriteTimeUtcTicks}|{kvp.Value.FileSize}|{kvp.Value.Hash}");
                        }
                    }

                    if (File.Exists(_cacheFilePath))
                    {
                        File.Delete(_cacheFilePath);
                    }
                    File.Move(tempPath, _cacheFilePath);
                    _isDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.LogMessage($"[WARNING] Failed to save hash cache: {ex.Message}");
                }
            }
        }
    }
}
