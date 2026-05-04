<p align="center">
  <img src="assets/logo.svg" width="120" alt="Server Update Repair Tool logo">
</p>

# Server Update Repair Tool

Server Update Repair Tool is a Windows desktop utility for diagnosing and repairing Windows Update, servicing, and in-place upgrade failures on Windows Server systems.

It was built for the kind of server that refuses cumulative updates, rolls back feature/version upgrades, or crashes around boot after a failed upgrade attempt. The tool favors conservative Windows-supported repair actions and aggressive evidence collection, so every step leaves a clear log trail.

## What It Collects

- Full transcript for every command the tool runs
- CBS and DISM servicing logs
- Windows Update logs, including generated `WindowsUpdate.log` where supported
- Panther setup and rollback logs
- Windows SetupDiag results for failed upgrade analysis
- Recent System, Application, Setup, Windows Update, Servicing, Kernel-Boot, and partition event logs
- Driver, storage controller, disk, BCD, WinRE, and reboot-pending inventory
- Minidumps and memory dump copy attempts with size limits

## Repair Actions

The recommended repair flow can:

- Reset Windows Update services and rebuild `SoftwareDistribution` / `catroot2`
- Run `DISM /Online /Cleanup-Image /RestoreHealth`
- Run `sfc /scannow`
- Run `chkdsk C: /scan`
- Trigger Windows Update detection
- Re-run diagnostics after repair

The tool does not automatically rewrite BCD, change VMware virtual storage controller drivers, or force offline boot repair. Those operations can break a VM if the virtual hardware and Windows storage stack do not agree.

## Download / Build

Build on Windows with .NET 8 SDK or newer:

```powershell
.\scripts\build.ps1
```

The executable will be published to:

```text
artifacts\ServerUpdateRepairTool\ServerUpdateRepairTool.exe
```

You can also publish directly:

```powershell
dotnet publish WinUpdateRepairTool.csproj -c Release -r win-x64 --self-contained true -o artifacts\ServerUpdateRepairTool
```

## Recommended Server Workflow

1. Take a VMware snapshot or verified backup.
2. Copy `ServerUpdateRepairTool.exe` to the affected Windows Server VM.
3. Run the executable as Administrator.
4. Start with `Full Diagnostic`.
5. Run `Boot/Crash Logs` if the system has shown `INACCESSIBLE_BOOT_DEVICE`, boot device not found, or similar boot failures.
6. Run `Recommended Repair`.
7. Reboot.
8. Try Windows Update or the in-place upgrade again.
9. Use `Zip Bundle` and review or share the collected logs if it still fails.

## DISM Repair Source

If DISM reports that source files are missing, mount a Windows Server ISO that matches the installed OS and patch level as closely as possible. Then enter a source such as:

```text
D:\sources\sxs
```

or:

```text
WIM:D:\sources\install.wim:2
```

Enable `/LimitAccess` when you want DISM to use only that source instead of contacting Windows Update.

## Log Location

Every run creates a timestamped folder:

```text
C:\ProgramData\ServerUpdateRepairTool\Logs\yyyyMMdd-HHmmss
```

The main file is:

```text
transcript.log
```

The UI can open the log folder or create a zip bundle.

## Notes for VMware / ESXi

For upgrade rollbacks followed by boot-device errors, pay special attention to:

- VMware Tools version
- PVSCSI / LSI Logic SAS controller driver state
- Boot mode consistency between BIOS and UEFI
- System Reserved / EFI partition free space
- Storage controller changes made during previous migrations
- SetupDiag output for driver migration and Safe OS failures

Those issues are intentionally logged rather than modified automatically.

## References

- [Microsoft SetupDiag documentation](https://learn.microsoft.com/en-us/windows/deployment/upgrade/setupdiag)
- [Microsoft DISM operating system package servicing command-line options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options)

