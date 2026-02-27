using Phantom.Models;

namespace Phantom.Services;

internal static class RequestedTweaksCatalog
{
    public static IReadOnlyList<TweakDefinition> CreateRequestedTweaks()
    {
        return
        [
            T("center-taskbar-items", "Center taskbar items", "Centers taskbar buttons/icons.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                if((Get-ItemProperty -Path $p -Name TaskbarAl -ErrorAction Stop).TaskbarAl -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Type DWord -Value 0
                """),

            T("disable-cross-device-resume", "Cross-Device Resume", "Disables Shared Experiences / cross-device resume.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP'
                $a=(Get-ItemProperty -Path $p -Name CdpSessionUserAuthzPolicy -ErrorAction Stop).CdpSessionUserAuthzPolicy
                $b=(Get-ItemProperty -Path $p -Name RomeSdkChannelUserAuthzPolicy -ErrorAction Stop).RomeSdkChannelUserAuthzPolicy
                if($a -eq 0 -and $b -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP' -Name CdpSessionUserAuthzPolicy -Type DWord -Value 0
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP' -Name RomeSdkChannelUserAuthzPolicy -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP' -Name CdpSessionUserAuthzPolicy -Type DWord -Value 1
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP' -Name RomeSdkChannelUserAuthzPolicy -Type DWord -Value 1
                """),

            T("dark-theme-windows", "Dark Theme for Windows", "Forces dark mode for apps and system.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
                $a=(Get-ItemProperty -Path $p -Name AppsUseLightTheme -ErrorAction Stop).AppsUseLightTheme
                $b=(Get-ItemProperty -Path $p -Name SystemUsesLightTheme -ErrorAction Stop).SystemUsesLightTheme
                if($a -eq 0 -and $b -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Type DWord -Value 0
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme -Type DWord -Value 0
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Type DWord -Value 1
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme -Type DWord -Value 1
                """),

            T("detailed-bsod", "Detailed BSoD", "Shows detailed parameters on stop errors.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
                if((Get-ItemProperty -Path $p -Name DisplayParameters -ErrorAction Stop).DisplayParameters -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -Name DisplayParameters -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -Name DisplayParameters -Type DWord -Value 0
                """),

            T("disable-multiplane-overlay", "Disable Multiplane Overlay", "Disables MPO to mitigate flickering/rendering issues.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
                if((Get-ItemProperty -Path $p -Name OverlayTestMode -ErrorAction Stop).OverlayTestMode -eq 5){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' -Name OverlayTestMode -Type DWord -Value 5
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' -Name OverlayTestMode -ErrorAction Stop
                """),

            T("disable-mouse-acceleration", "Mouse Acceleration", "Disables enhanced pointer precision.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Control Panel\Mouse'
                $a=(Get-ItemProperty -Path $p -Name MouseSpeed -ErrorAction Stop).MouseSpeed
                $b=(Get-ItemProperty -Path $p -Name MouseThreshold1 -ErrorAction Stop).MouseThreshold1
                $c=(Get-ItemProperty -Path $p -Name MouseThreshold2 -ErrorAction Stop).MouseThreshold2
                if($a -eq '0' -and $b -eq '0' -and $c -eq '0'){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseSpeed -Value '0'
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseThreshold1 -Value '0'
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseThreshold2 -Value '0'
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseSpeed -Value '1'
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseThreshold1 -Value '6'
                Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name MouseThreshold2 -Value '10'
                """),

            T("disable-new-outlook", "New Outlook", "Hides/disables the New Outlook toggle.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Office\16.0\Outlook\Options\General'
                if((Get-ItemProperty -Path $p -Name HideNewOutlookToggle -ErrorAction Stop).HideNewOutlookToggle -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Office\16.0\Outlook\Options\General' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Office\16.0\Outlook\Options\General' -Name HideNewOutlookToggle -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Office\16.0\Outlook\Options\General' -Name HideNewOutlookToggle -ErrorAction Stop
                """),

            T("numlock-on-startup", "NumLock on Startup", "Turns NumLock on at sign-in.", RiskTier.Basic, "HKU", true,
                """
                $p='Registry::HKEY_USERS\.DEFAULT\Control Panel\Keyboard'
                if((Get-ItemProperty -Path $p -Name InitialKeyboardIndicators -ErrorAction Stop).InitialKeyboardIndicators -eq '2'){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'Registry::HKEY_USERS\.DEFAULT\Control Panel\Keyboard' -Name InitialKeyboardIndicators -Value '2'
                """,
                """
                Set-ItemProperty -Path 'Registry::HKEY_USERS\.DEFAULT\Control Panel\Keyboard' -Name InitialKeyboardIndicators -Value '0'
                """),

            T("disable-start-recommendations", "Recommendations in Start Menu", "Disables recommended content in Start.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                if((Get-ItemProperty -Path $p -Name Start_IrisRecommendations -ErrorAction Stop).Start_IrisRecommendations -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name Start_IrisRecommendations -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name Start_IrisRecommendations -Type DWord -Value 1
                """),

            T("remove-settings-home-page", "Remove Settings Home Page", "Hides Settings home page via policy.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer'
                if((Get-ItemProperty -Path $p -Name SettingsPageVisibility -ErrorAction Stop).SettingsPageVisibility -eq 'hide:home'){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name SettingsPageVisibility -Type String -Value 'hide:home'
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name SettingsPageVisibility -ErrorAction Stop
                """),

            T("enable-s3-sleep", "S3 Sleep", "Disables modern standby override to prefer S3 sleep where supported.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Control\Power'
                if((Get-ItemProperty -Path $p -Name PlatformAoAcOverride -ErrorAction Stop).PlatformAoAcOverride -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -Name PlatformAoAcOverride -Type DWord -Value 0
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -Name PlatformAoAcOverride -ErrorAction Stop
                """),

            T("hide-search-button-taskbar", "Search Button in Taskbar", "Hides search button on taskbar.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Search'
                if((Get-ItemProperty -Path $p -Name SearchboxTaskbarMode -ErrorAction Stop).SearchboxTaskbarMode -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode -Type DWord -Value 1
                """),

            T("disable-sticky-keys", "Sticky Keys", "Disables Sticky Keys keyboard shortcut prompts.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Control Panel\Accessibility\StickyKeys'
                if((Get-ItemProperty -Path $p -Name Flags -ErrorAction Stop).Flags -eq '506'){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Control Panel\Accessibility\StickyKeys' -Name Flags -Value '506'
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Control Panel\Accessibility\StickyKeys' -Name Flags -Value '510'
                """),

            T("hide-task-view-button", "Task View Button in Taskbar", "Hides Task View button.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                if((Get-ItemProperty -Path $p -Name ShowTaskViewButton -ErrorAction Stop).ShowTaskViewButton -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name ShowTaskViewButton -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name ShowTaskViewButton -Type DWord -Value 1
                """),

            T("verbose-messages-during-logon", "Verbose Messages During Logon", "Shows detailed status during sign-in and startup.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
                if((Get-ItemProperty -Path $p -Name VerboseStatus -ErrorAction Stop).VerboseStatus -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name VerboseStatus -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name VerboseStatus -ErrorAction Stop
                """),

            T("add-ultimate-performance-profile", "Add and Activate Ultimate Performance Profile", "Adds and sets Ultimate Performance plan.", RiskTier.Advanced, "System", true,
                """
                $guid='e9a42b02-d5df-448d-aa00-03f14749eb61'
                $active = (powercfg /GetActiveScheme 2>$null | Out-String)
                if($active -match $guid -or $active -match 'Ultimate Performance'){'Applied'} else {'Not Applied'}
                """,
                """
                $templateGuid='e9a42b02-d5df-448d-aa00-03f14749eb61'
                $ultimateGuid = $null
                $plans = (powercfg /L 2>$null | Out-String)
                $ultimateLine = ($plans -split "`r?`n" | Where-Object { $_ -match 'Ultimate Performance' } | Select-Object -First 1)
                if($ultimateLine -and $ultimateLine -match '([0-9a-fA-F\-]{36})'){
                  $ultimateGuid = $matches[1]
                }

                if(-not $ultimateGuid){
                  $dupOut = (powercfg -duplicatescheme $templateGuid 2>&1 | Out-String)
                  $plans = (powercfg /L 2>$null | Out-String)
                  $ultimateLine = ($plans -split "`r?`n" | Where-Object { $_ -match 'Ultimate Performance' } | Select-Object -First 1)
                  if($ultimateLine -and $ultimateLine -match '([0-9a-fA-F\-]{36})'){
                    $ultimateGuid = $matches[1]
                  } elseif($dupOut -match '([0-9a-fA-F\-]{36})') {
                    $ultimateGuid = $matches[1]
                  }
                }

                if(-not $ultimateGuid){
                  throw "Ultimate Performance plan is unavailable on this system."
                }

                $setOut = (powercfg /setactive $ultimateGuid 2>&1 | Out-String)
                $activeNow = (powercfg /GetActiveScheme 2>$null | Out-String)
                if($activeNow -match $ultimateGuid -or $activeNow -match 'Ultimate Performance'){
                  Write-Output 'Applied'
                  return
                }
                throw ("Failed to activate Ultimate Performance plan. " + $setOut.Trim())
                """,
                """
                powercfg /setactive SCHEME_BALANCED 2>$null | Out-Null
                """),

            T("remove-ultimate-performance-profile", "Remove Ultimate Performance Profile", "Removes Ultimate Performance plan.", RiskTier.Advanced, "System", true,
                """
                $plans = (powercfg /L 2>$null | Out-String)
                if($plans -match 'Ultimate Performance'){'Not Applied'} else {'Applied'}
                """,
                """
                powercfg /setactive SCHEME_BALANCED 2>$null | Out-Null
                $plans = (powercfg /L 2>$null | Out-String)
                $ultimateLines = ($plans -split "`r?`n" | Where-Object { $_ -match 'Ultimate Performance' })
                foreach($line in $ultimateLines){
                  if($line -match '([0-9a-fA-F\-]{36})'){
                    powercfg /delete $matches[1] 2>$null | Out-Null
                  }
                }
                """,
                """
                $guid='e9a42b02-d5df-448d-aa00-03f14749eb61'
                $plans = (powercfg /L 2>$null | Out-String)
                if($plans -notmatch 'Ultimate Performance'){
                  powercfg -duplicatescheme $guid 2>$null | Out-Null
                }
                """),

            T("create-restore-point", "Create Restore Point", "Creates a system restore point.", RiskTier.Advanced, "System", false,
                """
                try {
                  $existing = Get-ComputerRestorePoint -ErrorAction Stop | Where-Object { $_.Description -eq 'Phantom tweak restore point' } | Select-Object -First 1
                  if ($null -ne $existing) { 'Applied' } else { 'Not Applied' }
                } catch {
                  'Not Applied'
                }
                """,
                """
                $existing = $null
                try {
                  $existing = Get-ComputerRestorePoint -ErrorAction Stop | Where-Object { $_.Description -eq 'Phantom tweak restore point' } | Select-Object -First 1
                } catch {}
                if ($null -ne $existing) {
                  Write-Output 'Applied'
                  return
                }
                Enable-ComputerRestore -Drive "$($env:SystemDrive)\" -ErrorAction Continue
                Checkpoint-Computer -Description 'Phantom tweak restore point' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
                Write-Output 'Applied'
                """,
                "Write-Output 'No undo action for restore point creation.'"),

            T("delete-temporary-files", "Delete Temporary Files", "Deletes temporary files from common temp locations.", RiskTier.Advanced, "System", false,
                "'Not Applied'",
                """
                $targets=@($env:TEMP, "$env:SystemRoot\Temp")
                foreach($t in $targets){
                  if(Test-Path $t){
                    Get-ChildItem -Path $t -Force -ErrorAction Continue | Remove-Item -Recurse -Force -ErrorAction Continue
                  }
                }
                Write-Output 'Temporary files cleanup attempted.'
                """,
                "Write-Output 'No undo action for temporary file deletion.'"),

            T("disable-activity-history", "Disable Activity History", "Disables activity history publishing/upload.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
                $a=(Get-ItemProperty -Path $p -Name PublishUserActivities -ErrorAction Stop).PublishUserActivities
                $b=(Get-ItemProperty -Path $p -Name UploadUserActivities -ErrorAction Stop).UploadUserActivities
                if($a -eq 0 -and $b -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name PublishUserActivities -Type DWord -Value 0
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name UploadUserActivities -Type DWord -Value 0
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name PublishUserActivities -ErrorAction Stop
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name UploadUserActivities -ErrorAction Stop
                """),

            T("disable-explorer-auto-folder-discovery", "Disable Explorer Automatic Folder Discovery", "Disables automatic folder type discovery.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell'
                if((Get-ItemProperty -Path $p -Name FolderType -ErrorAction Stop).FolderType -eq 'NotSpecified'){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell' -Name FolderType -Type String -Value 'NotSpecified'
                """,
                """
                Remove-ItemProperty -Path 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell' -Name FolderType -ErrorAction Stop
                """),

            T("disable-location-tracking", "Disable Location Tracking", "Disables location tracking policy.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors'
                if((Get-ItemProperty -Path $p -Name DisableLocation -ErrorAction Stop).DisableLocation -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors' -Name DisableLocation -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors' -Name DisableLocation -ErrorAction Stop
                """),

            T("disable-powershell7-telemetry", "Disable Powershell 7 Telemetry", "Sets POWERSHELL_TELEMETRY_OPTOUT for machine/user scope.", RiskTier.Advanced, "System", true,
                "if($env:POWERSHELL_TELEMETRY_OPTOUT -eq '1'){'Applied'} else {'Not Applied'}",
                """
                [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT','1','Machine')
                [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT','1','User')
                """,
                """
                [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT',$null,'Machine')
                [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT',$null,'User')
                """),

            T("disable-wpbt", "Disable Windows Platform Binary Table (WPBT)", "Disables WPBT execution policy via registry.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
                if((Get-ItemProperty -Path $p -Name DisableWpbtExecution -ErrorAction Stop).DisableWpbtExecution -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name DisableWpbtExecution -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name DisableWpbtExecution -ErrorAction Stop
                """),

            T("enable-end-task-with-right-click", "Enable End Task With Right Click", "Shows taskbar End Task entry.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings'
                if((Get-ItemProperty -Path $p -Name TaskbarEndTask -ErrorAction Stop).TaskbarEndTask -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings' -Name TaskbarEndTask -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings' -Name TaskbarEndTask -Type DWord -Value 0
                """),

            T("remove-widgets", "Remove Widgets", "Disables taskbar Widgets button and feed policy.", RiskTier.Advanced, "Both", true,
                """
                $a='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                $b='HKLM:\SOFTWARE\Policies\Microsoft\Dsh'
                $u=(Get-ItemProperty -Path $a -Name TaskbarDa -ErrorAction Stop).TaskbarDa
                $m=(Get-ItemProperty -Path $b -Name AllowNewsAndInterests -ErrorAction Stop).AllowNewsAndInterests
                if($u -eq 0 -and $m -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarDa -Type DWord -Value 0
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name AllowNewsAndInterests -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarDa -Type DWord -Value 1
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name AllowNewsAndInterests -ErrorAction Stop
                """),

            T("run-disk-cleanup", "Run Disk Cleanup", "Runs built-in Disk Cleanup.", RiskTier.Advanced, "System", false,
                "'Not Applied'",
                "cleanmgr.exe /VERYLOWDISK",
                "Write-Output 'No undo action for disk cleanup.'"),

            T("set-services-to-manual", "Set Services to Manual", "Sets broad non-critical services to manual with safety overrides.", RiskTier.Dangerous, "System", false,
                """
                $targets='DiagTrack','XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc','wuauserv'
                $ok=$true
                foreach($n in $targets){
                  $svc=Get-Service -Name $n -ErrorAction Ignore
                  if($null -eq $svc){continue}
                  if($n -eq 'DiagTrack' -and $svc.StartType -ne 'Disabled'){$ok=$false}
                  if($n -ne 'DiagTrack' -and $svc.StartType -ne 'Manual'){$ok=$false}
                }
                if($ok){'Applied'} else {'Not Applied'}
                """,
                """
                function Set-PhantomServiceType {
                  param([string]$Name,[string]$Type)
                  $svc=Get-Service -Name $Name -ErrorAction Ignore
                  if($null -eq $svc){return}
                  switch($Type){
                    'AutomaticDelayedStart' {
                      Set-Service -Name $Name -StartupType Automatic -ErrorAction Stop
                      sc.exe config $Name start= delayed-auto | Out-Null
                    }
                    'Automatic' { Set-Service -Name $Name -StartupType Automatic -ErrorAction Stop }
                    'Disabled' { Set-Service -Name $Name -StartupType Disabled -ErrorAction Stop }
                    default { Set-Service -Name $Name -StartupType Manual -ErrorAction Stop }
                  }
                }

                $manual=@('ALG','AppMgmt','AppReadiness','Appinfo','AxInstSV','BDESVC','BTAGService','CDPSvc','COMSysApp','CertPropSvc','CscService','DevQueryBroker','DeviceAssociationService','DeviceInstall','DisplayEnhancementService','EFS','EapHost','FDResPub','FrameServer','FrameServerMonitor','GraphicsPerfSvc','HvHost','IKEEXT','InstallService','IpxlatCfgSvc','KtmRm','LicenseManager','LxpSvc','MSDTC','MSiSCSI','McpManagementService','MicrosoftEdgeElevationService','NaturalAuthentication','NcaSvc','NcbService','NcdAutoSetup','NetSetupSvc','Netman','NlaSvc','PcaSvc','PeerDistSvc','PerfHost','PhoneSvc','PlugPlay','PolicyAgent','PrintNotify','PushToInstall','QWAVE','RasAuto','RasMan','RetailDemo','RmSvc','RpcLocator','SCPolicySvc','SCardSvr','SDRSVC','SEMgrSvc','SNMPTRAP','SNMPTrap','SSDPSRV','ScDeviceEnum','SensorDataService','SensorService','SensrSvc','SessionEnv','SharedAccess','SmsRouter','SstpSvc','StiSvc','TapiSrv','TermService','TieringEngineService','TokenBroker','TroubleshootingSvc','TrustedInstaller','UmRdpService','UsoSvc','VSS','VaultSvc','W32Time','WEPHOSTSVC','WFDSConMgrSvc','WMPNetworkSvc','WManSvc','WPDBusEnum','WSAIFabricSvc','WalletService','WarpJITSvc','WbioSrvc','WdiServiceHost','WdiSystemHost','WebClient','Wecsvc','WerSvc','WiaRpc','WinRM','WpcMonSvc','WpnService','autotimesvc','bthserv','camsvc','cloudidsvc','dcsvc','defragsvc','diagsvc','dmwappushservice','dot3svc','edgeupdate','edgeupdatem','fdPHost','fhsvc','hidserv','icssvc','lfsvc','lltdsvc','lmhosts','netprofm','perceptionsimulation','pla','seclogon','smphost','svsvc','swprv','upnphost','vds','vmicguestinterface','vmicheartbeat','vmickvpexchange','vmicrdv','vmicshutdown','vmictimesync','vmicvmsession','vmicvss','wbengine','wcncsvc','webthreatdefsvc','wercplsupport','wisvc','wlidsvc','wlpasvc','wmiApSrv','workfolderssvc','wuauserv')
                $automatic=@('AudioEndpointBuilder','AudioSrv','Audiosrv','BthAvctpSvc','CryptSvc','DPS','Dhcp','DispBrokerDesktopSvc','EventLog','EventSystem','FontCache','KeyIso','LanmanServer','LanmanWorkstation','Power','ProfSvc','SENS','SamSs','ShellHWDetection','Spooler','SysMain','Themes','UserManager','Wcmsvc','Winmgmt','iphlpsvc','nsi')
                $disabled=@('AppVClient','AssignedAccessManagerSvc','DiagTrack','DialogBlockingService','NetTcpPortSharing','RemoteAccess','RemoteRegistry','UevAgentService','shpamsvc','ssh-agent','tzautoupdate')
                $delayed=@('BITS','MapsBroker','WSearch')

                foreach($n in $manual){Set-PhantomServiceType -Name $n -Type 'Manual'}
                foreach($n in $automatic){Set-PhantomServiceType -Name $n -Type 'Automatic'}
                foreach($n in $disabled){Set-PhantomServiceType -Name $n -Type 'Disabled'}
                foreach($n in $delayed){Set-PhantomServiceType -Name $n -Type 'AutomaticDelayedStart'}
                Write-Output 'Service profile applied.'
                """,
                "Write-Output 'Manual service profile is intentionally one-way; restore manually if needed.'",
                true),

            T("adobe-network-block", "Adobe Network Block", "Blocks common Adobe endpoints via hosts entries.", RiskTier.Dangerous, "System", true,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                if((Get-Content -Path $hosts -ErrorAction Stop | Select-String -SimpleMatch '# PHANTOM_ADOBE_BLOCK').Count -gt 0){'Applied'} else {'Not Applied'}
                """,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                $marker='# PHANTOM_ADOBE_BLOCK'
                $entries=@('0.0.0.0 activate.adobe.com','0.0.0.0 practivate.adobe.com','0.0.0.0 lm.licenses.adobe.com','0.0.0.0 na1r.services.adobe.com')
                $content=if(Test-Path $hosts){Get-Content -Path $hosts -ErrorAction Stop}else{@()}
                $filtered=$content | Where-Object {$_ -notmatch 'PHANTOM_ADOBE_BLOCK'}
                foreach($line in $entries){$filtered += "$line $marker"}
                Set-Content -Path $hosts -Value $filtered -Encoding Ascii -Force
                """,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                $content=Get-Content -Path $hosts -ErrorAction Stop
                $filtered=$content | Where-Object {$_ -notmatch 'PHANTOM_ADOBE_BLOCK'}
                Set-Content -Path $hosts -Value $filtered -Encoding Ascii -Force
                """),

            T("block-razer-software-installs", "Block Razer Software Installs", "Blocks common Razer download endpoints.", RiskTier.Dangerous, "System", true,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                if((Get-Content -Path $hosts -ErrorAction Stop | Select-String -SimpleMatch '# PHANTOM_RAZER_BLOCK').Count -gt 0){'Applied'} else {'Not Applied'}
                """,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                $marker='# PHANTOM_RAZER_BLOCK'
                $entries=@('0.0.0.0 rzr.to','0.0.0.0 assets.razerzone.com','0.0.0.0 dl.razerzone.com','0.0.0.0 synapse.razer.com')
                $content=if(Test-Path $hosts){Get-Content -Path $hosts -ErrorAction Stop}else{@()}
                $filtered=$content | Where-Object {$_ -notmatch 'PHANTOM_RAZER_BLOCK'}
                foreach($line in $entries){$filtered += "$line $marker"}
                Set-Content -Path $hosts -Value $filtered -Encoding Ascii -Force
                """,
                """
                $hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                $content=Get-Content -Path $hosts -ErrorAction Stop
                $filtered=$content | Where-Object {$_ -notmatch 'PHANTOM_RAZER_BLOCK'}
                Set-Content -Path $hosts -Value $filtered -Encoding Ascii -Force
                """),

            T("brave-debloat", "Brave Debloat", "Applies Brave policy keys to disable sponsored/extra features.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Policies\BraveSoftware\Brave'
                if((Get-ItemProperty -Path $p -Name BraveRewardsDisabled -ErrorAction Stop).BraveRewardsDisabled -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                $p='HKLM:\SOFTWARE\Policies\BraveSoftware\Brave'
                New-Item -Path $p -Force | Out-Null
                Set-ItemProperty -Path $p -Name BraveRewardsDisabled -Type DWord -Value 1
                Set-ItemProperty -Path $p -Name BraveWalletDisabled -Type DWord -Value 1
                Set-ItemProperty -Path $p -Name BraveAIChatEnabled -Type DWord -Value 0
                Set-ItemProperty -Path $p -Name BackgroundModeEnabled -Type DWord -Value 0
                """,
                """
                $p='HKLM:\SOFTWARE\Policies\BraveSoftware\Brave'
                Remove-ItemProperty -Path $p -Name BraveRewardsDisabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name BraveWalletDisabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name BraveAIChatEnabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name BackgroundModeEnabled -ErrorAction Stop
                """),

            T("disable-fullscreen-optimizations", "Disable Fullscreen Optimizations", "Disables fullscreen optimization behavior in GameDVR pipeline.", RiskTier.Advanced, "HKCU", true,
                """
                $p='HKCU:\System\GameConfigStore'
                if((Get-ItemProperty -Path $p -Name GameDVR_FSEBehaviorMode -ErrorAction Stop).GameDVR_FSEBehaviorMode -eq 2){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\System\GameConfigStore' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_FSEBehavior -Type DWord -Value 2
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_FSEBehaviorMode -Type DWord -Value 2
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_HonorUserFSEBehaviorMode -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_FSEBehavior -Type DWord -Value 0
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_FSEBehaviorMode -Type DWord -Value 0
                Set-ItemProperty -Path 'HKCU:\System\GameConfigStore' -Name GameDVR_HonorUserFSEBehaviorMode -Type DWord -Value 0
                """),

            T("disable-ipv6", "Disable IPv6", "Disables IPv6 stack preference system-wide.", RiskTier.Dangerous, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters'
                if((Get-ItemProperty -Path $p -Name DisabledComponents -ErrorAction Stop).DisabledComponents -eq 255){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters' -Name DisabledComponents -Type DWord -Value 255
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters' -Name DisabledComponents -ErrorAction Stop
                """),

            T("disable-notification-tray-calendar", "Disable Notification Tray/Calendar", "Disables Action Center / notification center UI.", RiskTier.Advanced, "HKCU", true,
                """
                $p='HKCU:\Software\Policies\Microsoft\Windows\Explorer'
                if((Get-ItemProperty -Path $p -Name DisableNotificationCenter -ErrorAction Stop).DisableNotificationCenter -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' -Name DisableNotificationCenter -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' -Name DisableNotificationCenter -ErrorAction Stop
                """),

            T("disable-storage-sense", "Disable Storage Sense", "Turns off Storage Sense automatic cleanup.", RiskTier.Advanced, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy'
                if((Get-ItemProperty -Path $p -Name 01 -ErrorAction Stop).'01' -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy' -Name 01 -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy' -Name 01 -Type DWord -Value 1
                """),

            T("disable-teredo", "Disable Teredo", "Disables Teredo tunneling interface.", RiskTier.Advanced, "System", true,
                "if((netsh interface teredo show state) -match 'disabled'){'Applied'} else {'Not Applied'}",
                "netsh interface teredo set state disabled",
                "netsh interface teredo set state default"),

            T("edge-debloat", "Edge Debloat", "Applies Edge policies to reduce background/promotional features.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Edge'
                if((Get-ItemProperty -Path $p -Name HubsSidebarEnabled -ErrorAction Stop).HubsSidebarEnabled -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Edge'
                New-Item -Path $p -Force | Out-Null
                Set-ItemProperty -Path $p -Name HubsSidebarEnabled -Type DWord -Value 0
                Set-ItemProperty -Path $p -Name StartupBoostEnabled -Type DWord -Value 0
                Set-ItemProperty -Path $p -Name ShowRecommendationsEnabled -Type DWord -Value 0
                Set-ItemProperty -Path $p -Name PersonalizationReportingEnabled -Type DWord -Value 0
                Set-ItemProperty -Path $p -Name EdgeShoppingAssistantEnabled -Type DWord -Value 0
                """,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Edge'
                Remove-ItemProperty -Path $p -Name HubsSidebarEnabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name StartupBoostEnabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name ShowRecommendationsEnabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name PersonalizationReportingEnabled -ErrorAction Stop
                Remove-ItemProperty -Path $p -Name EdgeShoppingAssistantEnabled -ErrorAction Stop
                """),

            T("prefer-ipv4-over-ipv6", "Prefer IPv4 over IPv6", "Configures prefix policy to prefer IPv4 while keeping IPv6 enabled.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters'
                if((Get-ItemProperty -Path $p -Name DisabledComponents -ErrorAction Stop).DisabledComponents -eq 32){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters' -Name DisabledComponents -Type DWord -Value 32
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters' -Name DisabledComponents -ErrorAction Stop
                """),

            T("remove-all-ms-store-apps", "Remove ALL MS Store Apps - NOT RECOMMENDED", "Attempts to remove all non-framework AppX packages for all users.", RiskTier.Dangerous, "System", false,
                "'Not Applied'",
                "Get-AppxPackage -AllUsers | Where-Object { $_.Name -notmatch 'Store|NET|VCLibs|UI.Xaml|Native|Framework' } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Continue }",
                "Write-Output 'Reinstall of removed Store apps is manual.'",
                true),

            T("remove-gallery-from-explorer", "Remove Gallery from explorer", "Removes Gallery namespace from File Explorer navigation.", RiskTier.Advanced, "HKLM", true,
                """
                $k='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}'
                if(-not (Test-Path $k)){'Applied'} else {'Not Applied'}
                """,
                """
                Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}' -Recurse -Force -ErrorAction Continue
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}' -Force | Out-Null
                """),

            T("remove-home-from-explorer", "Remove Home from Explorer", "Switches Explorer launch target away from Home/Quick access.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                if((Get-ItemProperty -Path $p -Name LaunchTo -ErrorAction Stop).LaunchTo -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name LaunchTo -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name LaunchTo -ErrorAction Stop
                """),

            T("remove-xbox-gaming-components", "Remove Xbox & Gaming Components", "Removes Xbox/Gaming app packages and sets Xbox services to manual.", RiskTier.Dangerous, "System", false,
                """
                $svc='XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc'
                $ok=$true
                foreach($s in $svc){
                  $item=Get-Service -Name $s -ErrorAction Ignore
                  if($null -eq $item){continue}
                  if($item.StartType -ne 'Manual'){$ok=$false}
                }
                if($ok){'Applied'} else {'Not Applied'}
                """,
                """
                Get-AppxPackage -AllUsers *Xbox* | Remove-AppxPackage -ErrorAction Continue
                Get-AppxPackage -AllUsers *Gaming* | Remove-AppxPackage -ErrorAction Continue
                Set-Service -Name XblAuthManager -StartupType Manual -ErrorAction Continue
                Set-Service -Name XblGameSave -StartupType Manual -ErrorAction Continue
                Set-Service -Name XboxGipSvc -StartupType Manual -ErrorAction Continue
                Set-Service -Name XboxNetApiSvc -StartupType Manual -ErrorAction Continue
                """,
                "Write-Output 'Xbox components reinstall is manual via Store/optional features.'",
                true),

            T("revert-new-start-menu", "Revert the new start menu", "Applies start menu defaults closer to legacy behavior.", RiskTier.Advanced, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                $left=(Get-ItemProperty -Path $p -Name TaskbarAl -ErrorAction Stop).TaskbarAl
                $rec=(Get-ItemProperty -Path $p -Name Start_IrisRecommendations -ErrorAction Stop).Start_IrisRecommendations
                if($left -eq 0 -and $rec -eq 0){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Type DWord -Value 0
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name Start_IrisRecommendations -Type DWord -Value 0
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name BingSearchEnabled -Type DWord -Value 0
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Type DWord -Value 1
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name Start_IrisRecommendations -Type DWord -Value 1
                Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name BingSearchEnabled -ErrorAction Stop
                """),

            T("set-classic-right-click-menu", "Set Classic Right-Click Menu", "Restores legacy Windows context menu behavior.", RiskTier.Advanced, "HKCU", true,
                """
                $k='HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32'
                if(Test-Path $k){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32' -Name '(Default)' -Value ''
                """,
                """
                Remove-Item -Path 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}' -Recurse -Force -ErrorAction Stop
                """),

            T("set-display-for-performance", "Set Display for Performance", "Configures visual effects for best performance.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects'
                if((Get-ItemProperty -Path $p -Name VisualFXSetting -ErrorAction Stop).VisualFXSetting -eq 2){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name VisualFXSetting -Type DWord -Value 2
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name VisualFXSetting -Type DWord -Value 0
                """),

            T("set-time-to-utc-dualboot", "Set Time to UTC (Dual Boot)", "Configures RTC to UTC for dual-boot setups.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation'
                if((Get-ItemProperty -Path $p -Name RealTimeIsUniversal -ErrorAction Stop).RealTimeIsUniversal -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation' -Name RealTimeIsUniversal -Type DWord -Value 1
                """,
                """
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation' -Name RealTimeIsUniversal -ErrorAction Stop
                """),

            T("ultimate-performance-power-plan", "Ultimate performance power plan", "Enables and activates the Ultimate Performance plan.", RiskTier.Advanced, "System", true,
                """
                $guid='e9a42b02-d5df-448d-aa00-03f14749eb61'
                $active = (powercfg /GetActiveScheme 2>$null | Out-String)
                if($active -match $guid -or $active -match 'Ultimate Performance'){'Applied'} else {'Not Applied'}
                """,
                """
                $templateGuid='e9a42b02-d5df-448d-aa00-03f14749eb61'
                $ultimateGuid = $null
                $plans = (powercfg /L 2>$null | Out-String)
                $ultimateLine = ($plans -split "`r?`n" | Where-Object { $_ -match 'Ultimate Performance' } | Select-Object -First 1)
                if($ultimateLine -and $ultimateLine -match '([0-9a-fA-F\-]{36})'){
                  $ultimateGuid = $matches[1]
                }

                if(-not $ultimateGuid){
                  $dupOut = (powercfg -duplicatescheme $templateGuid 2>&1 | Out-String)
                  $plans = (powercfg /L 2>$null | Out-String)
                  $ultimateLine = ($plans -split "`r?`n" | Where-Object { $_ -match 'Ultimate Performance' } | Select-Object -First 1)
                  if($ultimateLine -and $ultimateLine -match '([0-9a-fA-F\-]{36})'){
                    $ultimateGuid = $matches[1]
                  } elseif($dupOut -match '([0-9a-fA-F\-]{36})') {
                    $ultimateGuid = $matches[1]
                  }
                }

                if(-not $ultimateGuid){
                  throw "Ultimate Performance plan is unavailable on this system."
                }

                $setOut = (powercfg /setactive $ultimateGuid 2>&1 | Out-String)
                $activeNow = (powercfg /GetActiveScheme 2>$null | Out-String)
                if($activeNow -match $ultimateGuid -or $activeNow -match 'Ultimate Performance'){
                  Write-Output 'Applied'
                  return
                }
                throw ("Failed to activate Ultimate Performance plan. " + $setOut.Trim())
                """,
                """
                powercfg /setactive SCHEME_BALANCED 2>$null | Out-Null
                """),

            T("hags-hardware-accelerated-gpu-scheduling", "HAGS (hardware-accelerated GPU scheduling)", "Enables hardware accelerated GPU scheduling.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
                if((Get-ItemProperty -Path $p -Name HwSchMode -ErrorAction Stop).HwSchMode -eq 2){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -Type DWord -Value 2
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -Type DWord -Value 1
                """),

            T("vbs-virtualization-based-security", "VBS (virtualization-based security)", "Enables virtualization-based security.", RiskTier.Advanced, "HKLM", true,
                """
                $a='HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
                $b='HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
                $vbs=(Get-ItemProperty -Path $a -Name EnableVirtualizationBasedSecurity -ErrorAction Stop).EnableVirtualizationBasedSecurity
                $lsa=(Get-ItemProperty -Path $b -Name LsaCfgFlags -ErrorAction Stop).LsaCfgFlags
                if($vbs -eq 1 -and $lsa -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' -Force | Out-Null
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' -Name EnableVirtualizationBasedSecurity -Type DWord -Value 1
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name LsaCfgFlags -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' -Name EnableVirtualizationBasedSecurity -Type DWord -Value 0
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name LsaCfgFlags -Type DWord -Value 0
                """),

            T("relaunch-apps", "Relaunch apps", "Restarts supported apps after sign-in.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                if((Get-ItemProperty -Path $p -Name RestartApps -ErrorAction Stop).RestartApps -eq 1){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name RestartApps -Type DWord -Value 1
                """,
                """
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name RestartApps -Type DWord -Value 0
                """),

            T("background-apps", "Background apps", "Allows supported apps to run in the background.", RiskTier.Basic, "HKCU", true,
                """
                $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications'
                if(-not (Test-Path $p)){ 'Applied' }
                else {
                  try {
                    $item=Get-ItemProperty -Path $p -Name GlobalUserDisabled -ErrorAction Stop
                    if($item.GlobalUserDisabled -eq 0){'Applied'} else {'Not Applied'}
                  } catch {
                    'Applied'
                  }
                }
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' -Name GlobalUserDisabled -Type DWord -Value 0
                """,
                """
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' -Name GlobalUserDisabled -Type DWord -Value 1
                """),

            T("activity-history", "Activity history", "Enables activity history collection and sync policy.", RiskTier.Advanced, "HKLM", true,
                """
                $p='HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
                $pub=$null
                $upl=$null
                try { $pub=(Get-ItemProperty -Path $p -Name PublishUserActivities -ErrorAction Stop).PublishUserActivities } catch {}
                try { $upl=(Get-ItemProperty -Path $p -Name UploadUserActivities -ErrorAction Stop).UploadUserActivities } catch {}
                if(($pub -eq 1 -or $null -eq $pub) -and ($upl -eq 1 -or $null -eq $upl)){'Applied'} else {'Not Applied'}
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name PublishUserActivities -Type DWord -Value 1
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name UploadUserActivities -Type DWord -Value 1
                """,
                """
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name PublishUserActivities -Type DWord -Value 0
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name UploadUserActivities -Type DWord -Value 0
                """),

            T("search-indexing", "Search indexing", "Keeps Windows Search service enabled for indexed results.", RiskTier.Basic, "System", true,
                """
                $s=Get-Service -Name 'WSearch' -ErrorAction Stop
                if($s.StartType -in @('Automatic','AutomaticDelayedStart') -and $s.Status -eq 'Running'){'Applied'} else {'Not Applied'}
                """,
                """
                Set-Service -Name 'WSearch' -StartupType Automatic -ErrorAction Stop
                Start-Service -Name 'WSearch' -ErrorAction Continue
                """,
                """
                Set-Service -Name 'WSearch' -StartupType Manual -ErrorAction Stop
                Stop-Service -Name 'WSearch' -Force -ErrorAction Continue
                """),

            T("delivery-optimization", "Delivery optimization", "Enables Delivery Optimization service and default download mode.", RiskTier.Advanced, "System", true,
                """
                $svc=Get-Service -Name 'DoSvc' -ErrorAction Stop
                $p='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config'
                $mode=$null
                try { $mode=(Get-ItemProperty -Path $p -Name DODownloadMode -ErrorAction Stop).DODownloadMode } catch {}
                if($svc.StartType -in @('Automatic','AutomaticDelayedStart') -and ($null -eq $mode -or $mode -in 0,1,2,3)){'Applied'} else {'Not Applied'}
                """,
                """
                Set-Service -Name 'DoSvc' -StartupType Automatic -ErrorAction Stop
                Start-Service -Name 'DoSvc' -ErrorAction Continue
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' -Name DODownloadMode -Type DWord -Value 1
                """,
                """
                Set-Service -Name 'DoSvc' -StartupType Manual -ErrorAction Stop
                Stop-Service -Name 'DoSvc' -Force -ErrorAction Continue
                New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' -Name DODownloadMode -Type DWord -Value 100
                """),

            T("network-adapter-onboard-processor", "Network adapter onboard processor", "Enables Receive Side Scaling (RSS) on active adapter.", RiskTier.Advanced, "System", true,
                """
                $adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Sort-Object InterfaceMetric | Select-Object -First 1
                if($null -eq $adapter){$adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1}
                if($null -eq $adapter){throw 'No active network adapter found.'}
                $rss=Get-NetAdapterRss -Name $adapter.Name -ErrorAction Stop
                if($rss.Enabled){'Applied'} else {'Not Applied'}
                """,
                """
                $adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Sort-Object InterfaceMetric | Select-Object -First 1
                if($null -eq $adapter){$adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1}
                if($null -eq $adapter){throw 'No active network adapter found.'}
                Set-NetAdapterRss -Name $adapter.Name -Enabled $true -ErrorAction Stop
                """,
                """
                $adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Sort-Object InterfaceMetric | Select-Object -First 1
                if($null -eq $adapter){$adapter=Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1}
                if($null -eq $adapter){throw 'No active network adapter found.'}
                Set-NetAdapterRss -Name $adapter.Name -Enabled $false -ErrorAction Stop
                """),

            
        ];
    }

    private static TweakDefinition T(
        string id,
        string name,
        string description,
        RiskTier riskTier,
        string scope,
        bool reversible,
        string detectScript,
        string applyScript,
        string undoScript,
        bool destructive = false)
    {
        return new TweakDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            RiskTier = riskTier,
            Scope = scope,
            Reversible = reversible,
            DetectScript = detectScript,
            ApplyScript = applyScript,
            UndoScript = undoScript,
            Destructive = destructive,
            StateCaptureKeys = []
        };
    }
}
