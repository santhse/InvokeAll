namespace PSParallel
{
    using System;
    using System.Linq;
    using System.Management.Automation.Host;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Management.Automation.Language;
    using System.Collections.Generic;
    using System.Collections;
    using System.Collections.ObjectModel;
    using System.IO;

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

            if (cmdAsts?.FirstOrDefault() is CommandAst cmdElement)
            {
                cmd = cmdElement.CommandElements.First().ToString();
                var cmdScript = ScriptBlock.Create("Get-Command " + cmd + " | Select-Object -First 1");
                PSObject cmdDetails = (PSObject)cmdScript.InvokeReturnAsIs();
                return cmdDetails.BaseObject as CommandInfo;
            }
            cmd = "unknown";
            return null;
        }

        /// <summary>
        /// Create a Proxy Function for the command supplied.
        /// </summary>
        /// <param name="commandInfo"></param>
        /// <param name="debugStrings"></param>
        /// <returns></returns>
        internal static FunctionInfo CreateProxyFunction(CommandInfo commandInfo, out List<string> debugStrings)
        {
            debugStrings = new List<string>();
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

            debugStrings.Add(string.Format("Creating ProxyFunction {0}", proxyCommandName));
            
            try
            {
                PSObject proxyFunctionPSObj = ScriptBlock.Create("param($name, $script) New-Item -Path Function:Global:$name -Value $script -Erroraction Stop")
                    .Invoke(proxyCommandName, proxyScriptStr).FirstOrDefault();
                debugStrings.Add(string.Format("Created proxy command {0}", proxyCommandName));

                return proxyFunctionPSObj.BaseObject as FunctionInfo;
            }
            catch (ActionPreferenceStopException ae) when ((ae.ErrorRecord.Exception is PSArgumentException) && ae.ErrorRecord.Exception.Message.EndsWith("already exists."))
            {
                //Todo: throwterminatingerror here
                debugStrings.Add("ProxyCommand Already Exsits");
                string removeProxyFunction = string.Format("Remove-Item -Path Function:{0} -Force", proxyCommandName);
                ScriptBlock.Create(removeProxyFunction).Invoke();
                throw new Exception(string.Format("Proxy function {0} already exists. It is present from a previous failure of Invoke-all, the script tried to remove it. " +
                    "Simply re-run your command to see if it works, if not, please manually remove the function using {1}", proxyCommandName, removeProxyFunction));
            }

        }

        /// <summary>
        /// Create the Runspace pool.
        /// For remote runspaces, load the datatypes optionally to support serialization
        /// For local commands, load the modules and snapins required or requested
        /// </summary>
        /// <param name="commandInfo"></param>
        /// <param name="pSHost"></param>
        /// <param name="maxRunspaces"></param>
        /// <param name="debugStrings"></param>
        /// <param name="useRemotePS"></param>
        /// <param name="loadAllTypedata"></param>
        /// <param name="modules"></param>
        /// <param name="modulesPath"></param>
        /// <param name="snapIns"></param>
        /// <param name="variableEntries"></param>
        /// <returns></returns>
        internal static RunspacePool CreateRunspacePool(CommandInfo commandInfo, 
            PSHost pSHost, 
            int maxRunspaces, 
            out List<string> debugStrings, 
            PSSession useRemotePS, 
            bool loadAllTypedata, 
            string[] modules = null, string[] modulesPath = null, string[] snapIns = null, IList<SessionStateVariableEntry> variableEntries = null)
        {
            debugStrings = new List<string>();
            RunspaceConnectionInfo runspaceConnectionInfo = null;
            Hashtable modulePrivatedata = commandInfo.Module?.PrivateData as Hashtable;
            
            //special handling for remote PSsession commands
            if ((modulePrivatedata != null && modulePrivatedata.ContainsKey("ImplicitRemoting")) || useRemotePS != null)
            {
                if (useRemotePS != null)
                {
                    debugStrings.Add("Using the supplied remote PSSession");
                    runspaceConnectionInfo = useRemotePS.Runspace.ConnectionInfo;
                }
                else
                {
                    debugStrings.Add("Using remote PSSession to execute the command");
                    PSObject remotepSModule = ScriptBlock.Create(
                        string.Format("Get-Module {0}", commandInfo.ModuleName)).InvokeReturnAsIs() as PSObject;
                    if (remotepSModule.BaseObject is PSModuleInfo remotepSModuleInfo)
                    {
                        PSObject remotePs = ScriptBlock.Create(
                            string.Format("Get-PSSession | Where-Object{{$_.state -eq 'opened' -and (\"{0}\").Contains($_.ComputerName)}} | Select-Object -First 1", 
                            remotepSModuleInfo.Description)).InvokeReturnAsIs() as PSObject;

                        if (remotePs.BaseObject is PSSession remotePSSession)
                        {
                            runspaceConnectionInfo = remotePSSession.Runspace.ConnectionInfo;
                            if (modules != null || modulesPath != null)
                            {
                                debugStrings.Add("Modules were specified to load, but they will not be loaded as the command supplied is from a remote PSSession");
                            }
                        }
                        else
                        {
                            debugStrings.Add(string.Format("Command - Get-PSSession | Where-Object{{$_.state -eq 'opened' -and (\"{0}\").Contains($_.ComputerName)}} " +
                                "| Select-Object -First 1 - was ran to find the PSSession", remotepSModuleInfo.Description));
                            throw new Exception("Unable to find a PSSession to use. You may try passing the PSSession to use using Parameter -UseRemotePSSession");
                        }
                    }
                }
                debugStrings.Add(string.Format("Using connection info {0}", runspaceConnectionInfo.ComputerName));
                
                TypeTable typeTable = TypeTable.LoadDefaultTypeFiles();
                
                if(loadAllTypedata)
                {
                    Collection<PSObject> typeDatas = ScriptBlock.Create("Get-TypeData").Invoke();
                    foreach (PSObject typeData in typeDatas)
                    {
                        TypeData t = (TypeData)typeData.BaseObject;
                        try
                        {
                            typeTable.AddType(t);
                            debugStrings.Add(string.Format("Added typedata{0}", t.TypeName));
                        }
                        catch (Exception e)
                        {
                            debugStrings.Add(string.Format("Unable to add typeData {0}. Error {1}", t.TypeName, e.Message));
                        }
                    }
                }
                return RunspaceFactory.CreateRunspacePool(1, Environment.ProcessorCount, runspaceConnectionInfo, pSHost, typeTable);
            }

            InitialSessionState iss = InitialSessionState.CreateDefault2();
            List<string> modulesToLoad = new List<String>();
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

            switch (commandInfo.CommandType)
            {
                case CommandTypes.Cmdlet:
                    CmdletInfo cmdletInfo = commandInfo as CmdletInfo;
                    if(cmdletInfo.Module != null)
                    {
                        debugStrings.Add(string.Format("The command {0} is from Module {1}", commandInfo.Name, cmdletInfo.ModuleName));
                        modulesToLoad.Add(cmdletInfo.ModuleName);
                    }
                    if (cmdletInfo.PSSnapIn != null)
                    {
                        debugStrings.Add(string.Format("The command {0} is from PSSnapin {1}", commandInfo.Name, cmdletInfo.PSSnapIn.Name));
                        snapInsToLoad.Add(cmdletInfo.PSSnapIn.Name);
                    }
                    
                    break;
                case CommandTypes.Function:
                    FunctionInfo functionInfo = commandInfo as FunctionInfo;
                    if (functionInfo != null)
                    {
                        //Add the function definition anyway as the function can be local or from a module/file
                        iss.Commands.Add(new SessionStateFunctionEntry(functionInfo.Name, functionInfo.Definition));
                        if (functionInfo.ScriptBlock.File != null)
                        {
                            debugStrings.Add(string.Format("The command is a custom function {0} from file {1}", commandInfo.Name, functionInfo.ScriptBlock.File));
                            FileInfo scriptFileInfo = new FileInfo(functionInfo.ScriptBlock.File);
                            
                            //if the function is from a PS1 script, don't load it as a module. The script will be executed and will cause unintended results
                            if (!scriptFileInfo.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                            {
                                modulesToLoad.Add(functionInfo.ScriptBlock.File);
                            }
                        }
                    }
                    break;
                case CommandTypes.ExternalScript:
                    debugStrings.Add(string.Format("The command {0} is of type ExternalScript", commandInfo.Name));
                    break;
                default:
                    break;
            }

            if (modules?.Count() > 0 && modules.Contains("All", StringComparer.OrdinalIgnoreCase))
            {
                var modulesAvailable = ScriptBlock.Create("Get-Module -ListAvailable | Select-Object -ExpandProperty Name").Invoke();
                modulesToLoad.AddRange(from module in modulesAvailable select module.BaseObject as string);
                debugStrings.Add(string.Format("Loaded all the available modules on this computer, {0} modules found", modulesToLoad.Count));
            }

            if (modules?.Count() > 0 && modules.Contains("Loaded", StringComparer.OrdinalIgnoreCase))
            {
                var modulesLoaded = ScriptBlock.Create("Get-Module | Select-Object -ExpandProperty Name").Invoke();
                modulesToLoad.AddRange(from module in modulesLoaded select module.BaseObject as string);
                debugStrings.Add(string.Format("Loaded the modules loaded on current Runspace, {0} modules found", modulesLoaded.Count));
            }
            debugStrings.Add("Loading Modules:");
            debugStrings.AddRange(modulesToLoad);
            iss.ImportPSModule(modulesToLoad.ToArray());
            
            snapInsToLoad.ForEach(s => iss.ImportPSSnapIn(s, out pSSnapInException));

            if (variableEntries != null)
                iss.Variables.Add(variableEntries);

            return RunspaceFactory.CreateRunspacePool(1, maxRunspaces, iss, pSHost);
        }

        /// <summary>
        /// The parameters passed can be anything, but essentially a variable with/without any experssions in it.
        /// Expression is not allowed in $Using, so try parameter binding using proxyfunction and return the resolved params.
        /// This can get tricky when on constrained runspace, User need to explicity specifiy the parameters in that case.        /// 
        /// </summary>
        /// <param name="proxyCommand"></param>
        /// <param name="scriptToRun"></param>
        /// <param name="cmdStr"></param>
        /// <returns></returns>
        internal static IDictionary FindParams(FunctionInfo proxyCommand, ScriptBlock scriptToRun, string cmdStr, PSObject inputObject)
        {
            string convertedScript = "param($Inputobject) " + scriptToRun.ToString();
            convertedScript = convertedScript.Replace(cmdStr, proxyCommand.Name);
            ScriptBlock commandBlock = ScriptBlock.Create(convertedScript.Replace("$_", "$inputobject"));
            IDictionary boundParams = (Dictionary<String, Object>)commandBlock.Invoke(inputObject).FirstOrDefault().BaseObject;
            return boundParams;
        }
    }
}
