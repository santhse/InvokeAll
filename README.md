# InvokeAll
## Multi-thread Powershell commands easily
This powershell cmdlet is a binary version of the poweshell script i had published before - https://blogs.technet.microsoft.com/santhse/invokeall/

Download the module file from releases tab and import it on the powershell window to use it.                                     
 ---------------------- OR ------------------------                                                                             
 You can also install it from Powershell gallery by running command, 
    
    Install-Module Invokeall
Commands that are exported from the Module:

    Invoke-All
    Get-InvokeAllJobsStatus
    Receive-InvokeAllJobs
 
Examples:
        
    Get-Mailbox | Invoke-All { Get-MailboxStatistics -Identity $_.windowsEmailaddress } -MaxThreads 10 -BatchSize 50 -Force

    $output = $servers | Invoke-all { Get-Networkdetails.ps1 -Server $_ -IpConfig } -ModulestoLoad MyNetworkModule, c:\Utilities\LogHelper.psm1 -CopyLocalVariables -Force -ReturnasJobObject

This module also supports async way of running the commands as shown below:



![InvokeAll-AsyncExample](https://user-images.githubusercontent.com/34683971/64614931-66554280-d3d1-11e9-8d86-7e87011e6189.png)

Cmdlet: Invoke-All
PARAMETERS

    -ScriptToRun <scriptblock>

        Scriptblock to execute in parallel.
        Usually it is the 2nd Pipeline block, wrap your command as shown in the below examples and it should work fine.
        You cannot use alias or external scripts. If you are using a function from a custom script, please make sure it is an Advance function or with Param blocks defined properly.

    -UseRemotePSSession <psobject>
        When specified, Remote Runspace opened on the host will be used

    -Async
        When specified, job Objects are created and returned immediately. Jobs will continue to run in the background, Use Get-InvokeAllJobsStatus and Receive-InvokeAllJobs to check and collect the results.

    -AppendJobNameToResult
        When specified, JobName is appended to the result object

    -BatchSize <int>
        BatchSize controls the number of jobs to run before waiting for one of them to complete

    -CopyLocalVariables
        Copies the local powershell variable names and its values to the Runspace. DON'T modify or loop through variables on the jobs as they are not thread-safe

    -Force
        Cmdlet, by default waits for 30 Seconds for the first Job to complete before Queuing rest of the jobs from Pipeline. Use -Force to skip this check

    -InputObject <psobject>
        Run script against these specified objects. Takes input from Pipeline or when specified explicitly.

    -LoadAllTypeDatas
        When specified, The typedata is not loaded in to the RunspacePool, Specify if loading typedata is delaying the creation of RunspacePool

    -MaxThreads <int>
        Number of threads to be executed in parallel, by default one thread per logical CPU

    -ModulestoLoad <string[]>
        Name of PS Modules or the Full path of the Modules (comma seperated) to load in to the Runspace for the command to work. Specifiy 'All' to load all the possible modules or 'Loaded' to load the currently loaded modules

    -NoFileLogging
        Specifiy this switch if Logging to a file should be skipped

    -PSSnapInsToLoad <string[]>
        Name of PSSnapins (comma seperated) to load in to the Runspace for the command to work.

    -ProgressBarStage <string>
        Provide any useful message to indicate the stage to the user

    -Quiet
        When specified, the Progress bar is not shown

    -ReturnasJobObject
        When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job



Cmdlet:Receive-InvokeAllJobs

PARAMETERS

    -AppendJobNameToResult
        When specified, JobName is appended to the result object

    -JobObjects <InvokeAll+Job[]>
        PSParallel Job objects returned from Invoke-All -ASync Switch

    -ReturnasJobObject
        When specified, instead of returing the Job results, returns the invokeall Job objects itself. It is useful when you want to access the Streams or other details of each job

    -Wait
        Wait for all Jobs to complete and receive the Output
