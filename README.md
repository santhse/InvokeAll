# InvokeAll
Multi-thread Powershell commands easily
This powershell cmdlet is a binary version of the poweshell script i had published before - https://blogs.technet.microsoft.com/santhse/invokeall/

Download the module file Invokeall_V_1.0.dll and import it on the powershell window to use it.

Examples:
Get-Mailbox | Invoke-All { Get-MailboxStatistics -Identity $_.windowsEmailaddress } -BatchSize 50 -Force


NAME
    Invoke-All

PARAMETERS

    -BatchSize <int>
        BatchSize controls the number of jobs to run before waiting for one of them to complete
     
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

    -ScriptToRun <scriptblock>

        Scriptblock to execute in parallel.
        Usually it is the 2nd Pipeline block, wrap your command as shown in the below examples and it should work fine.
        You cannot use alias or external scripts. If you are using a function from a custom script, please make sure it is an Advance function or with Param blocks defined properly.
        
    -CopyLocalVariables
        Copies the local powershell variable names and its values to the Runspace. DON'T modify or loop through variables on the jobs as they are not thread-safe
        
    -Force
        Cmdlet, by default waits for 30 Seconds for the first Job to complete before Queuing rest of the jobs from Pipeline. Use -Force to skip this check

    -InputObject <psobject>
        Run script against these specified objects. Takes input from Pipeline or when specified explicitly.


    -UseRemotePSSession <psobject>
        When specified, Remote Runspace opened on the host will be used

    <CommonParameters>
        This cmdlet supports the common parameters: Verbose, Debug,
        ErrorAction, ErrorVariable, WarningAction, WarningVariable,
        OutBuffer, PipelineVariable, and OutVariable. For more information, see
        about_CommonParameters (https:/go.microsoft.com/fwlink/?LinkID=113216).

