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

