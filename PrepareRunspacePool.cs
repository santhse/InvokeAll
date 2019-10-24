namespace PSParallel
{
    using System;
    using System.Collections.Generic;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using static PSParallel.Logger;
    using static RunspaceMethods;

    /// <summary>
    /// Creates a new runspacepool with the parameters supplied.
    /// The Runspacepool that is returned to the PS is to be used with Invoke-All -RunspaceToUse parameter
    /// This is useful when using Invoke-All on a script that uses the same Runspacepool settings throughout
    /// Re-using the RunspacePool will save time that is spent on creating the Runspacepool
    /// </summary>
    [Cmdlet("New", "InvokeAllRunspacePool", DefaultParameterSetName = "Default")]
    [OutputType(typeof(PSObject))]
    public class PrepareRunspacePool : PSCmdlet
    {
        /// <summary>
        /// Parameter Set for Remote Powershell Session
        /// </summary>
        private const string RemotePSParameterSetName = "RemotePS";

        /// <summary>
        /// Parameter Set Default
        /// </summary>
        private const string ModuleParameterSetName = "Default";

        /// <summary>
        /// Sample Command to be used to Prepare the Runspacepool with
        /// </summary>
        [Parameter(Mandatory = true,
            HelpMessage = "Runspacepool will be loaded with settings that are required to run this Command")]
        [ValidateNotNullOrEmpty()]
        public string CommandName { get; set; }

        /// <summary>
        /// Number of concurrent threads / jobs
        /// </summary>
        [Parameter(Mandatory = false,
            HelpMessage = "Number of threads to be executed in parallel, by default one thread per logical CPU")]
        [ValidateRange(2, 64)]
        public int MaxThreads { get; set; } = Environment.ProcessorCount;

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
        /// Create and return the Runspacepool for the command supplied
        /// </summary>
        protected override void ProcessRecord()
        {
            List<string> debugStrings = new List<string>();

            CommandInfo cmdInfo = GetCommandInfo(CommandName);
            ValidateCmdInfo(cmdInfo, CommandName);

            IList<SessionStateVariableEntry> stateVariableEntries = new List<SessionStateVariableEntry>();
            if (CopyLocalVariables.IsPresent)
            {
                stateVariableEntries = RunspaceMethods.GetSessionStateVariables();
            }

            // Create a runspace pool with the cmdInfo collected
            // Using Dummy PSHost to avoid any messages to the Host.
            WriteObject(
                CreateRunspacePool(
                    commandInfo: cmdInfo,
                    pSHost: new DummyCustomPSHost(),
                    maxRunspaces: MaxThreads,
                    debugStrings: out debugStrings,
                    loadAllTypedata: LoadAllTypeDatas.IsPresent,
                    useRemotePS: (PSSession)RemotePSSessionToUse?.BaseObject,
                    modules: ModulestoLoad,
                    snapIns: PSSnapInsToLoad,
                    variableEntries: stateVariableEntries));
            LogHelper.LogDebug(debugStrings, this);
        }
    }
}
