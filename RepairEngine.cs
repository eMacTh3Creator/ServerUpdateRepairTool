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
