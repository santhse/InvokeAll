using System;
using System.Linq;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Concurrent;
using static PSParallel.CollectAndCleanUpMethods;
using static PSParallel.Logger;

namespace PSParallel
{
    /// <summary>
    /// Invoke-All is the cmdlet exported from the Module. 
    /// It is a wrapper like function which takes input from Pipeline and process the commands consequently using runspaces.
    /// </summary>
    [Cmdlet("Invoke", "All", SupportsShouldProcess = true, DefaultParameterSetName = "Default")]
    [OutputType(typeof(PSObject))]

    public partial class InvokeAll : PSCmdlet
    {
        private const string ModuleParameterSetName = "Default";
        private const string RemotePSParameterSetName = "RemotePS";

        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = false,
            HelpMessage = @"
Scriptblock to execute in parallel.
Usually it is the 2nd Pipeline block, wrap your command as shown in the below examples and it should work fine.
You cannot use alias or external scripts. If you are using a function from a custom script, please make sure it is an Advance function or with Param blocks defined properly.
        ")]
        [ValidateNotNullOrEmpty()]
        public ScriptBlock ScriptToRun { get; set; }

        [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true,
            HelpMessage = "Run script against these specified objects. Takes input from Pipeline or when specified explicitly.")]
        [ValidateNotNullOrEmpty()]
        public PSObject InputObject { get; set; }

        [Parameter(Mandatory = false, Position = 2, ValueFromPipeline = false,
            HelpMessage = "Number of threads to be executed in parallel, by default one thread per logical CPU")]
        [ValidateRange(2,64)]
        public int MaxThreads { get; set; } = Environment.ProcessorCount;

        [Parameter(Mandatory = false, Position = 4, ValueFromPipeline = false,
            HelpMessage ="BatchSize controls the number of jobs to run before waiting for one of them to complete")]
        [ValidateRange(0,1000)]
        public int BatchSize { get; set; } = Environment.ProcessorCount;

        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Name of PS Modules or the Full path of the Modules (comma seperated) to load in to the Runspace for the command to work. " +
            "Specifiy 'All' to load all the possible modules or 'Loaded' to load the currently loaded modules")]
        public string[] ModulestoLoad { get; set; }

        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Name of PSSnapins (comma seperated) to load in to the Runspace for the command to work.")]
        public string[] PSSnapInsToLoad { get; set; }

        [Parameter(Mandatory = false, ParameterSetName = ModuleParameterSetName,
            HelpMessage = "Copies the local powershell variable names and its values to the Runspace. DON'T modify or loop through variables on the jobs as they are not thread-safe")]
        public SwitchParameter CopyLocalVariables;

        [Parameter(Mandatory = false, ParameterSetName = RemotePSParameterSetName,
            HelpMessage = "When specified, Remote Runspace opened on the host will be used")]
        public PSObject UseRemotePSSession;

        [Parameter(Mandatory = false, ParameterSetName = RemotePSParameterSetName,
            HelpMessage = "When specified, The typedata is not loaded in to the RunspacePool, Specify if loading typedata is delaying the creation of RunspacePool")]
        public SwitchParameter LoadAllTypeDatas;

        [Parameter(Mandatory = false,
            HelpMessage = "Provide any useful message to indicate the stage to the user")]
        public string ProgressBarStage { get; set; } = "Waiting on Jobs to complete";

        [Parameter(Mandatory = false,
            HelpMessage = "When specified, job Objects are created and returned immediately. Jobs will continue to run in the background, Use Get-InvokeAllJobsStatus and Receive-InvokeAllJobs to check and collect the results.")]
        public SwitchParameter Async;

        [Parameter(Mandatory = false,
            HelpMessage ="Cmdlet, by default waits for 30 Seconds for the first Job to complete before Queuing rest of the jobs from Pipeline. Use -Force to skip this check")]
        public SwitchParameter Force;

        [Parameter(Mandatory = false,
            HelpMessage ="Specifiy this switch if Logging to a file should be skipped")]
        public SwitchParameter NoFileLogging;
        
        [Parameter(Mandatory = false,
            HelpMessage ="When specified, the Progress bar is not shown")]
        public SwitchParameter Quiet;

        [Parameter(Mandatory = false,
            HelpMessage = "When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job")]
        public SwitchParameter ReturnasJobObject;

        [Parameter(Mandatory = false,
    HelpMessage = "When specified, JobName is appended to the result object")]
        public SwitchParameter AppendJobNameToResult;

        internal FunctionInfo proxyFunction = null;
        string commandName = null;
        CommandInfo cmdInfo = null;
        //Command types that are supported by this script
        CommandTypes[] supportedCommandTypes = { CommandTypes.ExternalScript, CommandTypes.Cmdlet, CommandTypes.Function, CommandTypes.Alias };
        internal RunspacePool runspacePool = null;
        
        int promptOnPercentFaults = 90;
        
        public class Job
        {
            public string JobName { get; set; }
            public Task<PSDataCollection<PSObject>> JobTask { get; set; }
            public int ID { get; set; }
            public PowerShell PowerShell { get; set; }
            public bool IsCollected { get; set; }
            public bool IsFaulted { get; set; }
            public AggregateException Exceptions { get; set; }

            ~Job(){
                this.PowerShell?.Dispose();
            }
        }
        
        private readonly object createJobLock = new object();
        private Job CheckandCreateJob(string jobName, Task<PSDataCollection<PSObject>> task, int jobID, PowerShell ps)
        {
            lock (createJobLock)
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

        //The jobs are removed as they complete sucessfully, but this list will retain the faulted jobs for reporting or retry
        //ToDo: Implement retry
        ConcurrentDictionary<int, Job> jobs = new ConcurrentDictionary<int, Job>();//(MaxParallel, MaxParallel);
        
        /// <summary>
        /// Discover the commad passed and prepare the RunspacePool
        /// </summary>
        protected override void BeginProcessing()
        {
            List<string> debugStrings = new List<string>();
            //System.Globalization.CultureInfo
            //Environment.GetFolderPath(Environment.SpecialFolder.)
            //Cmdlet will work properly if the Job input is from pipeline, if Input was specified as a parameter just error out
            if (!MyInvocation.ExpectingInput)
            {
                throw new Exception("Cmdlet will only accept input from Pipeline. Please see examples");
            }
            LogHelper.LogProgress("Starting script", this, quiet:Quiet.IsPresent);

            if (Async.IsPresent)
            {
                LogHelper.Log(fileWarningLogTypes, "Async switch is used, Use Get-InvokeAllJobsStatus to check the job status and Receive-InvokeAllJobs to collect the results", this, NoFileLogging.IsPresent);
                LogHelper.Log(fileWarningLogTypes, 
                    "No Error check done when Async switch is used. If additional PS modules are required to run the command, please specify them using -ModulestoLoad switch", 
                    this, NoFileLogging.IsPresent);
            }

            if (BatchSize < MaxThreads)
            {
                //TODo: Log warning not error
                LogHelper.Log(fileErrorLogTypes,
                    new ErrorRecord(new Exception(string.Format("The Batchsize {0} is less than the MaxThreads {1}. If you have reduced the Batchsize as a result of throttling, " +
                    "this is ok, else increase the BatchSize for better performance", BatchSize, MaxThreads)),
                    "BadParameterValues", ErrorCategory.InvalidData, this),
                    this,
                    NoFileLogging.IsPresent);
            }
            
            try
            {
                LogHelper.Log(fileVerboseLogTypes, "Starting script", this, NoFileLogging.IsPresent);
                LogHelper.Log(fileVerboseLogTypes, MyInvocation.Line, this, NoFileLogging.IsPresent);
                cmdInfo = RunspaceMethods.CmdDiscovery(ScriptToRun, out commandName);

                if (cmdInfo == null)
                {
                    throw new Exception("Unable to find the command specified to Invoke, please make sure required modules or functions are loaded to PS Session");
                }

                if (supportedCommandTypes.Contains(cmdInfo.CommandType))
                {
                    LogHelper.Log(fileVerboseLogTypes, String.Format("The supplied command {0} is of type {1}", commandName, cmdInfo.CommandType.ToString()),this, NoFileLogging.IsPresent);
                }
                else
                {
                    string notSupportedErrorString = "Invoke-All doesn't implement methods to run this command, Supported types are PSScripts, PSCmdlets and PSFunctions";
                    LogHelper.Log(fileVerboseLogTypes, notSupportedErrorString, this, NoFileLogging.IsPresent);
                    throw new Exception(notSupportedErrorString);
                }
                
                proxyFunction = RunspaceMethods.CreateProxyFunction(cmdInfo, debugStrings);
                LogHelper.LogDebug(debugStrings, this);
                if (proxyFunction == null)
                {
                    throw new Exception("Unable to create proxyfunction, please restart the powershell session and try again");
                }
                else
                {
                    LogHelper.Log(fileVerboseLogTypes, string.Format("Created ProxyFunction {0}", proxyFunction.Name), this, NoFileLogging.IsPresent);
                }

                IList<SessionStateVariableEntry> stateVariableEntries = new List<SessionStateVariableEntry>();
                if(CopyLocalVariables.IsPresent)
                {
                    string psGetUDVariables = @"
                        function Get-UDVariable {
                          get-variable | where-object {(@(
                            'FormatEnumerationLimit',
                            'MaximumAliasCount',
                            'MaximumDriveCount',
                            'MaximumErrorCount',
                            'MaximumFunctionCount',
                            'MaximumVariableCount',
                            'PGHome',
                            'PGSE',
                            'PGUICulture',
                            'PGVersionTable',
                            'PROFILE',
                            'PSSessionOption'
                            ) -notcontains $_.name) -and `
                            (([psobject].Assembly.GetType('System.Management.Automation.SpecialVariables').GetFields('NonPublic,Static') `
                            | Where-Object FieldType -eq ([string]) | ForEach-Object GetValue $null)) -notcontains $_.name
                          }
                        }

                        Get-UDVariable

                        ";

                    IEnumerable<PSVariable> variables = ScriptBlock.Create(psGetUDVariables).Invoke().Select(v => v.BaseObject as PSVariable);
                    variables.ToList().ForEach(v => stateVariableEntries.Add(new SessionStateVariableEntry(v.Name, v.Value, null)));
                    LogHelper.Log(fileVerboseLogTypes, 
                        string.Format("Will copy {0} local variables to the Runspace. Use -debug switch to see the details",stateVariableEntries.Count), 
                        this,
                        NoFileLogging.IsPresent);
                }

                //Create a runspace pool with the cmdInfo collected
                //Using Dummy PSHost to avoid any messages to the Host. Job will collect all the output on the powershell streams
                runspacePool = RunspaceMethods.CreateRunspacePool(
                    commandInfo: cmdInfo,
                    pSHost: new DummyCustomPSHost(),
                    maxRunspaces: MaxThreads,
                    debugStrings: out debugStrings,
                    loadAllTypedata: LoadAllTypeDatas.IsPresent,
                    useRemotePS: (PSSession)UseRemotePSSession?.BaseObject,
                    modules: ModulestoLoad,
                    snapIns: PSSnapInsToLoad,
                    variableEntries: stateVariableEntries
                    );

                LogHelper.LogDebug(debugStrings, this);
                runspacePool.ThreadOptions = PSThreadOptions.ReuseThread;
                runspacePool.Open();
                LogHelper.LogDebug("Opened RunspacePool", this);

                //for Logging Purposes, create a powershell instance and log the modules that are *actually* loaded on the runspace
                if (runspacePool.ConnectionInfo == null)
                {
                    using (PowerShell tempPS = PowerShell.Create())
                    {
                        tempPS.RunspacePool = runspacePool;
                        var modules = tempPS.AddCommand("Get-Module").Invoke();
                        var moduleNames = from module in modules select ((PSModuleInfo)(module.BaseObject)).Name;
                        LogHelper.LogDebug(new List<string>() { "Modules found on the RunspacePool:" }, this);
                        LogHelper.LogDebug(moduleNames.ToList(), this);
                        tempPS.Commands.Clear();

                        var loadedVariables = tempPS.AddCommand("Get-Variable").Invoke();
                        var varsValues = from varValue in loadedVariables select ((PSVariable)(varValue.BaseObject)).Name;
                        LogHelper.LogDebug(new List<string>() { "Vars found on the RunspacePool:" }, this);
                        LogHelper.LogDebug(varsValues.ToList(), this);
                    }
                }
                else
                {
                    if (ModulestoLoad != null || PSSnapInsToLoad != null)
                    {
                        LogHelper.Log(fileWarningLogTypes, 
                            "No additional modules that were specified to be loaded will be loaded as it is not supported when using remote PSSession", 
                            this,
                            NoFileLogging.IsPresent);
                    }
                }
                
            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                //Log any debug strings
                LogHelper.LogDebug(debugStrings, this);
                LogHelper.Log(fileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                CleanupObjs(this, proxyFunction, runspacePool, false);

                ThrowTerminatingError(error);
            }
        }
        
        int jobNum = 0;
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
                
                do
                {
                    try
                    {
                        //Create a powershell instance for each input object and it will be processed on a seperate thread
                        PowerShell powerShell = PowerShell.Create();
                        IDictionary psParams = RunspaceMethods.FindParams(proxyFunction, ScriptToRun, commandName, InputObject);
                
                        //If command supplied doesn't take any input, bail out.
                        //This check can be removed if this cmdlet needs to run 'something' in parallel, but just didn't find a use case yet
                        if (psParams.Count <= 0)
                        {
                            throw new Exception("No Parameters were used on the command, Please verify the command.");
                        }

                        if (jobNum == 1)
                        {
                            LogHelper.LogProgress("Queuing First Job", this, quiet:Quiet.IsPresent);
                            LogHelper.Log(fileVerboseLogTypes, "Paramaters used for the first job:", this, NoFileLogging.IsPresent);
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
                                LogHelper.Log(fileVerboseLogTypes, string.Format("{0} - {1}", param.Key.ToString(), paramValues), this, NoFileLogging.IsPresent);
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
                        
                        powerShell.RunspacePool = runspacePool;
                        //Creates the task and the continuation tasks
                        Task<PSDataCollection<PSObject>> task = Task<PSDataCollection<PSObject>>.Factory.FromAsync(powerShell.BeginInvoke(), r => powerShell.EndInvoke(r));

                        task.ContinueWith(t =>
                        {
                            var currentJob = CheckandCreateJob(
                                jobName: InputObject.ToString(),
                                task: t,
                                jobID: jobNum,
                                ps: powerShell);
                            currentJob.IsFaulted = true;
                            currentJob.Exceptions = t.Exception;
                        }, TaskContinuationOptions.OnlyOnFaulted);

                        //Continuation tasks may have already created the Job, check before creating it.
                        Job job = CheckandCreateJob(
                                jobName: InputObject.ToString(),
                                task: task,
                                jobID: jobNum,
                                ps: powerShell);

                        if (!jobs.TryAdd(job.ID, job))
                        {
                            if (jobNum == 1)
                            {
                                //We might retry the Job 1 during Error Check, so ignore the error and replace the first job
                                jobs.TryRemove(jobs.First().Key, out Job removedJob);
                                jobs.TryAdd(job.ID, job);
                            }
                            else
                            {
                                throw new Exception(string.Format("Unable to add job ID: {0}", job.ID));
                            }
                        }

                        if (!Async.IsPresent
                                && (jobs.Count == BatchSize || (!this.Force.IsPresent && (jobNum == 1))))
                        {
                            CollectJobs(this, jobs, jobNum, ProgressBarStage, Force, ReturnasJobObject, AppendJobNameToResult, jobNum);
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
                        foreach(Exception exception in exceptions)
                        {
                            CommandInfo commandInfo = RunspaceMethods.GetCommandInfo(((CommandNotFoundException)exception).CommandName);
                            if (commandInfo == null)
                            {
                                throw (CommandNotFoundException)exception;
                            }

                            RunspaceMethods.ModuleDetails moduleDetails = RunspaceMethods.GetModuleDetails(commandInfo, debugStrings);
                            LogHelper.LogDebug(debugStrings, this);
                            LogHelper.Log(fileVerboseLogTypes, exception.Message, this, NoFileLogging);

                            if (moduleDetails.IsFromRemotingModule)
                            {
                                LogHelper.Log(fileVerboseLogTypes, "The command is from a remotePS connection, cannot load local and remote runspaces together", this, NoFileLogging);
                                throw exception;
                            }

                            RunspaceMethods.LoadISSWithModuleDetails(moduleDetails, runspacePool.InitialSessionState);
                        }
                        runspacePool = RunspaceMethods.ResetRunspacePool(1, MaxThreads, runspacePool.InitialSessionState.Clone(), new DummyCustomPSHost());
                        LogHelper.LogDebug("Resetting Runspace to load the new modules", this);
                        LogHelper.Log(fileVerboseLogTypes, "Retrying first job", this, NoFileLogging);
                        runspacePool.Open();
                    }

                } while (!Force.IsPresent && jobNum == 1 && jobs.FirstOrDefault().Value?.IsFaulted == true);

                if (!Force.IsPresent && 
                    (jobs.Values.Where(f => f.IsFaulted == true).Count() / jobNum * 100) > promptOnPercentFaults)
                {
                    if (!ShouldContinue(string.Format("More than {0}% of the jobs are faulted, Do you want to continue with processing rest of the Inputs?", promptOnPercentFaults), "Most Jobs Faulted"))
                    {
                        throw new Exception("Most jobs faulted");
                    }
                }

            }
            catch (Exception e)
            {
                ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                LogHelper.Log(fileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                CleanupObjs(this, proxyFunction, runspacePool, e is PipelineStoppedException);
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
                //Nothing needs to be done here as Jobs are executed in Aysnc way.
                //Cleanup the ProxyFunction as user may decide to run the same command again
                CleanupObjs(this, proxyFunction, runspacePool, false, true);
            }
            else
            {
                try
                {
                    LogHelper.Log(fileVerboseLogTypes, "In EndProcessing", this, NoFileLogging.IsPresent);

                    //Collect the last batch of jobs
                    while (jobs.Values.Where(j => j.IsCollected != true).Count() > 0)
                    {
                        CollectJobs(this, jobs, jobNum, ProgressBarStage, Force, ReturnasJobObject, AppendJobNameToResult, jobNum);
                    }

                    //At this point we will only have the faulted jobs
                    IEnumerable<Job> faultedJobs = jobs.Values.Where(f => f.IsFaulted == true || f.PowerShell.HadErrors == true);
                    if (faultedJobs.Count() > 0)
                    {
                        LogHelper.Log(fileWarningLogTypes, string.Format("Completed processing jobs. Jobs collected: {0}. Faulted Jobs: {1}", jobNum, faultedJobs.Count()), this, NoFileLogging.IsPresent);
                        LogHelper.Log(fileWarningLogTypes, "Please review the errors", this, NoFileLogging.IsPresent);
                    }

                }
                catch (Exception e)
                {
                    ErrorRecord error = new ErrorRecord(e, e.GetType().Name, ErrorCategory.InvalidData, this);
                    LogHelper.Log(fileVerboseLogTypes, error.Exception.ToString(), this, NoFileLogging.IsPresent);
                    CleanupObjs(this, proxyFunction, runspacePool, e is PipelineStoppedException);
                    ThrowTerminatingError(error);
                }
                finally
                {
                    CleanupObjs(this, proxyFunction, runspacePool, false, false);
                }
            }
            
        }
        
        protected override void StopProcessing()
        {
            CleanupObjs(this, proxyFunction, runspacePool, true, false);
        }
    }
}
