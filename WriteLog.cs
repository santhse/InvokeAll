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

        internal static class LogHelper
        {
            static readonly string datetimeFormat = "yyyyMMddTHHmmss";
            public static string logFile = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + 
                "\\Iv" + System.DateTime.Now.ToString(datetimeFormat) + ".log";
            const string progressStr = "Executing Jobs";

            internal static void Log<T>(List<LogTarget> target, T message, InvokeAll invokeAllInstance)
            {
                foreach (LogTarget lT in target)
                {
                    Log(message, lT, invokeAllInstance);
                }

            }
            internal static void LogDebug(List<string> debugStrs, InvokeAll invokeAllinstance)
            {
                foreach (string debugEntry in debugStrs)
                {
                    Log(debugEntry, LogTarget.HostDebug, invokeAllinstance);
                }
            }

            internal static void LogDebug(string debugStr, InvokeAll invokeAllinstance)
            {
                LogDebug(new List<string>() { debugStr }, invokeAllinstance);
            }
            public static void Log<T>(T message, LogTarget logTarget, InvokeAll invokeAll)
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
                        if (!invokeAll.NoFileLogging.IsPresent)
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

            internal static void LogProgress(string currentOperation, InvokeAll invokeAll, string statusStr = progressStr, int percentComplete = -1)
            {
                if (invokeAll.Quiet.IsPresent)
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
