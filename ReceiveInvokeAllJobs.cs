using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using static PSParallel.CollectAndCleanUpMethods;
using static PSParallel.Logger;
using Job = PSParallel.InvokeAll.Job;

namespace PSParallel
{
    [Cmdlet("Receive", "InvokeAllJobs", SupportsShouldProcess = true, DefaultParameterSetName = "Default")]
    [OutputType(typeof(PSObject))]
    public class ReceiveInvokeAllJobs : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            HelpMessage = "PSParallel Job objects returned from Invoke-All -ASync Switch")]
        [ValidateNotNullOrEmpty()]
        public Job[] JobObjects { get; set; }

        [Parameter(Mandatory = false,
            HelpMessage = "Wait for all Jobs to complete and receive the Output")]
        public SwitchParameter Wait { get; set; }
        public ConcurrentDictionary<int, Job> Jobs { get; set; } = new ConcurrentDictionary<int, Job>();

        [Parameter(Mandatory = false,
            HelpMessage = "When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job")]
        public SwitchParameter ReturnasJobObject;

        [Parameter(Mandatory = false,
    HelpMessage = "When specified, JobName is appended to the result object")]
        public SwitchParameter AppendJobNameToResult;

        private void CollectAllJobs(ConcurrentDictionary<int,Job> jobs, bool completedJobsOnly = false)
        {
            RunspacePool runspacePool = jobs.First().Value.PowerShell.RunspacePool;
            if (completedJobsOnly)
            {
                LogHelper.Log(fileVerboseLogTypes, "Collecting partial jobs that are completed", this);
                jobs = new ConcurrentDictionary<int, Job>(jobs.Where(j => j.Value.JobTask.IsCompleted == true));
            }
            try
            {
                LogHelper.Log(fileVerboseLogTypes, "In EndProcessing", this);
                int totalJobs = jobs.Count;
                LogHelper.Log(fileVerboseLogTypes, string.Format("Number of Jobs that will be collected: {0}", totalJobs), this);

                //Collect the last batch of jobs
                while (jobs.Values.Where(j => j.IsCollected != true).Count() > 0)
                {
                    CollectJobs(this, jobs, totalJobs, "Collecting Jobs", false, ReturnasJobObject, AppendJobNameToResult);
                }

                //At this point we will only have the faulted jobs
                IEnumerable<Job> faultedJobs = jobs.Values.Where(f => f.IsFaulted == true || f.PowerShell.HadErrors == true);
                if (faultedJobs.Count() > 0)
                {
                    LogHelper.Log(fileWarningLogTypes, string.Format("Completed processing jobs. Jobs collected: {0}. Faulted Jobs: {1}", jobs.Count(), faultedJobs.Count()), this);
                    LogHelper.Log(fileWarningLogTypes, "Please review the errors", this);
                }

            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                LogHelper.Log(fileVerboseLogTypes, error.Exception.ToString(), this);
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

        protected override void ProcessRecord()
        {
            foreach (Job jobObject in JobObjects)
            {
                Jobs.TryAdd(jobObject.ID, jobObject);
            }
        }

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
                    if (ShouldContinue(String.Format("{0} Jobs have not completed yet. Would you like to collect the Jobs that are completed? " +
                        "If yes, You will have to run this command again later to collect rest of the job results.", numPendingJobs),"Jobs still not completed:"))
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
    }
}
