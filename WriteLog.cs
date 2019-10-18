namespace PSParallel
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Management.Automation;

    /// <summary>
    /// Static Class for logging Powershell Host and logfile
    /// </summary>
    internal static class Logger
    {
        /// <summary>
        /// Log to File and Verbose outputs
        /// </summary>
        private static LogTarget[] fileVerboseLogTypes = { LogTarget.File, LogTarget.HostVerbose };

        /// <summary>
        /// Log to File and Host Warning outputs
        /// </summary>
        private static LogTarget[] fileWarningLogTypes = { LogTarget.File, LogTarget.HostWarning };

        /// <summary>
        /// Log to File and Host Error outputs
        /// </summary>
        private static LogTarget[] fileErrorLogTypes = { LogTarget.File, LogTarget.HostError };

        /// <summary>
        /// Log as Debug strings on host
        /// </summary>
        private static LogTarget[] debugLogTypes = { LogTarget.HostDebug };

        /// <summary>
        /// Possible log targets
        /// </summary>
        internal enum LogTarget
        {
            None,
            File,
            HostVerbose,
            HostWarning,
            HostError,
            HostDebug,
            All
        }

        /// <summary>
        /// Log to File and Verbose outputs
        /// </summary>
        internal static LogTarget[] FileVerboseLogTypes { get => fileVerboseLogTypes; set => fileVerboseLogTypes = value; }

        /// <summary>
        /// Log to File and Host Warning outputs
        /// </summary>
        internal static LogTarget[] FileWarningLogTypes { get => fileWarningLogTypes; set => fileWarningLogTypes = value; }

        /// <summary>
        /// Log to File and Host Error outputs
        /// </summary>
        internal static LogTarget[] FileErrorLogTypes { get => fileErrorLogTypes; set => fileErrorLogTypes = value; }

        /// <summary>
        /// Log as Debug strings on host
        /// </summary>
        internal static LogTarget[] DebugLogTypes { get => debugLogTypes; set => debugLogTypes = value; }

        /// <summary>
        /// LogHelper class
        /// </summary>
        internal static class LogHelper
        {
            /// <summary>
            /// Standard format to be used for datetimes in logging
            /// </summary>
            private static readonly string datetimeFormat = "yyyyMMddTHHmmss";

            /// <summary>
            /// Logging file on to the Temp directory. Hardcoded to use Iv in filename
            /// </summary>
            private static string logFile = Path.Combine(Path.GetTempPath(), "Iv" + System.DateTime.UtcNow.ToString(datetimeFormat) + ".log");

            /// <summary>
            /// Default string to show in the Host progress
            /// </summary>
            private const string ProgressStr = "Executing Jobs";

            /// <summary>
            /// Logging method
            /// </summary>
            /// <typeparam name="T">Host or file type</typeparam>
            /// <param name="message">String to Log</param>
            /// <param name="logTarget">LogTargets to log</param>
            /// <param name="invokeAll">PSCmdlet host</param>
            /// <param name="noFileLogging">If true, no file logging is done</param>
            public static void Log<T>(T message, LogTarget logTarget, PSCmdlet invokeAll, bool noFileLogging = false)
            {
                string messageStr = null;
                if (message is ErrorRecord)
                {
                    // cast to object and then to ErrorRecord as direct casting is not allowed
                    messageStr = ((ErrorRecord)(object)message).Exception.Message;
                }
                else
                {
                    messageStr = (string)(object)message;
                }

                string logEntry = $"[{System.DateTime.UtcNow.ToString(datetimeFormat)}] {messageStr}";

                switch (logTarget)
                {
                    case LogTarget.File:
                        if (!noFileLogging)
                        {
                            using (StreamWriter streamWriter = new StreamWriter(logFile, append: true))
                            {
                                streamWriter.WriteLine(logEntry);
                                streamWriter.Close();
                            }
                        }

                        break;
                    case LogTarget.HostVerbose:
                        invokeAll.WriteVerbose(logEntry);
                        break;
                    case LogTarget.HostDebug:
                        invokeAll.WriteDebug(logEntry);
                        break;
                    case LogTarget.HostError:
                        if (message is ErrorRecord)
                        {
                            invokeAll.WriteError((ErrorRecord)(object)message);
                        }

                        break;
                    case LogTarget.HostWarning:
                        invokeAll.WriteWarning(logEntry);
                        break;
                    default:
                        break;
                }
            }

            /// <summary>
            /// Call Log method for each log target
            /// </summary>
            /// <typeparam name="T">Types allowed, string or ErrorRecord</typeparam>
            /// <param name="target">LogTarget to enter</param>
            /// <param name="message">String to Log</param>
            /// <param name="invokeAllInstance">PSCmdlet host</param>
            /// <param name="noFileLogging">Should log to file or Not</param>
            internal static void Log<T>(LogTarget[] target, T message, PSCmdlet invokeAllInstance, bool noFileLogging = false)
            {
                foreach (LogTarget lT in target)
                {
                    Log(message, lT, invokeAllInstance, noFileLogging);
                }
            }

            /// <summary>
            /// Call LogDebug method for each string
            /// </summary>
            /// <param name="debugStrs">Strings to write</param>
            /// <param name="invokeAllinstance">PSCmdlet host</param>
            internal static void LogDebug(List<string> debugStrs, PSCmdlet invokeAllinstance)
            {
                foreach (string debugEntry in debugStrs)
                {
                    Log(debugEntry, LogTarget.HostDebug, invokeAllinstance);
                }
            }

            /// <summary>
            /// Method to write debug entries to host
            /// </summary>
            /// <param name="debugStr">Strings to write</param>
            /// <param name="invokeAllinstance">PSCmdlet host</param>
            internal static void LogDebug(string debugStr, PSCmdlet invokeAllinstance)
            {
                LogDebug(new List<string>() { debugStr }, invokeAllinstance);
            }

            /// <summary>
            /// WriteProgress to the host
            /// </summary>
            /// <param name="currentOperation">Current operation name</param>
            /// <param name="invokeAll">PSCmdlet Host</param>
            /// <param name="statusStr">Current Status</param>
            /// <param name="percentComplete">Percent complete</param>
            /// <param name="quiet">When specified, just return</param>
            internal static void LogProgress(string currentOperation, PSCmdlet invokeAll, string statusStr = ProgressStr, int percentComplete = -1, bool quiet = false)
            {
                if (quiet)
                {
                    return;
                }

                ProgressRecord progressRecord = new ProgressRecord(20, statusStr, currentOperation)
                {
                    PercentComplete = percentComplete
                };

                if (statusStr.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    progressRecord.RecordType = ProgressRecordType.Completed;
                }

                invokeAll.WriteProgress(progressRecord);
            }
        }
    }
}