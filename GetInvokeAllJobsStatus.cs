using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading.Tasks;
using Job = PSParallel.InvokeAll.Job;

namespace PSParallel
{
    [Cmdlet("Get", "InvokeAllJobsStatus")]
    [OutputType(typeof(PSObject))]
    public class GetInvokeAllJobsStatus : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            HelpMessage = "PSParallel Job objects returned from Invoke-All -ASync Switch")]
        [ValidateNotNullOrEmpty()]
        public Job[] JobObjects { get; set; }

        List<Job> jobs = new List<Job>();
        protected override void ProcessRecord()
        {
            foreach (Job jobObject in JobObjects)
            {
                jobs.Add(jobObject);
            }
        }

        protected override void EndProcessing()
        {
            Host.UI.Write(ConsoleColor.Green, Host.UI.RawUI.BackgroundColor, string.Format("Completed :{0} / ", jobs.Count(j => j.JobTask.IsCompleted == true)));
            Host.UI.Write(ConsoleColor.Red, Host.UI.RawUI.BackgroundColor, string.Format("Faulted :{0} / ", jobs.Count(j => j.JobTask.IsFaulted == true)));
            Host.UI.Write(ConsoleColor.Cyan, Host.UI.RawUI.BackgroundColor, 
                string.Format("Pending :{0}", jobs.Count(j => (j.JobTask.Status == TaskStatus.WaitingForActivation) || j.JobTask.Status == TaskStatus.WaitingToRun)));
        }

    }

}
