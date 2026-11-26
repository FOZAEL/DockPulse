using System;
using System.Diagnostics;
using System.IO;
using DockPulse.Helpers;

namespace DockPulse.Services;

/// <summary>
/// Remembers one "working folder" (saved in workfolder.cfg) and opens it in File Explorer, VS Code or a terminal.
/// </summary>
public sealed class WorkingFolderService
{
    private readonly object _lock = new();
    private readonly string _configPath;
    private string _folderPath;

    public WorkingFolderService()
    {
        _configPath = DockPulseHelpers.GetSettingsFilePath("workfolder.cfg");
        _folderPath = LoadPath() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public event Action? Changed;

    public string FolderPath
    {
        get
        {
            lock (_lock)
            {
                return _folderPath;
            }
        }
    }

    /// <summary>Turns user input (quotes, %VARS%, trailing slashes, "C:") into a full path.</summary>
    public static bool TryNormalize(string? raw, out string path)
    {
        path = string.Empty;
        string trimmed = (raw ?? string.Empty).Trim().Trim('"').Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (expanded.Length == 2 && expanded[1] == ':')
            {
                expanded += Path.DirectorySeparatorChar;
            }

            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetFolder(string path)
    {
        lock (_lock)
        {
            _folderPath = path;
            try
            {
                File.WriteAllText(_configPath, $"path={path}{Environment.NewLine}");
            }
            catch
            {
                // Keep the new folder for this session even if it can't be saved
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Last folder name ("DockPulse"), or the drive itself for a drive root.</summary>
    public static string GetDisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Parent folder ("C:\Dev"), or "Drive" for a drive root.</summary>
    public static string GetParentDisplay(string path)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(parent) ? "Drive" : parent;
    }

    public static bool OpenInExplorer(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", QuoteArgument(path)) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenInVsCode(string path)
    {
        string? code = FindVsCode();
        if (code is null || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(code) { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Windows Terminal in the folder, or PowerShell if Windows Terminal isn't installed.</summary>
    public static bool OpenInTerminal(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d {QuoteArgument(path)}") { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Windows Terminal missing: fall back to a PowerShell window
        }

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = path });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindVsCode()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to PATH: VS Code puts ...\Microsoft VS Code\bin (code.cmd) there; Code.exe is one level up
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "code.cmd")))
                {
                    string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
                    if (parent is not null && File.Exists(Path.Combine(parent, "Code.exe")))
                    {
                        return Path.Combine(parent, "Code.exe");
                    }
                }
            }
            catch
            {
                // Skip malformed PATH entries
            }
        }

        return null;
    }

    // Quote for a Windows command line; a trailing backslash (drive root "C:\") would escape the closing quote
    private static string QuoteArgument(string path) =>
        path.EndsWith('\\') ? $"\"{path}.\"" : $"\"{path}\"";

    private string? LoadPath()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(_configPath))
            {
                if (line.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line["path=".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch
        {
            // Fall back to the default folder
        }

        return null;
    }
}
