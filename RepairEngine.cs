using System.Text;

namespace WinUpdateRepairTool;

internal sealed class RepairEngine
{
    private readonly CommandRunner _runner = new();

    public async Task RunDiagnosticsAsync(LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        log.WriteSection("Diagnostic run");
        await RunManyAsync(GetDiagnosticCommands(log), log, progress, cancellationToken);
        await ExportEventLogsAsync(log, progress, cancellationToken);
        CopyKnownLogs(log);
    }

    public async Task RunRecommendedRepairAsync(
        LogSession log,
        IProgress<string> progress,
        string? repairSource,
        bool limitAccess,
        CancellationToken cancellationToken)
    {
        log.WriteSection("Recommended repair");
        log.WriteLine("Reminder: take a VMware snapshot or backup before running repairs on a production server.");

        await ResetWindowsUpdateAsync(log, progress, cancellationToken);

        var restoreArgs = new StringBuilder("/Online /Cleanup-Image /RestoreHealth");
        if (!string.IsNullOrWhiteSpace(repairSource))
        {
            restoreArgs.Append(" /Source:\"").Append(repairSource.Trim()).Append('"');
        }

        if (limitAccess)
        {
            restoreArgs.Append(" /LimitAccess");
        }

        await _runner.RunAsync(new CommandSpec(
            "DISM RestoreHealth",
            "dism.exe",
            restoreArgs.ToString(),
            TimeSpan.FromHours(4)), log, progress, cancellationToken);

        await _runner.RunAsync(new CommandSpec(
            "SFC scan and repair",
            "sfc.exe",
            "/scannow",
            TimeSpan.FromHours(3)), log, progress, cancellationToken);

        await _runner.RunAsync(new CommandSpec(
            "Online disk scan",
            "chkdsk.exe",
            $"{Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"} /scan",
            TimeSpan.FromHours(2)), log, progress, cancellationToken);

        await _runner.RunAsync(PowerShell(
            "Trigger Windows Update detection",
            "UsoClient StartScan; wuauclt /resetauthorization /detectnow",
            TimeSpan.FromMinutes(10)), log, progress, cancellationToken);

        await RunDiagnosticsAsync(log, progress, cancellationToken);
    }

    public async Task ResetWindowsUpdateAsync(LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        log.WriteSection("Windows Update cache reset");
        var script = """
            $ErrorActionPreference = 'Continue'
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $services = 'wuauserv','bits','cryptsvc','trustedinstaller','msiserver','appidsvc'
            foreach ($service in $services) {
              $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
              if ($svc) {
                Write-Host "Stopping $service"
                Stop-Service -Name $service -Force -ErrorAction SilentlyContinue
              } else {
                Write-Host "Service not present: $service"
              }
            }
            $targets = @(
              "$env:windir\SoftwareDistribution",
              "$env:windir\System32\catroot2"
            )
            foreach ($target in $targets) {
              if (Test-Path -LiteralPath $target) {
                $backup = "$target.bak-$stamp"
                Write-Host "Renaming $target to $backup"
                Rename-Item -LiteralPath $target -NewName (Split-Path -Leaf $backup) -ErrorAction Continue
              } else {
                Write-Host "Path not present: $target"
              }
            }
            $restartServices = $services.Clone()
            [array]::Reverse($restartServices)
            foreach ($service in $restartServices) {
              $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
              if ($svc) {
                Write-Host "Starting $service"
                Start-Service -Name $service -ErrorAction SilentlyContinue
              }
            }
            """;

        await _runner.RunAsync(PowerShell("Reset Windows Update services and caches", script, TimeSpan.FromMinutes(30)), log, progress, cancellationToken);
    }

    public async Task CollectBootCrashAsync(LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        log.WriteSection("Boot and crash evidence");
        foreach (var command in GetBootCrashCommands(log))
        {
            await _runner.RunAsync(command, log, progress, cancellationToken);
        }

        CopyKnownLogs(log);
    }

    public async Task RunBootRiskCheckAsync(LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        log.WriteSection("Boot risk check");
        await _runner.RunAsync(PowerShell("Boot-critical update risk audit", $$"""
            $ErrorActionPreference = 'Continue'
            $reportPath = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'BootRiskCheck.report.txt'
            $dismPath = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'BootRiskCheck.dism-packages.txt'
            $bcdPath = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'BootRiskCheck.bcd.txt'
            $findings = New-Object System.Collections.Generic.List[object]

            function Add-Finding {
              param(
                [ValidateSet('INFO','PASS','WARN','FAIL')] [string] $Severity,
                [string] $Check,
                [string] $Message,
                [string] $Recommendation = ''
              )
              $findings.Add([pscustomobject]@{
                Severity = $Severity
                Check = $Check
                Message = $Message
                Recommendation = $Recommendation
              }) | Out-Null
            }

            function Get-RegDword {
              param([string] $Path, [string] $Name)
              try {
                $item = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop
                return [int]$item.$Name
              } catch {
                return $null
              }
            }

            function Test-BootDriver {
              param(
                [string] $ServiceName,
                [string] $FriendlyName,
                [bool] $RequiredWhenDetected
              )

              $servicePath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
              if (-not (Test-Path -LiteralPath $servicePath)) {
                Add-Finding WARN "$FriendlyName service" "$ServiceName was not found in Services." "If this controller is used for the boot disk, the VM can hit INACCESSIBLE_BOOT_DEVICE."
                return
              }

              $start = Get-RegDword $servicePath 'Start'
              $group = try { (Get-ItemProperty -LiteralPath $servicePath -Name Group -ErrorAction Stop).Group } catch { '' }
              $imagePath = try { (Get-ItemProperty -LiteralPath $servicePath -Name ImagePath -ErrorAction Stop).ImagePath } catch { '' }

              if ($RequiredWhenDetected -and $start -ne 0) {
                Add-Finding FAIL "$FriendlyName start type" "$ServiceName Start is $start, expected 0 (BOOT_START)." "Set the boot storage driver to BOOT_START before applying updates."
              } elseif ($start -eq 0) {
                Add-Finding PASS "$FriendlyName start type" "$ServiceName is BOOT_START. Group='$group' ImagePath='$imagePath'."
              } else {
                Add-Finding INFO "$FriendlyName start type" "$ServiceName Start is $start. This is only critical if that controller owns the boot disk."
              }

              $overridePath = Join-Path $servicePath 'StartOverride'
              if (Test-Path -LiteralPath $overridePath) {
                $override = Get-ItemProperty -LiteralPath $overridePath
                $badValues = @()
                foreach ($property in $override.PSObject.Properties) {
                  if ($property.Name -like 'PS*') { continue }
                  if ($property.Value -is [int] -and $property.Value -ne 0) {
                    $badValues += "$($property.Name)=$($property.Value)"
                  }
                }

                if ($badValues.Count -gt 0) {
                  Add-Finding FAIL "$FriendlyName StartOverride" "$ServiceName has non-zero StartOverride values: $($badValues -join ', ')." "Non-zero StartOverride can prevent a boot-critical storage driver from loading."
                } else {
                  Add-Finding PASS "$FriendlyName StartOverride" "$ServiceName StartOverride is absent or all zero."
                }
              } else {
                Add-Finding PASS "$FriendlyName StartOverride" "$ServiceName has no StartOverride key."
              }
            }

            function Test-ClassFilters {
              $classes = @{
                'DiskDrive' = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
                'HDC' = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e96a-e325-11ce-bfc1-08002be10318}'
                'SCSIAdapter' = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e97b-e325-11ce-bfc1-08002be10318}'
                'Volume' = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}'
              }

              foreach ($className in $classes.Keys) {
                $path = $classes[$className]
                if (-not (Test-Path -LiteralPath $path)) {
                  continue
                }

                $props = Get-ItemProperty -LiteralPath $path
                foreach ($filterName in 'UpperFilters','LowerFilters') {
                  $filters = $props.$filterName
                  if (-not $filters) {
                    Add-Finding PASS "$className $filterName" "No $filterName entries on $className."
                    continue
                  }

                  foreach ($filter in @($filters)) {
                    $filterService = "HKLM:\SYSTEM\CurrentControlSet\Services\$filter"
                    if (-not (Test-Path -LiteralPath $filterService)) {
                      Add-Finding FAIL "$className $filterName" "$filterName references '$filter', but that service key is missing." "Remove orphaned storage filter references only after confirming they are not required."
                      continue
                    }

                    $start = Get-RegDword $filterService 'Start'
                    if ($start -eq 4) {
                      Add-Finding FAIL "$className $filterName" "$filterName references '$filter', but its service is disabled." "A disabled storage filter referenced by the class key can cause 0x7B."
                    } elseif ($start -gt 1) {
                      Add-Finding WARN "$className $filterName" "$filterName references '$filter' with Start=$start." "Review this filter if 0x7B occurs after updates."
                    } else {
                      Add-Finding INFO "$className $filterName" "$filterName references '$filter' with Start=$start."
                    }
                  }
                }
              }
            }

            Write-Host 'Collecting storage controller inventory...'
            $controllers = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
              Where-Object { $_.DeviceClass -match 'SCSIAdapter|HDC' -or $_.DeviceName -match 'LSI|SAS|PVSCSI|VMware|RAID|SATA|NVMe' } |
              Sort-Object DeviceClass,DeviceName)

            if ($controllers.Count -eq 0) {
              Add-Finding WARN 'Storage controller inventory' 'No storage controller driver inventory was returned from WMI/CIM.' 'Check Device Manager manually before rebooting after updates.'
            } else {
              foreach ($controller in $controllers) {
                Add-Finding INFO 'Storage controller inventory' "$($controller.DeviceName) | Provider=$($controller.DriverProviderName) | Version=$($controller.DriverVersion) | INF=$($controller.InfName)"
              }
            }

            $detectedLsi = $controllers | Where-Object { $_.DeviceName -match 'LSI|Fusion|SAS' -or $_.InfName -match 'lsi' }
            $detectedPvscsi = $controllers | Where-Object { $_.DeviceName -match 'PVSCSI|Paravirtual' -or $_.InfName -match 'pvscsi' }

            Test-BootDriver 'LSI_SAS' 'LSI Logic SAS' ([bool]$detectedLsi)
            Test-BootDriver 'pvscsi' 'VMware PVSCSI' ([bool]$detectedPvscsi)
            Test-BootDriver 'vmscsi' 'VMware legacy SCSI' $false
            Test-BootDriver 'storahci' 'Microsoft AHCI' $false
            Test-BootDriver 'stornvme' 'Microsoft NVMe' $false
            Test-BootDriver 'megasas' 'MegaRAID SAS' $false

            Test-ClassFilters

            Write-Host 'Checking pending update and reboot markers...'
            $pendingMarkers = @(
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending',
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
            )
            foreach ($marker in $pendingMarkers) {
              if (Test-Path -LiteralPath $marker) {
                Add-Finding WARN 'Pending update marker' "$marker exists." 'Reboot or finish servicing before starting another update cycle.'
              } else {
                Add-Finding PASS 'Pending update marker' "$marker is not present."
              }
            }

            $pendingRename = try { (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop).PendingFileRenameOperations } catch { $null }
            if ($pendingRename) {
              Add-Finding WARN 'Pending file rename operations' 'PendingFileRenameOperations contains entries.' 'A reboot is already queued; avoid stacking Windows updates.'
            } else {
              Add-Finding PASS 'Pending file rename operations' 'No PendingFileRenameOperations value was found.'
            }

            $pendingXml = Join-Path $env:windir 'WinSxS\pending.xml'
            if (Test-Path -LiteralPath $pendingXml) {
              Add-Finding WARN 'Pending.xml' "$pendingXml exists." 'If the VM fails after update, offline DISM /RevertPendingActions may be needed.'
            } else {
              Add-Finding PASS 'Pending.xml' "$pendingXml is not present."
            }

            Write-Host 'Running DISM package state inventory...'
            $dismOutput = & dism.exe /Online /Get-Packages /Format:Table 2>&1
            $dismOutput | Set-Content -LiteralPath $dismPath -Encoding UTF8
            if ($dismOutput -match 'Install Pending|Uninstall Pending') {
              Add-Finding FAIL 'DISM package state' 'DISM reports one or more Install Pending or Uninstall Pending packages.' 'Do not start another update. Reboot cleanly or use offline recovery if the server will not boot.'
            } else {
              Add-Finding PASS 'DISM package state' 'No Install Pending or Uninstall Pending packages found in DISM package output.'
            }

            Write-Host 'Checking system volume free space...'
            try {
              $systemLetter = $env:SystemDrive.TrimEnd(':')
              $systemVolume = Get-Volume -DriveLetter $systemLetter -ErrorAction Stop
              $freeGb = [math]::Round($systemVolume.SizeRemaining / 1GB, 2)
              if ($systemVolume.SizeRemaining -lt 3GB) {
                Add-Finding FAIL 'System volume free space' "$($env:SystemDrive) has $freeGb GB free." 'Free at least 10 GB before attempting Windows updates or in-place upgrades.'
              } elseif ($systemVolume.SizeRemaining -lt 10GB) {
                Add-Finding WARN 'System volume free space' "$($env:SystemDrive) has $freeGb GB free." 'Low free space can cause servicing rollback and boot failures.'
              } else {
                Add-Finding PASS 'System volume free space' "$($env:SystemDrive) has $freeGb GB free."
              }
            } catch {
              Add-Finding WARN 'System volume free space' "Could not query system volume free space: $($_.Exception.Message)"
            }

            try {
              $systemPartitions = @(Get-Partition -ErrorAction Stop | Where-Object { $_.IsSystem -or $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' })
              foreach ($partition in $systemPartitions) {
                $volume = $partition | Get-Volume -ErrorAction SilentlyContinue
                if ($volume) {
                  $freeMb = [math]::Round($volume.SizeRemaining / 1MB, 1)
                  if ($volume.SizeRemaining -lt 50MB) {
                    Add-Finding FAIL 'System/EFI partition free space' "Disk $($partition.DiskNumber) partition $($partition.PartitionNumber) has $freeMb MB free." 'Free space on the boot partition is dangerously low.'
                  } elseif ($volume.SizeRemaining -lt 100MB) {
                    Add-Finding WARN 'System/EFI partition free space' "Disk $($partition.DiskNumber) partition $($partition.PartitionNumber) has $freeMb MB free." 'Low boot partition free space can break servicing or boot file updates.'
                  } else {
                    Add-Finding PASS 'System/EFI partition free space' "Disk $($partition.DiskNumber) partition $($partition.PartitionNumber) has $freeMb MB free."
                  }
                } else {
                  Add-Finding INFO 'System/EFI partition free space' "Disk $($partition.DiskNumber) partition $($partition.PartitionNumber) has no mounted volume for free-space query."
                }
              }
            } catch {
              Add-Finding WARN 'System/EFI partition free space' "Could not query system partitions: $($_.Exception.Message)"
            }

            Write-Host 'Checking BCD, WinRE, and BitLocker...'
            $bcd = & bcdedit.exe /enum all 2>&1
            $bcd | Set-Content -LiteralPath $bcdPath -Encoding UTF8
            if ($bcd -match 'unknown') {
              Add-Finding WARN 'BCD inventory' 'BCD output contains unknown device/path entries.' 'Review BootRiskCheck.bcd.txt before rebooting after updates.'
            } else {
              Add-Finding PASS 'BCD inventory' 'BCD output did not contain unknown device/path entries.'
            }

            $winre = & reagentc.exe /info 2>&1
            if ($winre -match 'Disabled') {
              Add-Finding WARN 'Windows Recovery Environment' 'WinRE appears disabled.' 'Enable or confirm recovery media before patching a server that has recurring boot failures.'
            } else {
              Add-Finding INFO 'Windows Recovery Environment' ($winre -join ' ')
            }

            $bitLocker = & manage-bde.exe -status $env:SystemDrive 2>&1
            if ($bitLocker -match 'Protection On') {
              Add-Finding WARN 'BitLocker' 'BitLocker protection appears enabled on the system volume.' 'Suspend protection before firmware, boot, or storage-driver changes.'
            } else {
              Add-Finding PASS 'BitLocker' 'BitLocker protection is not reported as enabled on the system volume.'
            }

            Write-Host 'Checking servicing-related services...'
            foreach ($serviceName in 'TrustedInstaller','wuauserv','bits','cryptsvc','PlugPlay','DeviceInstall') {
              $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
              if (-not $service) {
                Add-Finding WARN 'Service check' "$serviceName service was not found."
                continue
              }

              $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
              $start = Get-RegDword $regPath 'Start'
              if ($start -eq 4) {
                Add-Finding FAIL 'Service check' "$serviceName is disabled." 'Required update/device installation services must not be disabled during patching.'
              } else {
                Add-Finding INFO 'Service check' "$serviceName status=$($service.Status) start=$start."
              }
            }

            Write-Host 'Checking recent boot, disk, and update failures...'
            try {
              $events = @(Get-WinEvent -FilterHashtable @{LogName='System'; Id=7,11,15,41,51,55,129,153,157,1001,6008,7000,7001,7026} -MaxEvents 80 -ErrorAction Stop)
              if ($events.Count -gt 0) {
                $eventText = $events | Select-Object TimeCreated,Id,ProviderName,LevelDisplayName,Message | Format-List | Out-String
                $eventPath = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'BootRiskCheck.recent-system-events.txt'
                $eventText | Set-Content -LiteralPath $eventPath -Encoding UTF8
                Add-Finding WARN 'Recent system events' "Found $($events.Count) recent disk/boot/service-control events." 'Review BootRiskCheck.recent-system-events.txt for storage resets, disk errors, and boot failures.'
              } else {
                Add-Finding PASS 'Recent system events' 'No recent matching disk/boot/service-control events were returned.'
              }
            } catch {
              Add-Finding WARN 'Recent system events' "Could not query System log: $($_.Exception.Message)"
            }

            $critical = @($findings | Where-Object Severity -eq 'FAIL').Count
            $warnings = @($findings | Where-Object Severity -eq 'WARN').Count
            $passes = @($findings | Where-Object Severity -eq 'PASS').Count
            $infos = @($findings | Where-Object Severity -eq 'INFO').Count

            $lines = New-Object System.Collections.Generic.List[string]
            $lines.Add('Server Update Repair Tool - Boot Risk Check') | Out-Null
            $lines.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
            $lines.Add("Computer: $env:COMPUTERNAME") | Out-Null
            $lines.Add("Summary: FAIL=$critical WARN=$warnings PASS=$passes INFO=$infos") | Out-Null
            $lines.Add('') | Out-Null

            foreach ($finding in $findings) {
              $lines.Add("[$($finding.Severity)] $($finding.Check): $($finding.Message)") | Out-Null
              if ($finding.Recommendation) {
                $lines.Add("  Recommendation: $($finding.Recommendation)") | Out-Null
              }
            }

            $lines | Set-Content -LiteralPath $reportPath -Encoding UTF8
            foreach ($line in $lines) {
              Write-Host $line
            }

            Write-Host "BOOT RISK SUMMARY: FAIL=$critical WARN=$warnings PASS=$passes INFO=$infos"
            Write-Host "Boot risk report: $reportPath"

            if ($critical -gt 0) {
              exit 2
            }
            if ($warnings -gt 0) {
              exit 1
            }
            exit 0
            """, TimeSpan.FromMinutes(20)), log, progress, cancellationToken);
    }

    private async Task RunManyAsync(IEnumerable<CommandSpec> commands, LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runner.RunAsync(command, log, progress, cancellationToken);
        }
    }

    private IEnumerable<CommandSpec> GetDiagnosticCommands(LogSession log)
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        yield return Cmd("Windows version", "ver", TimeSpan.FromMinutes(2));
        yield return Cmd("System information", "systeminfo", TimeSpan.FromMinutes(5));
        yield return Cmd("Installed updates", "wmic qfe list brief /format:table", TimeSpan.FromMinutes(10));
        yield return Cmd("Windows Update services", "sc query wuauserv & sc query bits & sc query cryptsvc & sc query trustedinstaller", TimeSpan.FromMinutes(5));
        yield return new CommandSpec("DISM CheckHealth", "dism.exe", "/Online /Cleanup-Image /CheckHealth", TimeSpan.FromMinutes(30));
        yield return new CommandSpec("DISM ScanHealth", "dism.exe", "/Online /Cleanup-Image /ScanHealth", TimeSpan.FromHours(2));
        yield return new CommandSpec("SFC verify only", "sfc.exe", "/verifyonly", TimeSpan.FromHours(2));
        yield return new CommandSpec("CHKDSK online scan", "chkdsk.exe", $"{systemDrive} /scan", TimeSpan.FromHours(2));
        yield return Cmd("Dirty bit query", $"fsutil dirty query {systemDrive}", TimeSpan.FromMinutes(5));
        yield return Cmd("BCD inventory", "bcdedit /enum all", TimeSpan.FromMinutes(5));
        yield return Cmd("Windows recovery configuration", "reagentc /info", TimeSpan.FromMinutes(5));
        yield return Cmd("Driver inventory", "driverquery /v /fo csv", TimeSpan.FromMinutes(10));
        yield return PowerShell("Storage and VMware driver inventory", """
            Get-CimInstance Win32_PnPSignedDriver |
              Where-Object { $_.DeviceClass -match 'SCSIAdapter|HDC|System|DiskDrive|SCSIAdapter' -or $_.DeviceName -match 'VMware|LSI|PVSCSI|SAS|SATA|NVMe|RAID' } |
              Select-Object DeviceName,DeviceClass,DriverVersion,DriverProviderName,DriverDate,InfName |
              Sort-Object DeviceClass,DeviceName |
              Format-Table -AutoSize
            Get-CimInstance Win32_DiskDrive | Format-List *
            Get-CimInstance Win32_IDEController -ErrorAction SilentlyContinue | Format-List *
            Get-CimInstance Win32_SCSIController -ErrorAction SilentlyContinue | Format-List *
            """, TimeSpan.FromMinutes(15));
        yield return PowerShell("Pending reboot markers", """
            $paths = @(
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
              'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
            )
            foreach ($path in $paths) {
              Write-Host "== $path =="
              if (Test-Path $path) { Get-ItemProperty $path | Format-List * } else { Write-Host 'Not present' }
            }
            """, TimeSpan.FromMinutes(5));
        yield return PowerShell("Create WindowsUpdate.log", $$"""
            $target = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'WindowsUpdate.generated.log'
            try {
              Get-WindowsUpdateLog -LogPath $target -ErrorAction Stop
              Write-Host "Generated $target"
            } catch {
              Write-Host "Get-WindowsUpdateLog failed: $($_.Exception.Message)"
            }
            """, TimeSpan.FromMinutes(30));
        yield return PowerShell("Run Microsoft SetupDiag", $$"""
            $out = Join-Path '{{EscapePowerShellLiteral(log.RootPath)}}' 'SetupDiag'
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            $tool = Join-Path $out 'SetupDiag.exe'
            $candidates = @(
              "$env:SystemDrive\`$Windows.~BT\Sources\SetupDiag.exe",
              "$env:SystemDrive\Windows.old\`$Windows.~BT\Sources\SetupDiag.exe",
              "$env:windir\Logs\SetupDiag\SetupDiag.exe"
            )
            foreach ($candidate in $candidates) {
              if (Test-Path -LiteralPath $candidate) {
                Copy-Item -LiteralPath $candidate -Destination $tool -Force
                Write-Host "Using existing SetupDiag from $candidate"
                break
              }
            }
            if (-not (Test-Path -LiteralPath $tool)) {
              Write-Host 'Downloading latest SetupDiag from Microsoft'
              Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=870142' -OutFile $tool -UseBasicParsing
            }
            if (Test-Path -LiteralPath $tool) {
              $result = Join-Path $out 'SetupDiagResults.xml'
              & $tool /Output:"$result" /Format:xml /NoTel /Verbose /ZipLogs:True
              Write-Host "SetupDiag exit code: $LASTEXITCODE"
            } else {
              Write-Host 'SetupDiag was not available.'
            }
            """, TimeSpan.FromMinutes(30));
    }

    private IEnumerable<CommandSpec> GetBootCrashCommands(LogSession log)
    {
        yield return Cmd("Boot configuration", "bcdedit /enum all", TimeSpan.FromMinutes(5));
        yield return Cmd("Firmware boot manager", "bcdedit /enum firmware", TimeSpan.FromMinutes(5));
        yield return Cmd("Recovery configuration", "reagentc /info", TimeSpan.FromMinutes(5));
        yield return Cmd("BitLocker status", "manage-bde -status", TimeSpan.FromMinutes(5));
        yield return PowerShell("Disk and volume layout", """
            Get-Disk | Format-List *
            Get-Partition | Format-Table -AutoSize
            Get-Volume | Format-Table -AutoSize
            """, TimeSpan.FromMinutes(10));
        yield return PowerShell("Recent bugchecks and boot failures", """
            Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,55,1001,1005,10010,10016,1074,6008,7000,7001,7026} -MaxEvents 200 |
              Select-Object TimeCreated,Id,ProviderName,LevelDisplayName,Message |
              Format-List
            """, TimeSpan.FromMinutes(10));
    }

    private async Task ExportEventLogsAsync(LogSession log, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var eventFolder = Path.Combine(log.RootPath, "EventLogs");
        Directory.CreateDirectory(eventFolder);
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $out = '{{EscapePowerShellLiteral(eventFolder)}}'
            $logs = @(
              'System',
              'Application',
              'Setup',
              'Microsoft-Windows-WindowsUpdateClient/Operational',
              'Microsoft-Windows-Servicing/Operational',
              'Microsoft-Windows-Kernel-Boot/Operational',
              'Microsoft-Windows-Partition/Diagnostic'
            )
            foreach ($logName in $logs) {
              $safe = ($logName -replace '[\\/:*?"<>|]', '_')
              $evtx = Join-Path $out "$safe.evtx"
              $txt = Join-Path $out "$safe.last200.txt"
              Write-Host "Exporting $logName"
              wevtutil epl "$logName" "$evtx" /ow:true
              wevtutil qe "$logName" /c:200 /rd:true /f:text > "$txt"
            }
            """;

        await _runner.RunAsync(PowerShell("Export Windows event logs", script, TimeSpan.FromMinutes(30)), log, progress, cancellationToken);
    }

    private void CopyKnownLogs(LogSession log)
    {
        log.WriteSection("Copy known Windows logs");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var bootRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        log.CopyIfExists(Path.Combine(windows, "Logs", "CBS", "CBS.log"), "WindowsLogs\\CBS");
        log.CopyPattern(Path.Combine(windows, "Logs", "CBS"), "CbsPersist*.cab", "WindowsLogs\\CBS");
        log.CopyIfExists(Path.Combine(windows, "Logs", "DISM", "dism.log"), "WindowsLogs\\DISM");
        log.CopyIfExists(Path.Combine(windows, "WindowsUpdate.log"), "WindowsLogs\\WindowsUpdate");
        log.CopyPattern(Path.Combine(windows, "Logs", "SetupDiag"), "*", "WindowsLogs\\SetupDiag", maxFiles: 25);
        log.CopyIfExists(Path.Combine(windows, "inf", "setupapi.dev.log"), "WindowsLogs\\SetupApi");
        log.CopyIfExists(Path.Combine(windows, "Panther", "setupact.log"), "WindowsLogs\\Panther");
        log.CopyIfExists(Path.Combine(windows, "Panther", "setuperr.log"), "WindowsLogs\\Panther");
        log.CopyPattern(Path.Combine(windows, "Panther"), "*.log", "WindowsLogs\\Panther", maxFiles: 25);
        log.CopyPattern(Path.Combine(windows, "Panther", "Rollback"), "*.log", "WindowsLogs\\Panther\\Rollback", maxFiles: 50);
        log.CopyPattern(Path.Combine(bootRoot, "$Windows.~BT", "Sources", "Panther"), "*.log", "WindowsLogs\\WindowsBT\\Sources\\Panther", maxFiles: 50);
        log.CopyPattern(Path.Combine(bootRoot, "$Windows.~BT", "Sources", "Rollback"), "*.log", "WindowsLogs\\WindowsBT\\Sources\\Rollback", maxFiles: 50);
        log.CopyPattern(Path.Combine(windows, "Minidump"), "*.dmp", "CrashDumps\\Minidump", maxFiles: 10, maxBytes: 128L * 1024L * 1024L);
        log.CopyIfExists(Path.Combine(windows, "MEMORY.DMP"), "CrashDumps", maxBytes: 512L * 1024L * 1024L);
    }

    private static CommandSpec Cmd(string name, string command, TimeSpan timeout)
    {
        return new CommandSpec(name, "cmd.exe", "/c " + command, timeout);
    }

    private static CommandSpec PowerShell(string name, string script, TimeSpan timeout)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new CommandSpec(name, "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeout);
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''");
    }
}
