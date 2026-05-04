using System.Diagnostics;
using System.Text;

namespace WinUpdateRepairTool;

internal sealed record CommandSpec(
    string Name,
    string FileName,
    string Arguments,
    TimeSpan Timeout,
    bool ContinueOnError = true);

internal sealed record CommandResult(
    string Name,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration);

internal sealed class CommandRunner
{
    public async Task<CommandResult> RunAsync(
        CommandSpec spec,
        LogSession log,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        log.WriteSection(spec.Name);
        log.WriteLine($"> {spec.FileName} {spec.Arguments}");
        progress.Report($"Starting: {spec.Name}");

        using var timeoutCts = new CancellationTokenSource(spec.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default,
            WorkingDirectory = log.RootPath
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                log.WriteLine("Failed to start process.");
                return new CommandResult(spec.Name, -1, false, DateTimeOffset.Now - started);
            }

            var outputTask = PumpAsync(process.StandardOutput, log, progress, linkedCts.Token);
            var errorTask = PumpAsync(process.StandardError, log, progress, linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                log.WriteLine($"Timed out after {spec.Timeout}.");
                TryKill(process);
                await Task.WhenAll(SafeAwait(outputTask), SafeAwait(errorTask));
                return new CommandResult(spec.Name, -408, true, DateTimeOffset.Now - started);
            }

            await Task.WhenAll(outputTask, errorTask);
            var result = new CommandResult(spec.Name, process.ExitCode, false, DateTimeOffset.Now - started);
            log.WriteLine($"Exit code: {result.ExitCode}; duration: {result.Duration:g}");
            progress.Report($"Finished: {spec.Name} (exit {result.ExitCode})");
            return result;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            log.WriteLine("Canceled.");
            throw;
        }
        catch (Exception ex)
        {
            log.WriteLine("Command runner error:");
            log.WriteLine(ex.ToString());
            if (!spec.ContinueOnError)
            {
                throw;
            }

            return new CommandResult(spec.Name, -1, false, DateTimeOffset.Now - started);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        LogSession log,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            log.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                progress.Report(line);
            }
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The primary command result already records timeout/cancel context.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
