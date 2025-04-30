using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace C2COMMUNITY_Mod_Launcher
{
    public class LauncherDataManager
    {
        private const string DATA_FILE = "launcher.dat";
        private static Dictionary<string, string> _dataCache;

        public static string GetValue(string key)
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

        public static void SetValue(string key, string value)
        {
            if (_dataCache == null)
            {
                LoadData();
            }

            _dataCache[key] = value;
            SaveData();
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