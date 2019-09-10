namespace PSParallel
{
    using System.Management.Automation;
    using System.IO;
    using System;
    using System.Collections.Generic;

    internal static class Logger
    {
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
       
        internal static LogTarget[] fileVerboseLogTypes = { LogTarget.File, LogTarget.HostVerbose};
        internal static LogTarget[] fileWarningLogTypes = { LogTarget.File, LogTarget.HostWarning };
        internal static LogTarget[] fileErrorLogTypes =  { LogTarget.File, LogTarget.HostError };
        internal static LogTarget[] debugLogTypes =  { LogTarget.HostDebug };

        internal static class LogHelper
        {
            static readonly string datetimeFormat = "yyyyMMddTHHmmss";
            public static string logFile = Path.Combine(Path.GetTempPath(),"Iv" + System.DateTime.Now.ToString(datetimeFormat) + ".log");
            const string progressStr = "Executing Jobs";

            internal static void Log<T>(LogTarget[] target, T message, PSCmdlet invokeAllInstance, bool noFileLogging = false)
            {
                foreach (LogTarget lT in target)
                {
                    Log(message, lT, invokeAllInstance, noFileLogging);
                }

            }
            internal static void LogDebug(List<string> debugStrs, PSCmdlet invokeAllinstance)
            {
                foreach (string debugEntry in debugStrs)
                {
                    Log(debugEntry, LogTarget.HostDebug, invokeAllinstance);
                }
            }

            internal static void LogDebug(string debugStr, PSCmdlet invokeAllinstance)
            {
                LogDebug(new List<string>() { debugStr }, invokeAllinstance);
            }
            public static void Log<T>(T message, LogTarget logTarget, PSCmdlet invokeAll, bool noFileLogging = false)
            {
                string messageStr = null;
                if (message is ErrorRecord)
                {
                    //cast to object and then to ErrorRecord as direct casting is not allowed
                    messageStr = ((ErrorRecord)(object)message).Exception.Message;
                }
                else
                {
                    messageStr = (string)(object)message;
                }
                string logEntry = string.Format("[{0}] {1}", System.DateTime.Now.ToString(datetimeFormat), messageStr);
                switch (logTarget)
                {
                    case LogTarget.File:
                        if (!noFileLogging)
                        {
                            using (StreamWriter streamWriter = new StreamWriter(logFile, append: true))
                            {
                                streamWriter.WriteLine(logEntry);
                                streamWriter.Close();
                            };
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

            internal static void LogProgress(string currentOperation, PSCmdlet invokeAll, string statusStr = progressStr, int percentComplete = -1, bool quiet = false)
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
