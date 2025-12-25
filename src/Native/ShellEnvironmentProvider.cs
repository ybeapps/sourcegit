using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    /// <summary>
    /// Provides shell environment capture functionality for git commands.
    /// This enables tools like nvm, rbenv, pyenv to work correctly by sourcing
    /// the user's shell profile and capturing environment variables.
    /// </summary>
    public static class ShellEnvironmentProvider
    {
        private class EnvironmentCache
        {
            public Dictionary<string, string> Variables { get; set; }
            public DateTime CapturedAt { get; set; }
            public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(30);

            public bool IsStale => DateTime.Now - CapturedAt > MaxAge;
        }

        private static readonly Dictionary<string, EnvironmentCache> _cache = new();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Detects the user's default shell.
        /// </summary>
        /// <returns>Path to shell executable, or null if not detected</returns>
        public static string DetectUserShell()
        {
            if (OperatingSystem.IsWindows())
                return null; // Not supported on Windows

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                // 1. Check SHELL environment variable
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
                    return shell;

                // 2. Read from /etc/passwd
                var username = Environment.UserName;
                var passwdPath = "/etc/passwd";
                if (File.Exists(passwdPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(passwdPath);
                        foreach (var line in lines)
                        {
                            if (line.StartsWith(username + ":"))
                            {
                                var parts = line.Split(':');
                                if (parts.Length >= 7)
                                {
                                    var shellPath = parts[6].Trim();
                                    if (File.Exists(shellPath))
                                        return shellPath;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors reading passwd file
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets or captures shell environment synchronously.
        /// Uses cache if available and not stale.
        /// </summary>
        /// <param name="repoPath">Repository path</param>
        /// <param name="customShell">Optional custom shell path</param>
        /// <param name="customArgs">Optional custom shell arguments</param>
        /// <returns>Environment variables dictionary, or null on failure</returns>
        public static Dictionary<string, string> GetOrCaptureSync(
            string repoPath,
            string customShell = null,
            string customArgs = null)
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(repoPath, out var cached) && !cached.IsStale)
                    return cached.Variables;
            }

            // If not cached or stale, capture synchronously
            try
            {
                var task = CaptureEnvironmentAsync(repoPath, customShell, customArgs);

                // Wait with timeout to avoid hanging
                if (task.Wait(TimeSpan.FromSeconds(5)))
                {
                    var env = task.Result;
                    if (env != null)
                    {
                        lock (_cacheLock)
                        {
                            _cache[repoPath] = new EnvironmentCache
                            {
                                Variables = env,
                                CapturedAt = DateTime.Now
                            };
                        }
                    }
                    return env;
                }
            }
            catch
            {
                // Ignore errors, fall back to null
            }

            return null;
        }

        /// <summary>
        /// Invalidates cached environment for a repository.
        /// </summary>
        /// <param name="repoPath">Repository path</param>
        public static void InvalidateCache(string repoPath)
        {
            lock (_cacheLock)
            {
                _cache.Remove(repoPath);
            }
        }

        /// <summary>
        /// Captures environment by running shell in the repository directory.
        /// </summary>
        private static async Task<Dictionary<string, string>> CaptureEnvironmentAsync(
            string repoPath,
            string customShell = null,
            string customArgs = null)
        {
            // Detect shell
            var shell = customShell;
            if (string.IsNullOrEmpty(shell))
            {
                shell = DetectUserShell();
                if (string.IsNullOrEmpty(shell))
                    return null;
            }

            // Validate shell exists
            if (!File.Exists(shell))
                return null;

            // Determine shell arguments
            var shellName = Path.GetFileName(shell).ToLower(CultureInfo.InvariantCulture);
            var args = customArgs;

            if (string.IsNullOrEmpty(args))
            {
                // Default: login shell
                args = shellName switch
                {
                    "zsh" => "-l",
                    "bash" => "-l",
                    "fish" => "-l",
                    _ => "-l"
                };
            }

            // Build command: cd to repo, then print environment
            // Use single quotes to avoid variable expansion issues
            var cdPath = repoPath.Replace("'", "'\\''"); // Escape single quotes
            var command = $"{args} -c 'cd \"{cdPath}\" && env'";

            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            try
            {
                using var proc = Process.Start(startInfo);
                if (proc == null)
                    return null;

                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                    return null;

                // Parse environment output: KEY=VALUE lines
                var env = new Dictionary<string, string>();
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = line.Substring(0, idx);
                        var value = line.Substring(idx + 1);

                        // Skip empty keys
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        env[key] = value;
                    }
                }

                return env.Count > 0 ? env : null;
            }
            catch
            {
                // Log error but don't crash - gracefully degrade
                return null;
            }
        }
    }
}
