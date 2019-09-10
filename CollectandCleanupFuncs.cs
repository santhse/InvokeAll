namespace PSParallel
{
    using System.Management.Automation;
    using System;
    using System.Threading.Tasks;
    using System.Linq;
    using System.Management.Automation.Runspaces;
    using System.Collections.Concurrent;
    using static PSParallel.Logger;
    using Job = InvokeAll.Job;
    
    internal static class CollectAndCleanUpMethods
    {
        internal static void CleanupObjs(PSCmdlet invokeAll, FunctionInfo proxyFunctionInfo, RunspacePool runspacePool, bool isFromPipelineStoppedException, 
            bool isAsyncEnd = false, bool noFileLogging = false, bool quiet = false)
        {
            LogHelper.LogProgress("Completed", invokeAll, "Completed", quiet:quiet);
            LogHelper.Log(fileVerboseLogTypes, "In Cleanup", invokeAll, noFileLogging);
            if (!isFromPipelineStoppedException && proxyFunctionInfo != null)
            {
                ScriptBlock.Create(@"Remove-Item Function:" + proxyFunctionInfo.Name).Invoke();
            }

            if (!isAsyncEnd && runspacePool != null)
            {
                if (runspacePool.ConnectionInfo != null)
                {
                    //runspacePool.Disconnect();
                }
                runspacePool.Close();
                runspacePool.Dispose();
            }
        }

        internal static void CollectJobs(PSCmdlet invokeAll, ConcurrentDictionary<int,Job> jobs, int totalJobs, string progressBarStage, 
            bool force, bool returnAsJobObject, bool appendJobNameToResult, 
            int jobID = 0, bool noFileLogging = false, bool quiet = false)
        {
            if (!force && jobID == 1)
            {
                LogHelper.LogProgress("Error check: Waiting 30 seconds for the first job to complete", invokeAll, quiet:quiet);
                
                //For the first job, default the wait to 30 sceonds and do a error check, if it is not completed, prompt user with option to wait or to continue..
                if ((bool)jobs.First().Value.JobTask.Wait(TimeSpan.FromSeconds(30)))
                {
                    LogHelper.Log(fileVerboseLogTypes, "First Job completed", invokeAll, noFileLogging);
                    Job firstCollectedJob = CollectJob(invokeAll, jobs.First().Value, returnAsJobObject, force, appendJobNameToResult);

                    if (!jobs.TryRemove(firstCollectedJob.ID, out Job removedJob) == true)
                    {
                        LogHelper.LogDebug(string.Format("Unable to remove Job {0}", firstCollectedJob.ID), invokeAll);
                    }
                    LogHelper.Log(fileVerboseLogTypes, "Will queue rest of the Jobs as the current Batch completes", invokeAll, noFileLogging);

                    return;
                }
                else
                {
                    LogHelper.Log(fileVerboseLogTypes, "First Job didn't complete in 30 seconds", invokeAll, noFileLogging);
                    if (!invokeAll.ShouldContinue("First instance still running, Want to continue queuing the rest of the jobs?", "FirstJob Error Check"))
                    {
                        throw new Exception("Aborted from the first instance error check");
                    }
                }
            }

            var pendingJobs = jobs.Where(q => q.Value.IsCollected != true);
            var faultedJobs = jobs.Where(f => f.Value.IsFaulted == true || f.Value.PowerShell.HadErrors == true);
            if (pendingJobs.Count() <= 0)
            {
                LogHelper.Log(fileVerboseLogTypes, "There are no pending jobs to collect", invokeAll, noFileLogging);
                return;
            }

            int completedJobs = totalJobs - pendingJobs.Count();
            LogHelper.LogProgress(string.Format("Active Queue: {0} / Completed: {1} - Faulted {2}, waiting for any one of them to complete", 
                pendingJobs.Count(), completedJobs, faultedJobs.Count()), 
                invokeAll, 
                progressBarStage, quiet:quiet);

            //Todo: log something to the file, but not every job completed
            int completedTask = Task.WaitAny((from pendingJob in pendingJobs select pendingJob.Value.JobTask).ToArray());
                        
            if(jobs.TryGetValue(pendingJobs.ElementAt(completedTask).Value.ID, out Job processedJob) == true)
            {
                Job collectedJob = CollectJob(invokeAll, processedJob, returnAsJobObject, force, appendJobNameToResult);
                
                if (!jobs.TryRemove(collectedJob.ID, out Job removedJob) == true)
                {
                    LogHelper.LogDebug(string.Format("Unable to remove Job {0}", collectedJob.ID), invokeAll);
                }
            }
            else
            {
                LogHelper.Log(fileErrorLogTypes, 
                    string.Format("TaskID {0} completed, but wasnt found at Jobs array. Please report this issue", completedTask), 
                    invokeAll,
                    noFileLogging);
            }
        }

        internal static Job CollectJob(PSCmdlet invokeAll, Job job, bool returnAsJobObject, bool force, bool appendJobNameToResult,
            bool noFileLogging = false)
        {
            LogHelper.LogDebug(string.Format("Collecting JobID:{0}",job.ID), invokeAll);
            if (job.IsFaulted == true || job.PowerShell.HadErrors == true)
            {
                if (!force && job.ID == 1)
                {
                    LogHelper.Log(fileVerboseLogTypes, "There was an error from First job, will stop queueing jobs.", invokeAll, noFileLogging);
                    if (job.Exceptions != null)
                    {
                        throw job.Exceptions;
                    }
                    else
                    {
                        invokeAll.ThrowTerminatingError(job.PowerShell.Streams.Error.FirstOrDefault());
                    }
                }
                else
                {
                    HandleFaultedJob(invokeAll, job, returnAsJobObject);
                }
            }
            else
            {
                if (returnAsJobObject)
                {
                    invokeAll.WriteObject(job);
                }
                else
                {
                    if (job.JobTask.Result?.Count > 0)
                    {
                        foreach(PSObject result in job.JobTask.Result)
                        {
                            if (appendJobNameToResult)
                            {
                                result.Members.Add(new PSNoteProperty("PSJobName", job.JobName));
                            }
                            invokeAll.WriteObject(result);
                        }
                    }
                    else
                    {
                        LogHelper.Log(fileVerboseLogTypes, 
                            string.Format("JobID:{0} JobName: {1} has no result to be appened to the output. IsJobCompleted:{2}, IsJobFaulted:{3}", 
                            job.ID, job.JobName, job.JobTask.IsCompleted.ToString(), job.IsFaulted.ToString()), 
                            invokeAll,
                            noFileLogging);
                    }
                }
                
                
            }
            job.IsCollected = true;
            return job;
        }

        internal static void HandleFaultedJob(PSCmdlet invokeAll, Job faultedJob, bool returnAsJobObject,
            bool noFileLogging = false)
        {
            LogHelper.Log(fileVerboseLogTypes, string.Format("Job: {0} is faulted.", faultedJob.ID), invokeAll, noFileLogging);
            if (returnAsJobObject)
            {
                invokeAll.WriteObject(faultedJob);
            }
            else
            {
                if (faultedJob.IsFaulted == true)
                {
                    foreach (var e in faultedJob.Exceptions.InnerExceptions)
                    {
                        if (e is RemoteException re)
                        {
                            LogHelper.Log(fileErrorLogTypes, re.ErrorRecord, invokeAll, noFileLogging);
                        }
                        else
                        {
                            LogHelper.Log(fileErrorLogTypes, ((ActionPreferenceStopException)e).ErrorRecord, invokeAll, noFileLogging);
                        }
                    }
                }

                if (faultedJob.PowerShell.Streams.Error.Count > 0)
                {
                    foreach (ErrorRecord errorRecord in faultedJob.PowerShell.Streams.Error)
                    {
                        LogHelper.Log(fileErrorLogTypes, errorRecord, invokeAll, noFileLogging);
                    }
                }
            }
        }
    }
}