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

        private interface ILogBase
        {
            void Log<T>(T message, LogTarget logTarget);
        }
        internal class LogHost : ILogBase
        {
            InvokeAll InvokeAllInstance { get; set; }
            string LogFile { get; set; }

            public LogHost(InvokeAll invokeAll, string log)
            {
                InvokeAllInstance = invokeAll;
                LogFile = log;
            }

            public void Log<T>(T message, LogTarget logTarget)
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
                string logEntry = string.Format("[{0}] {1}", System.DateTime.Now.ToString("yyyy-MM-dd-HHmmss"), messageStr);
                switch (logTarget)
                {
                    case LogTarget.File:
                        if (!InvokeAllInstance.NoFileLogging.IsPresent)
                        {
                            using (StreamWriter streamWriter = new StreamWriter(LogFile, append: true))
                            {
                                streamWriter.WriteLine(logEntry);
                                streamWriter.Close();
                            };
                        }
                        break;
                    case LogTarget.HostVerbose:
                        InvokeAllInstance.WriteVerbose(logEntry);
                        break;
                    case LogTarget.HostDebug:
                        InvokeAllInstance.WriteDebug(logEntry);
                        break;
                    case LogTarget.HostError:
                        if (message is ErrorRecord)
                        {
                            InvokeAllInstance.WriteError((ErrorRecord)(object)message);
                        }
                        break;
                    case LogTarget.HostWarning:
                        InvokeAllInstance.WriteWarning(logEntry);
                        break;
                    default:
                        break;
                }
            }

        }
        internal static class LogHelper
        {
            private static ILogBase logger = null;
            const string progressStr = "Executing Jobs";
            internal static void Log<T>(List<LogTarget> target, T message, InvokeAll invokeAllInstance)
            {
                foreach (LogTarget lT in target)
                {
                    logger = new LogHost(invokeAllInstance, invokeAllInstance.logFile);
                    logger.Log(message, lT);
                }

            }

            internal static void LogDebug(List<string> debugStrs, InvokeAll invokeAllinstance)
            {
                logger = new LogHost(invokeAllinstance, invokeAllinstance.logFile);
                foreach (string debugEntry in debugStrs)
                {
                    logger.Log(debugEntry, LogTarget.HostDebug);
                }
            }

            internal static void LogDebug(string debugStr, InvokeAll invokeAllinstance)
            {
                LogDebug(new List<string>() { debugStr }, invokeAllinstance);
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
