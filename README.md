<p align="center">
  <img src="assets/logo.svg" width="120" alt="Server Update Repair Tool logo">
</p>

# Server Update Repair Tool

Server Update Repair Tool is a Windows desktop utility for diagnosing and repairing Windows Update, servicing, and in-place upgrade failures on Windows Server systems.

It was built for the kind of server that refuses cumulative updates, rolls back feature/version upgrades, or crashes around boot after a failed upgrade attempt. The tool favors conservative Windows-supported repair actions and aggressive evidence collection, so every step leaves a clear log trail.

## What It Collects

- Full transcript for every command the tool runs
- Boot Risk Check report for pre-update storage/boot readiness
- CBS and DISM servicing logs
- Windows Update logs, including generated `WindowsUpdate.log` where supported
- Panther setup and rollback logs
- Windows SetupDiag results for failed upgrade analysis
- Recent System, Application, Setup, Windows Update, Servicing, Kernel-Boot, and partition event logs
- Driver, storage controller, disk, BCD, WinRE, and reboot-pending inventory
- Minidumps and memory dump copy attempts with size limits

## Repair Actions

The `Boot Risk Check` button is read-only. It checks:

- LSI Logic SAS, VMware PVSCSI, VMware legacy SCSI, AHCI, NVMe, and MegaRAID boot-driver registry state
- `StartOverride` values that can stop a boot-critical driver from loading
- Storage class `UpperFilters` and `LowerFilters` references
- Pending reboot, pending CBS package, and `pending.xml` markers
- DISM package states such as `Install Pending` or `Uninstall Pending`
- BCD entries with unknown device/path values
- System volume and EFI/System partition free space
- WinRE and BitLocker state
- Update/device-install services that are disabled
- Recent disk, boot, bugcheck, and service-control events

The recommended repair flow can:

- Reset Windows Update services and rebuild `SoftwareDistribution` / `catroot2`
- Run `DISM /Online /Cleanup-Image /RestoreHealth`
- Run `sfc /scannow`
- Run `chkdsk C: /scan`
- Trigger Windows Update detection
- Re-run diagnostics after repair

The tool does not automatically rewrite BCD, change VMware virtual storage controller drivers, or force offline boot repair. Those operations can break a VM if the virtual hardware and Windows storage stack do not agree.

## Download

Most users should download the prebuilt package instead of building from source:

[Download the latest release](https://github.com/eMacTh3Creator/ServerUpdateRepairTool/releases/latest)

Use the `ServerUpdateRepairTool-v*-win-x64.zip` asset, extract it, and run `ServerUpdateRepairTool.exe` as Administrator on the affected server.

The release also includes a `.sha256` checksum file. To verify it on Windows:

```powershell
Get-FileHash .\ServerUpdateRepairTool-v0.1.0-win-x64.zip -Algorithm SHA256
```

## Build From Source

Developers can build on Windows with .NET 8 SDK or newer:

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
4. Run `Boot Risk Check` before applying updates.
5. Start with `Full Diagnostic`.
6. Run `Boot/Crash Logs` if the system has shown `INACCESSIBLE_BOOT_DEVICE`, boot device not found, or similar boot failures.
7. Run `Recommended Repair` only after reviewing any Boot Risk Check failures.
8. Reboot.
9. Try Windows Update or the in-place upgrade again.
10. Use `Zip Bundle` and review or share the collected logs if it still fails.

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

Pre-update boot risk output is written to:

```text
BootRiskCheck.report.txt
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
