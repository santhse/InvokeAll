namespace PSParallel
{
    using System.Management.Automation;
    using System;
    using System.Threading.Tasks;
    using System.Linq;
    using static PSParallel.Logger;

    partial class InvokeAll : PSCmdlet
    {
        private readonly object createJobLock = new object();
        private int faultedJobsCount = 0;
        private Job CheckandCreateJob(string jobName, Task<PSDataCollection<PSObject>> task, int jobID, PowerShell ps)
        {
            lock(createJobLock)
            {
                Job matchingJob = jobs.Values.Where(j => j.JobTask.Id == task.Id)?.FirstOrDefault();
                if (matchingJob == null)
                {
                    matchingJob = new Job
                    {
                        JobName = jobName,
                        JobTask = task,
                        PowerShell = ps,
                        ID = jobID
                    };

                }
                return matchingJob;
            }
        }
        private void CleanupObjs(bool isFromPipelineStoppedException)
        {
            LogHelper.LogProgress("Completed", this, "Completed");
            LogHelper.Log(fileVerboseLogTypes, "In Cleanup", this);
            if (!isFromPipelineStoppedException && proxyFunction != null)
            {
                ScriptBlock.Create(@"Remove-Item Function:" + proxyFunction.Name).Invoke();
            }

            if (runspacePool != null)
            {
                if (runspacePool.ConnectionInfo != null)
                {
                    //runspacePool.Disconnect();
                }
                runspacePool.Close();
                runspacePool.Dispose();
            }

            if(!NoFileLogging.IsPresent)
            {
                LogHelper.Log(fileVerboseLogTypes, (string.Format("Log file updated: {0}", logFile)), this);
            }
        }
        private void CollectJobs(int jobID)
        {
            if (!Force.IsPresent && jobID == 1)
            {
                LogHelper.LogProgress("Error check: Waiting 30 seconds for the first job to complete", this);

                //For the first job, default the wait to 30 sceonds and do a error check, if it is not completed, prompt user with option to wait or to continue..
                try
                {                    
                    if ((bool)jobs.First().Value.JobTask.Wait(TimeSpan.FromSeconds(30)))
                    {
                        LogHelper.Log(fileVerboseLogTypes, "First Job completed",this);
                        CollectJob(jobs.First().Value);
                        LogHelper.Log(fileVerboseLogTypes, "Will queue rest of the Jobs as the current Batch completes", this);
                        LogHelper.Log(fileVerboseLogTypes, string.Format("Queuing Jobs, Batch Size {0}", BatchSize), this);
                        return;
                    }
                    else
                    {
                        LogHelper.Log(fileVerboseLogTypes, "First Job didn't complete in 30 seconds", this);
                        if (!ShouldContinue("First instance still running, Want to continue queuing the rest of the jobs?", "FirstJob Error Check"))
                        {
                            throw new Exception("Aborted from the first instance error check");
                        }
                    }
                }
                catch (Exception e)
                {
                    ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidResult, this);
                    ThrowTerminatingError(error);
                }
                
            }

            Job[] pendingJobs = jobs.Values.Where(q => q.IsCollected != true).ToArray(); 
            if (pendingJobs.Count() <= 0)
            {
                LogHelper.Log(fileVerboseLogTypes, "There are no pending jobs to collect", this);
                return;
            }

            int completedJobs = jobNum - pendingJobs.Count();
            LogHelper.LogProgress(string.Format("Active Queue: {0} / Completed: {1} - Faulted {2}, waiting for any one of them to complete", pendingJobs.Count(), completedJobs, faultedJobsCount), this, ProgressBarStage);
            if (completedJobs % BatchSize == 0)
            {
                LogHelper.Log(fileVerboseLogTypes, string.Format("Completed Jobs: {0}. Pending Jobs: {1}", completedJobs, pendingJobs.Count()), this);
            }

            //wait on the pendingjobs. Task.Waitany will return the index of the Completed Job, using the index find the job in the Jobs dictionary and collect the results
            //ToDo: Implement timeout logic for each Job e.g: (JobTimeoutinSecs == -1) ? TimeSpan.FromMilliseconds(-1) : TimeSpan.FromSeconds(JobTimeoutinSecs));
            //-1 is returned if the timeout occurred, implement a way to find timeout on each job
            int completedTask = Task.WaitAny((from pendingJob in pendingJobs select pendingJob.JobTask).ToArray());
            
            if(jobs.TryGetValue(pendingJobs.ElementAt(completedTask).ID, out Job processedJob) == true)
            {
                CollectJob(processedJob);
            }
            else
            {
                LogHelper.Log(fileErrorLogTypes, string.Format("TaskID {0} completed, but wasnt found at Jobs array. Please report this issue", completedTask), this);
            }
        }

        private void CollectJob(Job job)
        {
            LogHelper.LogDebug(string.Format("Collecting JobID:{0}",job.ID), this);
            if (job.IsFaulted == true || job.PowerShell.HadErrors == true)
            {
                //when a job is faulted, decide here to either prompt or just continue..
                //if there is a way to find throttling implement logic to delay the queueing of next tasks
                if (!Force.IsPresent && job.ID == 1)
                {
                    LogHelper.Log(fileVerboseLogTypes, "There was an error from First job, will stop queueing jobs.", this);
                    if (job.Exceptions != null)
                    {
                        ErrorRecord error = new ErrorRecord(job.Exceptions, job.Exceptions.GetType().Name, ErrorCategory.InvalidResult, this);
                        ThrowTerminatingError(error);
                    }
                    else
                    {
                        ErrorRecord error = new ErrorRecord(new Exception(job.PowerShell.Streams.Error.ToString()), "CmdErr", ErrorCategory.InvalidResult, this);
                        ThrowTerminatingError(error);
                    }
                }
                else
                {
                    HandleFaultedJob(job);
                }
            }
            else
            {
                if (ReturnasJobObject.IsPresent)
                {
                    WriteObject(job);
                }
                else
                {
                    if (job.JobTask.Result?.Count > 0)
                    {
                        WriteObject(job.JobTask.Result);
                    }
                    else
                    {
                        LogHelper.Log(fileVerboseLogTypes, string.Format("JobID:{0} JobName: {1} has no result to be appened to the output. IsJobCompleted:{2}, IsJobFaulted:{3}", job.ID, job.JobName, job.JobTask.IsCompleted.ToString(), job.IsFaulted.ToString()), this);
                    }
                }
                
                if (!jobs.TryRemove(job.ID, out Job removedJob) == true)
                {
                    LogHelper.LogDebug(string.Format("Unable to remove Job {0}", job.ID), this);
                }
            }
            job.IsCollected = true;
        }

        private void HandleFaultedJob(Job faultedJob)
        {
            faultedJobsCount++;
            LogHelper.Log(fileVerboseLogTypes, string.Format("Job: {0} is faulted.", faultedJob.ID), this);
            if (ReturnasJobObject.IsPresent == true)
            {
                WriteObject(faultedJob);
            }
            else
            {
                if (faultedJob.IsFaulted == true)
                {
                    foreach (var e in faultedJob.Exceptions.InnerExceptions)
                    {
                        if (e is RemoteException re)
                        {
                            LogHelper.Log(fileErrorLogTypes, re.ErrorRecord, this);
                        }
                        else
                        {
                            LogHelper.Log(fileErrorLogTypes, ((ActionPreferenceStopException)e).ErrorRecord, this);
                        }
                    }
                }

                if (faultedJob.PowerShell.Streams.Error.Count > 0)
                {
                    foreach (ErrorRecord errorRecord in faultedJob.PowerShell.Streams.Error)
                    {
                        LogHelper.Log(fileErrorLogTypes, errorRecord, this);
                    }

                }
            }
        }
    }
}