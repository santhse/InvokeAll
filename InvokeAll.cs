// ---------------------------------------------------------------------------
// <copyright file="InvokeAll.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// ---------------------------------------------------------------------------

namespace PSParallel
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Threading.Tasks;

    using static PSParallel.CollectAndCleanUpMethods;
    using static PSParallel.Logger;

    /// <summary>
    /// Invoke-All is the cmdlet exported from the Module. 
    /// It is a wrapper like function which takes input from Pipeline and process the commands consequently using runspaces.
    /// </summary>
    [Cmdlet("Invoke", "All", SupportsShouldProcess = true, DefaultParameterSetName = "Default")]
    [OutputType(typeof(PSObject))]
    public partial class InvokeAll : PSCmdlet
    {
        /// <summary>
        /// Lock to prevent jobs being created by mutiple threads
        /// </summary>
        private readonly object createJobLock = new object();

        /// <summary>
        /// Init variable to hold the command name passed to multi-thread
        /// </summary>
        private string commandName = null;

        /// <summary>
        /// Command info object of the command passed
        /// </summary>
        private CommandInfo cmdInfo = null;

        /// <summary>
        /// If 90% of the Jobs are failed already, prompt if the rest of the jobs should be queued
        /// </summary>
        private int promptOnPercentFaults = 90;

        /// <summary>
        /// Job number that is currently being processed
        /// </summary>
        private int jobNum = 0;

        /// <summary>
        /// The jobs are removed as they complete sucessfully, but this list will retain the faulted jobs for reporting or retry
        /// </summary>
        private ConcurrentDictionary<int, Job> jobs = new ConcurrentDictionary<int, Job>();

        /// <summary>
        /// Parameter Set for Remote Powershell Session
        /// </summary>
        private const string RemotePSParameterSetName = "RemotePS";

        /// <summary>
        /// Parameter Set Default
        /// </summary>
        private const string ModuleParameterSetName = "Default";

        /// <summary>
        /// Parameter Set Default
        /// </summary>
        private const string ReUseRunspaceParameterSetName = "ReUseRunspace";

        /// <summary>
        /// Scriptblock to execute in parallel.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = false,
            HelpMessage = @"
Scriptblock to execute in parallel.
Usually it is the 2nd Pipeline block, wrap your command as shown in the below examples and it should work fine.
You cannot use alias or external scripts. If you are using a function from a custom script, please make sure it is an Advance function or with Param blocks defined properly.
        ")]
        [ValidateNotNullOrEmpty()]
        public ScriptBlock ScriptToRun { get; set; }

        /// <summary>
        /// Input object from the Pipeline
        /// </summary>
        [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true,
            HelpMessage = "Run script against these specified objects. Takes input from Pipeline or when specified explicitly.")]
        [ValidateNotNullOrEmpty()]
        public PSObject InputObject { get; set; }

        /// <summary>
        /// Number of concurrent threads / jobs
        /// </summary>
        [Parameter(Mandatory = false, Position = 2, ValueFromPipeline = false,
            HelpMessage = "Number of threads to be executed in parallel, by default one thread per logical CPU")]
        [ValidateRange(2, 64)]
        public int MaxThreads { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Total number of Jobs to be processed in one batch. When the batch is full, new jobs are queued as any of them in a batch completes
        /// </summary>
        [Parameter(Mandatory = false, Position = 4, ValueFromPipeline = false,
            HelpMessage = "BatchSize controls the number of jobs to run before waiting for one of them to complete")]
        [ValidateRange(0, 1000)]
        public int BatchSize { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Modules to be loaded
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Name of PS Modules or the Full path of the Modules (comma seperated) to load in to the Runspace for the command to work. " +
            "Specifiy 'All' to load all the possible modules or 'Loaded' to load the currently loaded modules")]
        public string[] ModulestoLoad { get; set; }

        /// <summary>
        /// Powershell snapins to be loaded
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Name of PSSnapins (comma seperated) to load in to the Runspace for the command to work.")]
        public string[] PSSnapInsToLoad { get; set; }

        /// <summary>
        /// If a script is being used to multithread and if it uses the local variables or environmentals, use this parameter.
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Copies the local powershell variable names and its values to the Runspace. DON'T modify or loop through variables on the jobs as they are not thread-safe")]
        public SwitchParameter CopyLocalVariables { get; set; }

        /// <summary>
        /// If the command is from a remote powershell session, you may run Get-PSSession and pass the output to this parameter
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = RemotePSParameterSetName,
            HelpMessage = "When specified, Remote Runspace opened on the host will be used")]
        public PSObject RemotePSSessionToUse { get; set; }

        /// <summary>
        /// To deserialize the objects from RemotePowershell session, load all the typedata from the local session to remote.
        /// Useful if remote powershell session is full serialization mode.
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = RemotePSParameterSetName,
            HelpMessage = "When specified, The typedata is not loaded in to the RunspacePool, Specify if loading typedata is delaying the creation of RunspacePool")]
        public SwitchParameter LoadAllTypeDatas { get; set; }

        /// <summary>
        /// This is useful when using Invoke-All on a script that uses the same Runspacepool settings throughout
        /// Re-using the RunspacePool will save time that is spent on creating the Runspacepool
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = ReUseRunspaceParameterSetName,
            HelpMessage = "Run New-InvokeAllRunspacepool and use the Output object on this parameter.")]
        public RunspacePool RunspaceToUse { get; set; }

        /// <summary>
        /// Specifiy a meaning full message to show the progress, something like, "Collecting eventlogs from Servers"
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "Provide any useful message to indicate the stage to the user")]
        public string ProgressBarStage { get; set; } = "Waiting on Jobs to complete";

        /// <summary>
        /// Useful when you want to return to the powershell after queuing the jobs and execute someother commands.
        /// Jobs will continue to run in the background, Use Get-InvokeAllJobsStatus and Receive-InvokeAllJobs to check and collect the results.
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "When specified, job Objects are created and returned immediately. Jobs will continue to run in the background, Use Get-InvokeAllJobsStatus and Receive-InvokeAllJobs to check and collect the results.")]
        public SwitchParameter Async { get; set; }

        /// <summary>
        /// To skip error checking
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "Cmdlet, by default waits for 30 Seconds for the first Job to complete before Queuing rest of the jobs from Pipeline. Use -Force to skip this check")]
        public SwitchParameter Force { get; set; }

        /// <summary>
        /// No log file will be created
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "Specifiy this switch if Logging to a file should be skipped")]
        public SwitchParameter NoFileLogging { get; set; }

        /// <summary>
        /// No progress bar is shown
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "When specified, the Progress bar is not shown")]
        public SwitchParameter Quiet { get; set; }

        /// <summary>
        /// When specified, instead of returing the Job results, returns the invokeall Job objects itself. 
        /// It is useful when you want to access the Streams or other details of each job
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job")]
        public SwitchParameter ReturnasJobObject { get; set; }

        /// <summary>
        /// Append the Jobname to result
        /// </summary>
        [Parameter(Mandatory = false,
    HelpMessage = "When specified, JobName is appended to the result object")]
        public SwitchParameter AppendJobNameToResult { get; set; }

        /// <summary>
        /// Init the ProxyFunction variable
        /// </summary>
        private FunctionInfo ProxyFunction { get; set; } = null;

        /// <summary>
        /// Runspacepool to create threads
        /// </summary>
        private RunspacePool RunspacePool { get; set; } = null;

        /// <summary>
        /// Runspacepool to create threads
        /// </summary>
        private bool ReuseRunspacePool { get; set; } = false;

        /// <summary>
        /// Discover the commad passed and prepare the RunspacePool
        /// </summary>
        protected override void BeginProcessing()
        {
            List<string> debugStrings = new List<string>();

            // Cmdlet will work properly if the Job input is from pipeline, if Input was specified as a parameter just error out
            if (!MyInvocation.ExpectingInput)
            {
                throw new Exception("Cmdlet will only accept input from Pipeline. Please see examples");
            }

            LogHelper.LogProgress("Starting script", this, quiet: Quiet.IsPresent);

            if (Async.IsPresent)
            {
                LogHelper.Log(
                    FileWarningLogTypes,
                    "Async switch is used, Use Get-InvokeAllJobsStatus to check the job status and Receive-InvokeAllJobs to collect the results",
                    this,
                    NoFileLogging.IsPresent);
                LogHelper.Log(
                    FileWarningLogTypes,
                    "No Error check done when Async switch is used. If additional PS modules are required to run the command, please specify them using -ModulestoLoad switch",
                    this,
                    NoFileLogging.IsPresent);
            }

            if (BatchSize < MaxThreads)
            {
                string poorBatchSize = $"The Batchsize {BatchSize} is less than the MaxThreads {MaxThreads}. If you have reduced the Batchsize as a result of throttling, " +
                    "this is ok, else increase the BatchSize for better performance";

                // TODo: Log warning not error
                LogHelper.Log(
                    FileErrorLogTypes,
                    new ErrorRecord(new Exception(poorBatchSize), "BadParameterValues", ErrorCategory.InvalidData, this),
                    this,
                    NoFileLogging.IsPresent);
            }

            ReuseRunspacePool = this.ParameterSetName == ReUseRunspaceParameterSetName ? true : false;

            try
            {
                LogHelper.Log(FileVerboseLogTypes, "Starting script", this, NoFileLogging.IsPresent);
                LogHelper.Log(FileVerboseLogTypes, MyInvocation.Line, this, NoFileLogging.IsPresent);
                cmdInfo = RunspaceMethods.CmdDiscovery(ScriptToRun, out commandName);

                RunspaceMethods.ValidateCmdInfo(cmdInfo, commandName);

                ProxyFunction = RunspaceMethods.CreateProxyFunction(cmdInfo, debugStrings);
                LogHelper.LogDebug(debugStrings, this);
                if (ProxyFunction == null)
                {
                    throw new Exception("Unable to create proxyfunction, please restart the powershell session and try again");
                }
                else
                {
                    LogHelper.Log(FileVerboseLogTypes, $"Created ProxyFunction {ProxyFunction.Name}", this, NoFileLogging.IsPresent);
                }

                // If RunspacePool is passed as a parameter, use it, otherwise, Create the RunspacePool
                if (ReuseRunspacePool)
                {
                    RunspacePool = RunspaceToUse;
                }
                else
                {
                    IList<SessionStateVariableEntry> stateVariableEntries = new List<SessionStateVariableEntry>();
                    if (CopyLocalVariables.IsPresent)
                    {
                        stateVariableEntries = RunspaceMethods.GetSessionStateVariables();
                        LogHelper.Log(
                            FileVerboseLogTypes,
                            $"Will copy {stateVariableEntries.Count} local variables to the Runspace. Use -debug switch to see the details",
                            this,
                            NoFileLogging.IsPresent);
                    }

                    // Create a runspace pool with the cmdInfo collected
                    // Using Dummy PSHost to avoid any messages to the Host. Job will collect all the output on the powershell streams
                    RunspacePool = RunspaceMethods.CreateRunspacePool(
                        commandInfo: cmdInfo,
                        pSHost: new DummyCustomPSHost(),
                        maxRunspaces: MaxThreads,
                        debugStrings: out debugStrings,
                        loadAllTypedata: LoadAllTypeDatas.IsPresent,
                        useRemotePS: (PSSession)RemotePSSessionToUse?.BaseObject,
                        modules: ModulestoLoad,
                        snapIns: PSSnapInsToLoad,
                        variableEntries: stateVariableEntries);
                }

                LogHelper.LogDebug(debugStrings, this);

                // for Logging Purposes, create a powershell instance and log the modules that are *actually* loaded on the runspace
                if (RunspacePool.ConnectionInfo == null)
                {
                    using (PowerShell tempPS = PowerShell.Create())
                    {
                        tempPS.RunspacePool = RunspacePool;
                        var modules = tempPS.AddCommand("Get-Module").Invoke();
                        var moduleNames = from module in modules select ((PSModuleInfo)module.BaseObject).Name;
                        LogHelper.LogDebug(new List<string>() { "Modules found on the RunspacePool:" }, this);
                        LogHelper.LogDebug(moduleNames.ToList(), this);
                        tempPS.Commands.Clear();

                        var loadedVariables = tempPS.AddCommand("Get-Variable").Invoke();
                        var varsValues = from varValue in loadedVariables select ((PSVariable)varValue.BaseObject).Name;
                        LogHelper.LogDebug(new List<string>() { "Vars found on the RunspacePool:" }, this);
                        LogHelper.LogDebug(varsValues.ToList(), this);
                    }
                }
                else
                {
                    if (ModulestoLoad != null || PSSnapInsToLoad != null)
                    {
                        LogHelper.Log(
                            FileWarningLogTypes,
                            "No additional modules that were specified to be loaded will be loaded as it is not supported when using remote PSSession",
                            this,
                            NoFileLogging.IsPresent);
                    }
                }
            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);

                // Log any debug strings
                LogHelper.LogDebug(debugStrings, this);
                LogHelper.Log(FileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                CleanupObjs(this, ProxyFunction, RunspacePool, false, false, ReuseRunspacePool);

                ThrowTerminatingError(error);
            }
        }

        /// <summary>
        /// Create powershell instance for each Job and run it on Ruspacepool.
        /// Use Tasks to create threads and collect the results.
        /// If Force parameter is not used and first Job, perform Error check before Queuing remaining Jobs
        /// </summary>
        protected override void ProcessRecord()
        {
            try
            {
                jobNum++;
                LogHelper.LogProgress($"Queuing Job ID: {jobNum}", this, quiet: Quiet.IsPresent);
                do
                {
                    try
                    {
                        // Create a powershell instance for each input object and it will be processed on a seperate thread
                        PowerShell powerShell = PowerShell.Create();
                        IDictionary psParams = RunspaceMethods.FindParams(ProxyFunction, ScriptToRun, commandName, InputObject);

                        // If command supplied doesn't take any input, bail out.
                        // This check can be removed if this cmdlet needs to run 'something' in parallel, but just didn't find a use case yet
                        if (psParams.Count <= 0)
                        {
                            throw new Exception("No Parameters were used on the command, Please verify the command.");
                        }

                        if (jobNum == 1)
                        {
                            LogHelper.Log(FileVerboseLogTypes, "Paramaters used for the first job:", this, NoFileLogging.IsPresent);
                            foreach (DictionaryEntry param in psParams)
                            {
                                string paramValues = null;
                                if (param.Value is Array)
                                {
                                    foreach (var val in param.Value as Array)
                                    {
                                        paramValues += val?.ToString() + ", ";
                                    }
                                }
                                else
                                {
                                    paramValues = param.Value?.ToString();
                                }

                                LogHelper.Log(FileVerboseLogTypes, $"{param.Key.ToString()} - {paramValues}", this, NoFileLogging.IsPresent);
                            }
                        }

                        if (cmdInfo.CommandType == CommandTypes.ExternalScript)
                        {
                            powerShell.AddScript(((ExternalScriptInfo)cmdInfo).ScriptContents).AddParameters(psParams);
                        }
                        else
                        {
                            powerShell.AddCommand(commandName).AddParameters(psParams);
                        }

                        powerShell.RunspacePool = RunspacePool;

                        // Creates the task and the continuation tasks
                        Task<PSDataCollection<PSObject>> task = Task<PSDataCollection<PSObject>>.Factory.FromAsync(powerShell.BeginInvoke(), r => powerShell.EndInvoke(r));

                        Task faultTask = task.ContinueWith(
                            t =>
                            {
                                var currentJob = CheckandCreateJob(
                                    jobName: InputObject.ToString(),
                                    task: t,
                                    jobID: jobNum,
                                    ps: powerShell);
                                currentJob.IsFaulted = true;
                                currentJob.Exceptions = t.Exception;
                            },
                            TaskContinuationOptions.OnlyOnFaulted);

                        // Continuation tasks may have already created the Job, check before creating it.
                        Job job = CheckandCreateJob(
                                jobName: InputObject.ToString(),
                                task: task,
                                jobID: jobNum,
                                ps: powerShell);

                        // Populate the Job object with the Input Object
                        if (ReturnasJobObject.IsPresent || Async.IsPresent)
                        {
                            job.InputPSObject = InputObject;
                        }

                        if (!jobs.TryAdd(job.ID, job))
                        {
                            if (jobNum == 1)
                            {
                                // We might retry the Job 1 during Error Check, so ignore the error and replace the first job
                                jobs.TryRemove(jobs.First().Key, out Job removedJob);
                                jobs.TryAdd(job.ID, job);
                            }
                            else
                            {
                                throw new Exception($"Unable to add job ID: {job.ID}");
                            }
                        }

                        if (!Async.IsPresent && (jobs.ToList().Count == BatchSize || (!this.Force.IsPresent && (jobNum == 1))))
                        {
                            CollectJobs(this, jobs, jobNum, ProgressBarStage, Force.IsPresent, ReturnasJobObject.IsPresent, AppendJobNameToResult.IsPresent, jobNum);
                        }

                        if (Async.IsPresent)
                        {
                            WriteObject(job);
                        }
                    }
                    catch (AggregateException ae) when (jobNum == 1 && ae.InnerExceptions.Where(ie => ie is CommandNotFoundException).Count() > 0)
                    {
                        IEnumerable<Exception> exceptions = ae.InnerExceptions.Where(ie => ie is CommandNotFoundException);
                        List<string> debugStrings = new List<string>();
                        foreach (Exception exception in exceptions)
                        {
                            CommandInfo commandInfo = RunspaceMethods.GetCommandInfo(((CommandNotFoundException)exception).CommandName);
                            if (commandInfo == null)
                            {
                                throw (CommandNotFoundException)exception;
                            }

                            RunspaceMethods.ModuleDetails moduleDetails = RunspaceMethods.GetModuleDetails(commandInfo, debugStrings);
                            LogHelper.LogDebug(debugStrings, this);
                            LogHelper.Log(FileVerboseLogTypes, exception.Message, this, NoFileLogging);

                            if (moduleDetails.IsFromRemotingModule)
                            {
                                LogHelper.Log(FileVerboseLogTypes, "The command is from a remotePS connection, cannot load local and remote runspaces together", this, NoFileLogging);
                                throw exception;
                            }

                            RunspaceMethods.LoadISSWithModuleDetails(moduleDetails, RunspacePool.InitialSessionState);
                        }

                        RunspacePool = RunspaceMethods.ResetRunspacePool(1, MaxThreads, RunspacePool.InitialSessionState.Clone(), new DummyCustomPSHost());
                        LogHelper.LogDebug("Resetting Runspace to load the new modules", this);
                        LogHelper.Log(FileVerboseLogTypes, "Retrying first job", this, NoFileLogging);
                        RunspacePool.Open();
                    }
                }
                while (!Force.IsPresent && jobNum == 1 && jobs.FirstOrDefault().Value?.IsFaulted == true);

                if (!Force.IsPresent &&
                    (jobs.Where(f => f.Value.IsFaulted == true).Count() / jobNum * 100) > promptOnPercentFaults)
                {
                    if (!ShouldContinue($"More than {promptOnPercentFaults}% of the jobs are faulted, Do you want to continue with processing rest of the Inputs?", "Most Jobs Faulted"))
                    {
                        throw new Exception("Most jobs faulted");
                    }
                }
            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                LogHelper.Log(FileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                CleanupObjs(this, ProxyFunction, RunspacePool, e is PipelineStoppedException, false, ReuseRunspacePool);
                ThrowTerminatingError(error);
            }
        }

        /// <summary>
        /// Collect the last batch of Jobs, process any faulted jobs and Exit
        /// </summary>
        protected override void EndProcessing()
        {
            if (Async.IsPresent)
            {
                // Nothing needs to be done here as Jobs are executed in Aysnc way.
                // Cleanup the ProxyFunction as user may decide to run the same command again
                CleanupObjs(this, ProxyFunction, RunspacePool, false, true);
            }
            else
            {
                try
                {
                    LogHelper.Log(FileVerboseLogTypes, "In EndProcessing", this, NoFileLogging.IsPresent);

                    // Collect the last batch of jobs
                    while (jobs.Where(j => j.Value.IsCollected != true).Count() > 0)
                    {
                        CollectJobs(this, jobs, jobNum, ProgressBarStage, Force, ReturnasJobObject.IsPresent, AppendJobNameToResult.IsPresent, jobNum);
                    }

                    // At this point we will only have the faulted jobs
                    IEnumerable<Job> faultedJobs = jobs.Where(f => f.Value.IsFaulted == true || f.Value.PowerShell.HadErrors == true).Select(fJ => fJ.Value);
                    if (faultedJobs.Count() > 0)
                    {
                        LogHelper.Log(FileWarningLogTypes, $"Completed processing jobs. Jobs collected: {jobNum}. Faulted Jobs: {faultedJobs.Count()}", this, NoFileLogging.IsPresent);
                        LogHelper.Log(FileWarningLogTypes, "Please review the errors", this, NoFileLogging.IsPresent);
                    }
                }
                catch (Exception e)
                {
                    ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                    LogHelper.Log(FileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                    CleanupObjs(this, ProxyFunction, RunspacePool, e is PipelineStoppedException);
                    ThrowTerminatingError(error);
                }
                finally
                {
                    CleanupObjs(this, ProxyFunction, RunspacePool, false, false, ReuseRunspacePool);
                }
            }
        }

        /// <summary>
        /// Cleanup if script was terminated by user
        /// </summary>
        protected override void StopProcessing()
        {
            CleanupObjs(this, ProxyFunction, RunspacePool, true, false, ReuseRunspacePool);
        }

        /// <summary>
        /// Check if the job is already created to avoid duplication
        /// </summary>
        /// <param name="jobName">Name of the Job</param>
        /// <param name="task">Task ID from Powershell.BeginInvoke()</param>
        /// <param name="jobID">Job Number</param>
        /// <param name="ps">Powershell object for the job</param>
        /// <param name="inputPSObject">Inputobject used on the job</param>
        /// <returns>New or existing Job</returns>
        private Job CheckandCreateJob(string jobName, Task<PSDataCollection<PSObject>> task, int jobID, PowerShell ps, PSObject inputPSObject = null)
        {
            lock (createJobLock)
            {
                Job matchingJob = jobs.Where(j => j.Value.JobTask.Id == task.Id).Select(mJ => mJ.Value).FirstOrDefault();
                if (matchingJob == null)
                {
                    matchingJob = new Job
                    {
                        JobName = jobName,
                        JobTask = task,
                        PowerShell = ps,
                        ID = jobID,
                        InputPSObject = inputPSObject
                    };
                }

                return matchingJob;
            }
        }

        /// <summary>
        /// Job object created for each input
        /// </summary>
        public class Job
        {
            /// <summary>
            /// Finalizes an instance of the <see cref="Job" /> class. Dispose the powershell object used on each Job
            /// </summary>
            ~Job()
            {
                this.PowerShell?.Dispose();
            }

            /// <summary>
            /// Job Name, Used to identify the result
            /// </summary>
            public string JobName { get; set; }

            /// <summary>
            /// Task returned by powershell.begininvoke()
            /// </summary>
            public Task<PSDataCollection<PSObject>> JobTask { get; set; }

            /// <summary>
            /// Job number / ID
            /// </summary>
            public int ID { get; set; }

            /// <summary>
            /// Input object that was passed, used when it is needed in the Output Job Object
            /// </summary>
            public PSObject InputPSObject { get; set; }

            /// <summary>
            /// Powershell object to run each job
            /// </summary>
            public PowerShell PowerShell { get; set; }

            /// <summary>
            /// Are the results collected for this job?
            /// </summary>
            public bool IsCollected { get; set; }

            /// <summary>
            /// Job failed to execute
            /// </summary>
            public bool IsFaulted { get; set; }

            /// <summary>
            /// Exceptions from the job, populated when jobs are collected.
            /// </summary>
            public AggregateException Exceptions { get; set; }
        }
    }
}