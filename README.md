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
    New-InvokeAllRunspacePool
    
About the Module:

--> InvokeAll.dll is a Powershell module written in c#. It uses .Net Tasks and powershell’s Runspacepool feature to multithread and execute powershell commands or scripts.

--> The module also supports executing commands asynchronously and the results can be collected at a later time. There are multiple switches on the cmdlet to control various features which makes its easier to customize according to each needs.

--> For each input object from the pipeline, the module creates a powershell instance and submits it to the runspacepool aka job. (Mandatory to pass the Input as a pipeline object)

--> Number of instances that are being Queued at a time is controlled by the batch size. The remaining instances from the pipeline are queued as a job in the batch completes. This is to reduce to memory footprint of the jobs while the command executes.

--> Number of instances being actively processed is controlled by the Runspacepool and the Maxthreads parameter.

--> The module creates a log file in %temp% directory for any troubleshooting.

 
Examples:
        
    Get-Mailbox | Invoke-All { Get-MailboxStatistics -Identity $_.windowsEmailaddress } -MaxThreads 10 -BatchSize 50 -Force

    $output = $servers | Invoke-all { Get-Networkdetails.ps1 -Server $_ -IpConfig } -ModulestoLoad MyNetworkModule, c:\Utilities\LogHelper.psm1 -CopyLocalVariables -Force -ReturnasJobObject

This module also supports async way of running the commands as shown below:



![InvokeAll-AsyncExample](https://user-images.githubusercontent.com/34683971/67493378-b3cefb80-f66f-11e9-85ed-30fa1c7df338.png)

Also: Reusing the runspace on a script:
## Creating the runspace to reuse it.

    $runspace = New-InvokeAllRunspacePool -CommandName Get-MRSrequest -LoadAllTypeDatas

    $mailboxStats += $mbx | Invoke-All { Get-MailboxStatistics -Identity "$_.MailboxGuid" -IncludeSoftDeletedRecipients } `
        -ProgressBarStage "Collecting MailboxStat details for all Mailbox Locations"`
        -RunspaceToUse $runspace `
        -BatchSize $locations.Count `
        -Force
    $folderStats += $mbx | Invoke-All { Get-MailboxFolderStatistics -Identity $_ -IncludeSoftDeletedRecipients -IncludeOldestAndNewestItems -FolderScope All } `
        -ProgressBarStage "Collecting Mailbox Folder stats" `
        -AppendJobNameToResult `
        -RunspaceToUse $runspace `
        -BatchSize $locations.Count `
        -Force | `
        select @{Name = 'MailboxGuid';e={$_.PSJobName}},*;

## Removing the Runspace after use
       $runspace.close()
       $runspace.Dispose()

## Limitations:
Because of the Runspacepool limitation, this module cannot support a script that will use Remote PS Session commands. In a runspacepool, we cannot create a Runspace that could load remote PS connection as well as local modules. 
