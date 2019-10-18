namespace PSParallel
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Host;
    using System.Management.Automation.Language;
    using System.Management.Automation.Runspaces;

    /// <summary>
    /// Functions to discover the PS command and prepare the RunspacePool
    /// </summary>
    internal static class RunspaceMethods
    {
        /// <summary>
        /// Run Get-Command for the passed command on the current powershell instance to discover the CommandInfo
        /// </summary>
        /// <param name="scriptBlock">Entire command string passed to mulithread</param>
        /// <param name="cmd">Cmdlet or Command discovered</param>
        /// <returns>CommandInfo of the discovreed command</returns>
        internal static CommandInfo CmdDiscovery(ScriptBlock scriptBlock, out string cmd)
        {
            IEnumerable<Ast> cmdAsts = scriptBlock.Ast.FindAll(ast => ast is CommandAst, true);
            CommandAst cmdElement = cmdAsts?.FirstOrDefault() as CommandAst;
            if (cmdElement != null)
            {
                cmd = cmdElement.CommandElements.First().ToString();
                return GetCommandInfo(cmd);
            }

            cmd = "unknown";
            return null;
        }

        /// <summary>
        /// Loads the IntialSessionState with the module details. Used when Preparing RunspacePool
        /// </summary>
        /// <param name="moduleDetails">ModuleDetails object with all settings populated</param>
        /// <param name="iss">ISS object to populate</param>
        internal static void LoadISSWithModuleDetails(ModuleDetails moduleDetails, InitialSessionState iss)
        {
            PSSnapInException pSSnapInException = new PSSnapInException();
            if (moduleDetails.Functions.Count > 0)
            {
                iss.Commands.Add(moduleDetails.Functions.FirstOrDefault());
            }

            if (moduleDetails.PSModule.Length > 0)
            {
                iss.ImportPSModule(new string[] { moduleDetails.PSModule });
            }

            if (moduleDetails.PSSnapIn.Length > 0)
            {
                iss.ImportPSSnapIn(moduleDetails.PSSnapIn, out pSSnapInException);
            }
        }

        /// <summary>
        /// Executes "Get-command" on the current PSHost to collect the command details
        /// </summary>
        /// <param name="cmd">Command to discover</param>
        /// <returns>CommandInfo object of the command passed</returns>
        internal static CommandInfo GetCommandInfo(string cmd)
        {
            // there could be scope used, trim it before checking, eg: global:myglobalfunction
            cmd = cmd.Split(':').Last();
            var cmdScript = ScriptBlock.Create("Get-Command " + cmd + " | Select-Object -First 1");
            PSObject cmdDetails = (PSObject)cmdScript.InvokeReturnAsIs();
            return cmdDetails?.BaseObject as CommandInfo;
        }

        /// <summary>
        /// Using the commandInfo, populate the module details and return it
        /// </summary>
        /// <param name="commandInfo">CommandInfo object of the command</param>
        /// <param name="debugStrings">Outputs any debug strings</param>
        /// <returns>ModuleDetails object</returns>
        internal static ModuleDetails GetModuleDetails(CommandInfo commandInfo, List<string> debugStrings)
        {
            ModuleDetails moduleDetails = new ModuleDetails();
            switch (commandInfo.CommandType)
            {
                case CommandTypes.Cmdlet:
                    CmdletInfo cmdletInfo = commandInfo as CmdletInfo;
                    if (cmdletInfo.Module != null)
                    {
                        debugStrings.Add($"The command {commandInfo.Name} is from Module {cmdletInfo.ModuleName}");
                        moduleDetails.PSModule = cmdletInfo.ModuleName;
                    }

                    if (cmdletInfo.PSSnapIn != null)
                    {
                        debugStrings.Add($"The command {commandInfo.Name} is from PSSnapin {cmdletInfo.PSSnapIn.Name}");
                        moduleDetails.PSSnapIn = cmdletInfo.PSSnapIn.Name;
                    }

                    break;
                case CommandTypes.Function:
                    FunctionInfo functionInfo = commandInfo as FunctionInfo;
                    if (functionInfo != null)
                    {
                        // Add the function definition anyway as the function can be local or from a module/file
                        moduleDetails.Functions.Add(new SessionStateFunctionEntry(functionInfo.Name, functionInfo.Definition));
                        if (functionInfo.ScriptBlock.File != null)
                        {
                            debugStrings.Add($"The command is a custom function {commandInfo.Name} from file {functionInfo.ScriptBlock.File}");
                            FileInfo scriptFileInfo = new FileInfo(functionInfo.ScriptBlock.File);

                            // if the function is from a PS1 script, don't load it as a module. The script will be executed and will cause unintended results
                            if (!scriptFileInfo.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                            {
                                moduleDetails.PSModule = functionInfo.ScriptBlock.File;
                            }
                        }
                    }

                    break;
                case CommandTypes.ExternalScript:
                    debugStrings.Add($"The command {commandInfo.Name} is of type ExternalScript");
                    break;
                default:
                    break;
            }

            Hashtable modulePrivatedata = commandInfo.Module?.PrivateData as Hashtable;

            // special handling for remote PSsession commands
            if (modulePrivatedata != null && modulePrivatedata.ContainsKey("ImplicitRemoting"))
            {
                moduleDetails.IsFromRemotingModule = true;
            }

            return moduleDetails;
        }

        /// <summary>
        /// Create a Proxy Function for the command supplied. This is used to Mock the command and discover the parameters passed
        /// </summary>
        /// <param name="commandInfo">Command details object</param>
        /// <param name="debugStrings">For logging</param>
        /// <returns>FunctionInfo Object</returns>
        internal static FunctionInfo CreateProxyFunction(CommandInfo commandInfo, List<string> debugStrings)
        {
            string proxyCommandName = null;
            string proxyCommand = ProxyCommand.Create(new CommandMetadata(commandInfo));
            ScriptBlock proxyScript = ScriptBlock.Create(proxyCommand);
            ScriptBlockAst proxyScriptAst = (ScriptBlockAst)proxyScript.Ast;

            string proxyScriptStr = proxyScriptAst.ParamBlock.ToString() + "; return $PSBoundParameters";
            proxyScriptStr = proxyScriptStr.Replace("\r\n", string.Empty);
            debugStrings.Add(proxyScriptStr);
            if (commandInfo.CommandType == CommandTypes.ExternalScript)
            {
                proxyCommandName = commandInfo.Name.Split('\\').LastOrDefault().Replace(".ps1", "IvAllhelper.ps1");
            }
            else
            {
                proxyCommandName = commandInfo.Name + "IvAllhelper";
            }

            debugStrings.Add($"Creating ProxyFunction {proxyCommandName}");

            try
            {
                PSObject proxyFunctionPSObj = ScriptBlock.Create("param($name, $script) New-Item -Path Function:Global:$name -Value $script -Erroraction Stop")
                    .Invoke(proxyCommandName, proxyScriptStr).FirstOrDefault();
                debugStrings.Add($"Created proxy command {proxyCommandName}");

                return proxyFunctionPSObj.BaseObject as FunctionInfo;
            }
            catch (ActionPreferenceStopException ae) when ((ae.ErrorRecord.Exception is PSArgumentException) && ae.ErrorRecord.Exception.Message.EndsWith("already exists."))
            {
                // Todo: throwterminatingerror here
                debugStrings.Add("ProxyCommand Already Exsits");
                string removeProxyFunction = $"Remove-Item -Path Function:{proxyCommandName} -Force";
                ScriptBlock.Create(removeProxyFunction).Invoke();
                throw new Exception(
                    $"Proxy function {proxyCommandName} already exists. It is present from a previous failure of Invoke-all, the script tried to remove it. " +
                    "Simply re-run your command to see if it works, if not, please manually remove the function using {removeProxyFunction}");
            }
        }

        /// <summary>
        /// Create the Runspace pool.
        /// For remote runspaces, load the datatypes optionally to support serialization
        /// For local commands, load the modules and snapins required or requested
        /// </summary>
        /// <param name="commandInfo">Command details</param>
        /// <param name="pSHost">Current HOST or a DummyPSHost object</param>
        /// <param name="maxRunspaces">Number of threads</param>
        /// <param name="debugStrings">for Logging</param>
        /// <param name="useRemotePS">Remote PSSession to use</param>
        /// <param name="loadAllTypedata">If using remote PSSession, and if true, load all the typedata</param>
        /// <param name="modules">Modules to load</param>
        /// <param name="modulesPath">Path of modules</param>
        /// <param name="snapIns">PSSnapins to load</param>
        /// <param name="variableEntries">Variables to load</param>
        /// <returns>RunspacePool with all the required modules and settings loaded</returns>
        internal static RunspacePool CreateRunspacePool(
            CommandInfo commandInfo,
            PSHost pSHost,
            int maxRunspaces,
            out List<string> debugStrings,
            PSSession useRemotePS,
            bool loadAllTypedata,
            string[] modules = null,
            string[] modulesPath = null,
            string[] snapIns = null,
            IList<SessionStateVariableEntry> variableEntries = null)
        {
            debugStrings = new List<string>();
            RunspaceConnectionInfo runspaceConnectionInfo = null;
            Hashtable modulePrivatedata = commandInfo.Module?.PrivateData as Hashtable;

            ModuleDetails moduleDetails = GetModuleDetails(commandInfo, debugStrings);

            // special handling for remote PSsession commands
            if (moduleDetails.IsFromRemotingModule || useRemotePS != null)
            {
                if (useRemotePS != null)
                {
                    debugStrings.Add("Using the supplied remote PSSession");
                    runspaceConnectionInfo = useRemotePS.Runspace.ConnectionInfo;
                }
                else
                {
                    debugStrings.Add("Using remote PSSession to execute the command");
                    PSObject remotepSModule = ScriptBlock.Create($"Get-Module {commandInfo.ModuleName}").InvokeReturnAsIs() as PSObject;
                    PSModuleInfo remotepSModuleInfo = remotepSModule.BaseObject as PSModuleInfo;
                    if (remotepSModule != null)
                    {
                        string getPSSession = $"Get-PSSession | Where-Object{{$_.state -eq 'opened' -and (\"{remotepSModuleInfo.Description}\").Contains($_.ComputerName)}} | Select-Object -First 1";
                        PSObject remotePs = ScriptBlock.Create(getPSSession).InvokeReturnAsIs() as PSObject;

                        PSSession remotePSSession = remotePs.BaseObject as PSSession;
                        if (remotePSSession != null)
                        {
                            runspaceConnectionInfo = remotePSSession.Runspace.ConnectionInfo;
                            if (modules != null || modulesPath != null)
                            {
                                debugStrings.Add("Modules were specified to load, but they will not be loaded as the command supplied is from a remote PSSession");
                            }
                        }
                        else
                        {
                            debugStrings.Add(
                                $"Command - Get-PSSession | Where-Object{{$_.state -eq 'opened' -and (\"{remotepSModuleInfo.Description}\").Contains($_.ComputerName)}} " +
                                "| Select-Object -First 1 - was ran to find the PSSession");
                            throw new Exception("Unable to find a PSSession to use. You may try passing the PSSession to use using Parameter -UseRemotePSSession");
                        }
                    }
                }

                debugStrings.Add($"Using connection info {runspaceConnectionInfo.ComputerName}");
                TypeTable typeTable = TypeTable.LoadDefaultTypeFiles();

                if (loadAllTypedata)
                {
                    Collection<PSObject> typeDatas = ScriptBlock.Create("Get-TypeData").Invoke();
                    foreach (PSObject typeData in typeDatas)
                    {
                        TypeData t = (TypeData)typeData.BaseObject;
                        try
                        {
                            typeTable.AddType(t);
                            debugStrings.Add($"Added typedata{t.TypeName}");
                        }
                        catch (Exception e)
                        {
                            debugStrings.Add($"Unable to add typeData {t.TypeName}. Error {e.Message}");
                        }
                    }
                }

                return RunspaceFactory.CreateRunspacePool(1, Environment.ProcessorCount, runspaceConnectionInfo, pSHost, typeTable);
            }

            InitialSessionState iss = InitialSessionState.CreateDefault2();
            List<string> modulesToLoad = new List<string>();
            List<string> snapInsToLoad = new List<string>();
            PSSnapInException pSSnapInException = new PSSnapInException();

            if (modules?.Count() > 0)
            {
                modulesToLoad.AddRange(modules);
            }

            if (snapIns?.Count() > 0)
            {
                snapInsToLoad.AddRange(snapIns);
            }

            // Populate ISS with the snapins and modules from the moduleDetails
            LoadISSWithModuleDetails(moduleDetails, iss);

            // Load user specified snapins and modules
            if (modules?.Count() > 0 && modules.Contains("All", StringComparer.OrdinalIgnoreCase))
            {
                var modulesAvailable = ScriptBlock.Create("Get-Module -ListAvailable | Select-Object -ExpandProperty Name").Invoke();
                modulesToLoad.AddRange(from module in modulesAvailable select module.BaseObject as string);
                debugStrings.Add($"Loaded all the available modules on this computer, {modulesToLoad.Count} modules found");
            }

            if (modules?.Count() > 0 && modules.Contains("Loaded", StringComparer.OrdinalIgnoreCase))
            {
                var modulesLoaded = ScriptBlock.Create("Get-Module | Select-Object -ExpandProperty Name").Invoke();
                modulesToLoad.AddRange(from module in modulesLoaded select module.BaseObject as string);
                debugStrings.Add($"Loaded the modules loaded on current Runspace, {modulesLoaded.Count} modules found");
            }

            debugStrings.Add("Loading Modules:");
            debugStrings.AddRange(modulesToLoad);
            iss.ImportPSModule(modulesToLoad.ToArray());

            snapInsToLoad.ForEach(s => iss.ImportPSSnapIn(s, out pSSnapInException));

            if (variableEntries != null)
            {
                iss.Variables.Add(variableEntries);
            }

            return RunspaceFactory.CreateRunspacePool(1, maxRunspaces, iss, pSHost);
        }

        /// <summary>
        /// Create a new Runspacepool from an existing one. Used when a new module needs to be added to a runspacepool.
        /// Cannot add additional modules after runspacepool is created, so reset it.
        /// </summary>
        /// <param name="minRunspaces">Min num of threads</param>
        /// <param name="maxRunspaces">Max num of threads</param>
        /// <param name="initialSessionState">ISS to associate</param>
        /// <param name="pSHost">PSHost to associate</param>
        /// <returns>New Runspacepool</returns>
        internal static RunspacePool ResetRunspacePool(int minRunspaces, int maxRunspaces, InitialSessionState initialSessionState, PSHost pSHost)
        {
            return RunspaceFactory.CreateRunspacePool(minRunspaces, maxRunspaces, initialSessionState, pSHost);
        }

        /// <summary>
        /// Invoke the Proxyfunction and returns the Parameters passed
        /// The parameters passed can be anything, but essentially a variable with/without any experssions in it.
        /// Expression is not allowed in $Using, so try parameter binding using proxyfunction and return the resolved params.
        /// This can get tricky when on constrained runspace, User need to explicity specifiy the parameters in that case.
        /// </summary>
        /// <param name="proxyCommand">Proxy Command created</param>
        /// <param name="scriptToRun">Users actual command</param>
        /// <param name="cmdStr">Extracted command name </param>
        /// <param name="inputObject">Parameter value from pipeline</param>
        /// <returns>Parameters used on the command</returns>
        internal static IDictionary FindParams(FunctionInfo proxyCommand, ScriptBlock scriptToRun, string cmdStr, PSObject inputObject)
        {
            string convertedScript = "param($Inputobject) " + scriptToRun.ToString();
            convertedScript = convertedScript.Replace(cmdStr, proxyCommand.Name);
            ScriptBlock commandBlock = ScriptBlock.Create(convertedScript.Replace("$_", "$inputobject"));
            IDictionary boundParams = (Dictionary<string, object>)commandBlock.Invoke(inputObject).FirstOrDefault().BaseObject;
            return boundParams;
        }

        /// <summary>
        /// ModuleDetails class
        /// </summary>
        public class ModuleDetails
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ModuleDetails" /> class
            /// </summary>
            public ModuleDetails()
            {
                this.CommandStr = string.Empty;
                this.PSModule = string.Empty;
                this.PSSnapIn = string.Empty;
                this.Functions = new List<SessionStateFunctionEntry>();
                this.IsFromRemotingModule = false;
            }

            /// <summary>
            /// Command from which the module details were discovered
            /// </summary>
            public string CommandStr { get; set; }

            /// <summary>
            /// Module name
            /// </summary>
            public string PSModule { get; set; }

            /// <summary>
            /// If the command is from PSSnapin, the name of it.
            /// </summary>
            public string PSSnapIn { get; set; }

            /// <summary>
            /// Custom PS Functions used
            /// </summary>
            public List<SessionStateFunctionEntry> Functions { get; set; }

            /// <summary>
            /// true, if the command passed is from a remote powershell session
            /// </summary>
            public bool IsFromRemotingModule { get; set; }
        }
    }
}
