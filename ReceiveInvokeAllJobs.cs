namespace PSParallel
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Text;
    using static PSParallel.CollectAndCleanUpMethods;
    using static PSParallel.Logger;
    using Job = PSParallel.InvokeAll.Job;

    /// <summary>
    /// Exports Receive-InvokeAllJobs Cmdlet
    /// </summary>
    [Cmdlet("Receive", "InvokeAllJobs", SupportsShouldProcess = true, DefaultParameterSetName = "Default")]
    [OutputType(typeof(PSObject))]
    public class ReceiveInvokeAllJobs : PSCmdlet
    {
        /// <summary>
        /// New Dictionary object to hold Job objects
        /// </summary>
        public ConcurrentDictionary<int, Job> Jobs { get; set; } = new ConcurrentDictionary<int, Job>();

        /// <summary>
        /// PSParallel Job objects returned from Invoke-All -ASync Switch
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            HelpMessage = "PSParallel Job objects returned from Invoke-All -ASync Switch")]
        [ValidateNotNullOrEmpty()]
        public Job[] JobObjects { get; set; }

        /// <summary>
        /// Wait for all Jobs to complete and receive the Output
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "Wait for all Jobs to complete and receive the Output")]
        public SwitchParameter Wait { get; set; }

        /// <summary>
        /// Return Job object
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job")]
        public SwitchParameter ReturnasJobObject { get; set; }

        /// <summary>
        /// Append Job Name to the result
        /// </summary>
        [Parameter(Mandatory = false,
    HelpMessage = "When specified, JobName is appended to the result object")]
        public SwitchParameter AppendJobNameToResult { get; set; }

        /// <summary>
        /// To receive pipeline inputs
        /// </summary>
        protected override void ProcessRecord()
        {
            foreach (Job jobObject in JobObjects)
            {
                Jobs.TryAdd(jobObject.ID, jobObject);
            }
        }

        /// <summary>
        /// Main processing, collect all the jobs
        /// </summary>
        protected override void EndProcessing()
        {
            if (Wait.IsPresent)
            {
                CollectAllJobs(Jobs);
            }
            else
            {
                int numPendingJobs = Jobs.Where(j => j.Value.JobTask.IsCompleted != true).Count();
                if (numPendingJobs > 0)
                {
                    StringBuilder jobsNotCompletedStr = new StringBuilder($"{ numPendingJobs } Jobs have not completed yet.Would you like to collect the Jobs that are completed? ");
                    jobsNotCompletedStr.AppendLine("If yes, You will have to run this command again later to collect rest of the job results. ");
                    if (ShouldContinue(jobsNotCompletedStr.ToString(), "Jobs still not completed:"))
                    {
                        CollectAllJobs(Jobs, true);
                    }
                }
                else
                {
                    CollectAllJobs(Jobs);
                }
            }
        }

        /// <summary>
        /// Wrapper to call CollectandCleanupFunctions
        /// </summary>
        /// <param name="jobs">Jobs that needs to be collected</param>
        /// <param name="completedJobsOnly">Should collect completed jobs and return, defaults false.</param>
        private void CollectAllJobs(ConcurrentDictionary<int, Job> jobs, bool completedJobsOnly = false)
        {
            RunspacePool runspacePool = jobs.First().Value.PowerShell.RunspacePool;
            if (completedJobsOnly)
            {
                LogHelper.Log(FileVerboseLogTypes, "Collecting partial jobs that are completed", this);
                jobs = new ConcurrentDictionary<int, Job>(jobs.Where(j => j.Value.JobTask.IsCompleted == true));
            }

            try
            {
                LogHelper.Log(FileVerboseLogTypes, "In EndProcessing", this);
                int totalJobs = jobs.Skip(0).Count();
                LogHelper.Log(FileVerboseLogTypes, $"Number of Jobs that will be collected: {totalJobs}", this);

                // Collect the last batch of jobs
                while (jobs.Where(j => j.Value.IsCollected != true).Count() > 0)
                {
                    CollectJobs(this, jobs, totalJobs, "Collecting Jobs", true, ReturnasJobObject.IsPresent, AppendJobNameToResult.IsPresent);
                }

                // At this point we will only have the faulted jobs
                IEnumerable<Job> faultedJobs = jobs.Where(f => f.Value.IsFaulted == true || f.Value.PowerShell.HadErrors == true).Select(fJ => fJ.Value);
                if (faultedJobs.Count() > 0)
                {
                    LogHelper.Log(FileWarningLogTypes, $"Completed processing jobs. Jobs collected: {jobs.Count()}. Faulted Jobs: {faultedJobs.Count()}", this);
                    LogHelper.Log(FileWarningLogTypes, "Please review the errors", this);
                }
            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                LogHelper.Log(FileVerboseLogTypes, error.Exception.ToString(), this);
                CleanupObjs(this, null, runspacePool, e is PipelineStoppedException);
                ThrowTerminatingError(error);
            }
            finally
            {
                if (!completedJobsOnly)
                {
                    CleanupObjs(this, null, runspacePool, false, false);
                }
            }
        }
    }
}
