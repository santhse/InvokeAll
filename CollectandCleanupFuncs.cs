namespace PSParallel
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Threading.Tasks;

    using static PSParallel.Logger;
    using Job = InvokeAll.Job;

    /// <summary>
    /// Static functions for collecting results of the Jobs and Job Cleanup, to be used with Invoke-All cmdlet
    /// </summary>
    internal static class CollectAndCleanUpMethods
    {
        /// <summary>
        /// Close the Runspace pool and remove any proxy function that was created
        /// </summary>
        /// <param name="invokeAll">PSCmdlet host</param>
        /// <param name="proxyFunctionInfo">Proxy Function created by Invoke-All</param>
        /// <param name="runspacePool">Runsapcepool object</param>
        /// <param name="isFromPipelineStoppedException">When powershell pipeline is stopped, skip removing the proxyfunction</param>
        /// <param name="isAsyncEnd">Is cleanup called with -Async switch specified</param>
        /// <param name="noFileLogging">Do not write to Log file</param>
        /// <param name="quiet">Do not show the Powershell progress bar</param>
        internal static void CleanupObjs(
            PSCmdlet invokeAll,
            FunctionInfo proxyFunctionInfo,
            RunspacePool runspacePool,
            bool isFromPipelineStoppedException,
            bool isAsyncEnd = false,
            bool noFileLogging = false,
            bool quiet = false)
        {
            LogHelper.LogProgress("Completed", invokeAll, "Completed", quiet: quiet);
            LogHelper.Log(FileVerboseLogTypes, "In Cleanup", invokeAll, noFileLogging);

            if (!isFromPipelineStoppedException && proxyFunctionInfo != null)
            {
                ScriptBlock.Create(@"Remove-Item Function:" + proxyFunctionInfo.Name).Invoke();
            }

            if (!isAsyncEnd && runspacePool != null)
            {
                runspacePool.Close();
                runspacePool.Dispose();
            }
        }

        /// <summary>
        /// Collect results for the completed jobs in the Jobs Dictionary.
        /// Wait 30 seconds for first job's error check. If error check is not required wait Indefinitely for any of the job to complete and collect the results
        /// Remove the Job from the Jobs Dictionary as the results are collected and returned to the caller
        /// </summary>
        /// <param name="invokeAll">PSCmdlet host</param>
        /// <param name="jobs">Jobs Dictionary</param>
        /// <param name="totalJobs">Count of total jobs accepted on the Process block</param>
        /// <param name="progressBarStage">Operation being performed to show as progress</param>
        /// <param name="force">To skip error check</param>
        /// <param name="returnAsJobObject">Return the Job object instead of outputting the results</param>
        /// <param name="appendJobNameToResult">Add Job name to the result</param>
        /// <param name="jobID">Current JobID from the process block</param>
        /// <param name="noFileLogging">Do not write to Log file</param>
        /// <param name="quiet">Do not show the Powershell progress bar</param>
        internal static void CollectJobs(
            PSCmdlet invokeAll,
            ConcurrentDictionary<int, Job> jobs,
            int totalJobs,
            string progressBarStage,
            bool force,
            bool returnAsJobObject,
            bool appendJobNameToResult,
            int jobID = 0,
            bool noFileLogging = false,
            bool quiet = false)
        {
            if (!force && jobID == 1)
            {
                LogHelper.LogProgress("Waiting 30 seconds for the first job to complete", invokeAll, "Error Check", quiet: quiet);

                // For the first job, default the wait to 30 sceonds and do a error check, if it is not completed, prompt user with option to wait or to continue..
                if ((bool)jobs.First().Value.JobTask.Wait(TimeSpan.FromSeconds(30)))
                {
                    LogHelper.Log(FileVerboseLogTypes, "First Job completed", invokeAll, noFileLogging);
                    Job firstCollectedJob = CollectJob(invokeAll, jobs.First().Value, returnAsJobObject, force, appendJobNameToResult);

                    if (!jobs.TryRemove(firstCollectedJob.ID, out Job removedJob) == true)
                    {
                        LogHelper.LogDebug($"Unable to remove Job {firstCollectedJob.ID}", invokeAll);
                    }

                    LogHelper.Log(FileVerboseLogTypes, "Will queue rest of the Jobs as the current Batch completes", invokeAll, noFileLogging);
                    return;
                }
                else
                {
                    LogHelper.Log(FileVerboseLogTypes, "First Job didn't complete in 30 seconds", invokeAll, noFileLogging);
                    if (!invokeAll.ShouldContinue("First instance still running, Want to continue queuing the rest of the jobs?", "FirstJob Error Check"))
                    {
                        throw new Exception("Aborted from the first instance error check");
                    }
                }

                LogHelper.LogProgress("Completed", invokeAll, "Error Check", 100, quiet: quiet);
            }

            var pendingJobs = jobs.Where(q => q.Value.IsCollected != true);
            var faultedJobs = jobs.Where(f => f.Value.IsFaulted == true || f.Value.PowerShell.HadErrors == true);
            if (pendingJobs.Count() <= 0)
            {
                LogHelper.Log(FileVerboseLogTypes, "There are no pending jobs to collect", invokeAll, noFileLogging);
                return;
            }

            int completedJobs = totalJobs - pendingJobs.Count();
            LogHelper.LogProgress(
                $"Active Queue: {pendingJobs.Count()} / Completed: {completedJobs} - Faulted {faultedJobs.Count()}, waiting for any one of them to complete",
                invokeAll,
                progressBarStage,
                quiet: quiet);

            // Todo: log something to the file, but not every job completed
            int completedTask = Task.WaitAny((from pendingJob in pendingJobs select pendingJob.Value.JobTask).ToArray());

            if (jobs.TryGetValue(pendingJobs.ElementAt(completedTask).Value.ID, out Job processedJob) == true)
            {
                Job collectedJob = CollectJob(invokeAll, processedJob, returnAsJobObject, force, appendJobNameToResult);

                if (!jobs.TryRemove(collectedJob.ID, out Job removedJob) == true)
                {
                    LogHelper.LogDebug($"Unable to remove Job {collectedJob.ID}", invokeAll);
                }
            }
            else
            {
                LogHelper.Log(
                    FileErrorLogTypes,
                    $"TaskID {completedTask} completed, but wasnt found at Jobs array. Please report this issue",
                    invokeAll,
                    noFileLogging);
            }
        }

        /// <summary>
        /// Collect the completed Job result.
        /// </summary>
        /// <param name="invokeAll">PSCmdlet host</param>
        /// <param name="job">Job Object</param>
        /// <param name="returnAsJobObject">Return the Job object instead of outputting the results</param>
        /// <param name="force">To skip error check</param>
        /// <param name="appendJobNameToResult">Add Job name to the result</param>
        /// <param name="noFileLogging">Do not write to Log file</param>
        /// <returns>Collected Job Object</returns>
        internal static Job CollectJob(PSCmdlet invokeAll, Job job, bool returnAsJobObject, bool force, bool appendJobNameToResult, bool noFileLogging = false)
        {
            LogHelper.LogDebug($"Collecting JobID:{job.ID}", invokeAll);
            if (job.IsFaulted == true || job.PowerShell.HadErrors == true)
            {
                if (!force && job.ID == 1)
                {
                    job.IsFaulted = true;
                    LogHelper.Log(FileVerboseLogTypes, "There was an error from First job, will stop queueing jobs.", invokeAll, noFileLogging);
                    if (job.Exceptions != null)
                    {
                        throw job.Exceptions;
                    }
                    else
                    {
                        List<Exception> jobExceptions = new List<Exception>();
                        foreach (ErrorRecord errorRecord in job.PowerShell.Streams.Error)
                        {
                            jobExceptions.Add(errorRecord.Exception);
                        }

                        throw new AggregateException(jobExceptions);
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
                        foreach (PSObject result in job.JobTask.Result)
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
                        LogHelper.Log(
                            FileVerboseLogTypes,
                            $"JobID:{job.ID} JobName: {job.JobName} has no result to be appened to the output. IsJobCompleted:{job.JobTask.IsCompleted.ToString()}, IsJobFaulted:{job.IsFaulted.ToString()}",
                            invokeAll,
                            noFileLogging);
                    }
                }
            }

            job.IsCollected = true;
            return job;
        }

        /// <summary>
        /// Log the faulted Job
        /// </summary>
        /// <param name="invokeAll">PSCmdlet host</param>
        /// <param name="faultedJob">Faulted Job Object</param>
        /// <param name="returnAsJobObject">Return the Job object instead of outputting the results</param>
        /// <param name="noFileLogging">Do not write to Log file</param>
        internal static void HandleFaultedJob(PSCmdlet invokeAll, Job faultedJob, bool returnAsJobObject, bool noFileLogging = false)
        {
            LogHelper.Log(FileVerboseLogTypes, $"Job: {faultedJob.ID} is faulted.", invokeAll, noFileLogging);
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
                        LogHelper.Log(new LogTarget[] { LogTarget.HostError }, $"Job {faultedJob.ID} / {faultedJob.JobName} faulted", invokeAll, noFileLogging);
                        LogHelper.Log(FileErrorLogTypes, ((RuntimeException)e).ErrorRecord, invokeAll, noFileLogging);
                    }
                }

                if (faultedJob.PowerShell.Streams.Error.Count > 0)
                {
                    foreach (ErrorRecord errorRecord in faultedJob.PowerShell.Streams.Error)
                    {
                        LogHelper.Log(FileErrorLogTypes, errorRecord, invokeAll, noFileLogging);
                    }
                }
            }
        }
    }
}