using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Crysis3_MP_Launcher
{
    public class LauncherDataManager
    {
        private const string DATA_FILE = "launcher.dat";
        private static readonly object _dataLock = new object();
        private static Dictionary<string, string> _dataCache;

        public static string GetValue(string key)
        {
            lock (_dataLock)
            {
                if (_dataCache == null)
                {
                    LoadData();
                }

                if (_dataCache.TryGetValue(key, out string value))
                {
                    return value;
                }

                return null;
            }
        }

        public static void SetValue(string key, string value)
        {
            lock (_dataLock)
            {
                if (_dataCache == null)
                {
                    LoadData();
                }

                _dataCache[key] = value;
                SaveData();
            }
        }

        private static void LoadData()
        {
            _dataCache = new Dictionary<string, string>();
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DATA_FILE);

            if (!File.Exists(filePath))
            {
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(filePath);
                foreach (string line in lines)
                {
                    string[] parts = line.Split(new[] { ':' }, 2);
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        _dataCache[key] = value;
                    }
                }
            }
            catch
            {
                // Silently handle any read errors
            }
        }

        private static void SaveData()
        {
            try
            {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DATA_FILE);
                var lines = _dataCache.Select(kvp => $"{kvp.Key}:{kvp.Value}");
                File.WriteAllLines(filePath, lines);
            }
            catch
            {
                // Silently handle any write errors
            }
        }
    }
} 