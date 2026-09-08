//#define ENABLE_LOGGING

using System;
using System.Diagnostics;
using System.IO;

namespace Crysis3_MP_Launcher
{
    public static class Logger
    {
        private static readonly object _fileLock = new object();
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");

#if ENABLE_LOGGING
        public static bool IsEnabled { get; set; } = true;
#else
        public static bool IsEnabled
        {
            get => false;
            set { }
        }
#endif

        public static void Initialize()
        {
            if (!IsEnabled) return;

            try
            {
                lock (_fileLock)
                {
                    File.WriteAllText(_logPath, $"=== Launcher Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                }
                LogMessage("[INIT] Launcher initialized");
            }
            catch
            {
                // Ignore log initialization errors
            }
        }

        public static void LogMessage(string message)
        {
            if (!IsEnabled || string.IsNullOrEmpty(message)) return;

            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string formattedMessage = $"[{timestamp}] {message}";

                lock (_fileLock)
                {
                    File.AppendAllText(_logPath, formattedMessage + Environment.NewLine);
                }

#if DEBUG
                Debug.WriteLine(formattedMessage);
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logging failed: {ex.Message}");
            }
        }

        public static void Log(string message) => LogMessage(message);

        public static void LogError(string message, Exception ex = null)
        {
            if (ex != null)
            {
                LogMessage($"[ERROR] {message}: {ex.Message}\r\n{ex.StackTrace}");
            }
            else
            {
                LogMessage($"[ERROR] {message}");
            }
        }
    }
}
