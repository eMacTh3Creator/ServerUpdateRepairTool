using System.IO.Compression;

namespace WinUpdateRepairTool;

internal sealed class LogSession
{
    private readonly object _gate = new();

    public LogSession()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ServerUpdateRepairTool",
            "Logs");

        RootPath = Path.Combine(basePath, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(RootPath);
        TranscriptPath = Path.Combine(RootPath, "transcript.log");

        WriteSection("Session started");
        WriteLine($"Computer: {Environment.MachineName}");
        WriteLine($"User: {Environment.UserDomainName}\\{Environment.UserName}");
        WriteLine($"OS: {Environment.OSVersion}");
        WriteLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        WriteLine($"Process: {Application.ExecutablePath}");
        WriteLine($"Log root: {RootPath}");
    }

    public string RootPath { get; }

    public string TranscriptPath { get; }

    public void WriteSection(string title)
    {
        WriteLine("");
        WriteLine("============================================================");
        WriteLine(title);
        WriteLine("============================================================");
    }

    public void WriteLine(string text)
    {
        lock (_gate)
        {
            File.AppendAllText(TranscriptPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
    }

    public string WriteTextFile(string relativePath, string contents)
    {
        var path = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        WriteLine($"Wrote {path}");
        return path;
    }

    public void CopyIfExists(string sourcePath, string relativeFolder, long maxBytes = 512L * 1024L * 1024L)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                WriteLine($"Missing: {sourcePath}");
                return;
            }

            var info = new FileInfo(sourcePath);
            if (info.Length > maxBytes)
            {
                WriteLine($"Skipped large file ({info.Length:n0} bytes): {sourcePath}");
                return;
            }

            var targetFolder = Path.Combine(RootPath, relativeFolder);
            Directory.CreateDirectory(targetFolder);
            var targetPath = Path.Combine(targetFolder, SanitizeFileName(info.Name));
            File.Copy(sourcePath, targetPath, overwrite: true);
            WriteLine($"Copied {sourcePath} to {targetPath}");
        }
        catch (Exception ex)
        {
            WriteLine($"Copy failed for {sourcePath}: {ex.Message}");
        }
    }

    public void CopyPattern(string folder, string pattern, string relativeFolder, int maxFiles = 25, long maxBytes = 256L * 1024L * 1024L)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                WriteLine($"Missing folder: {folder}");
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, pattern).OrderByDescending(File.GetLastWriteTimeUtc).Take(maxFiles))
            {
                CopyIfExists(file, relativeFolder, maxBytes);
            }
        }
        catch (Exception ex)
        {
            WriteLine($"Copy pattern failed for {folder}\\{pattern}: {ex.Message}");
        }
    }

    public string CreateZip()
    {
        var zipPath = RootPath.TrimEnd(Path.DirectorySeparatorChar) + ".zip";
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(RootPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        WriteLine($"Created zip: {zipPath}");
        return zipPath;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
