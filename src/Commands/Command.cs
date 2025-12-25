using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Args { get; set; } = string.Empty;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            Log?.AppendLine($"$ git {Args}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            var captured = new CapturedProcess() { Process = proc };
            var capturedLock = new object();
            try
            {
                proc.Start();

                // Not safe, please only use `CancellationToken` in readonly commands.
                if (CancellationToken.CanBeCanceled)
                {
                    CancellationToken.Register(() =>
                    {
                        lock (capturedLock)
                        {
                            if (captured is { Process: { HasExited: false } })
                                captured.Process.Kill();
                        }
                    });
                }
            }
            catch (Exception e)
            {
                if (RaiseError)
                    App.RaiseException(Context, e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HandleOutput(e.Message, errs);
            }

            lock (capturedLock)
            {
                captured.Process = null;
            }

            Log?.AppendLine(string.Empty);

            if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
            {
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        App.RaiseException(Context, errMsg);
                }

                return false;
            }

            return true;
        }

        protected Result ReadToEnd()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = proc.StandardOutput.ReadToEnd();
            rs.StdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected async Task<Result> ReadToEndAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = await proc.StandardOutput.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            rs.StdErr = await proc.StandardError.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var start = new ProcessStartInfo();
            start.FileName = Native.OS.GitExecutable;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Try to get shell environment if enabled
            var shellEnv = TryGetShellEnvironment();
            if (shellEnv != null)
            {
                // Populate environment from captured shell
                foreach (var kvp in shellEnv)
                {
                    start.Environment[kvp.Key] = kvp.Value;
                }
            }

            // Force using this app as SSH askpass program (overrides shell env if present)
            var selfExecFile = Process.GetCurrentProcess().MainModule!.FileName;
            start.Environment["SSH_ASKPASS"] = selfExecFile; // Can not use parameter here, because it invoked by SSH with `exec`
            start.Environment["SSH_ASKPASS_REQUIRE"] = "prefer";
            start.Environment["SOURCEGIT_LAUNCH_AS_ASKPASS"] = "TRUE";
            if (!OperatingSystem.IsLinux())
                start.Environment["DISPLAY"] = "required";

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                start.Environment["GIT_SSH_COMMAND"] = $"ssh -i '{SSHKey}' -F '/dev/null'";

            // Force using en_US.UTF-8 locale
            if (OperatingSystem.IsLinux())
            {
                start.Environment["LANG"] = "C";
                start.Environment["LC_ALL"] = "C";
            }

            var builder = new StringBuilder(2048);
            builder
                .Append("--no-pager -c core.quotepath=off -c credential.helper=")
                .Append(Native.OS.CredentialHelper)
                .Append(' ');

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);
            start.Arguments = builder.ToString();

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private Dictionary<string, string> TryGetShellEnvironment()
        {
            // Only on macOS/Linux
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                return null;

            // Need working directory
            if (string.IsNullOrEmpty(WorkingDirectory))
                return null;

            // Load repository settings
            var settings = LoadRepositorySettings(WorkingDirectory);
            if (settings == null || !settings.UseShellEnvironment)
                return null;

            // Get or capture environment (uses cache)
            try
            {
                return Native.ShellEnvironmentProvider.GetOrCaptureSync(
                    WorkingDirectory,
                    settings.CustomShellPath,
                    settings.CustomShellArgs
                );
            }
            catch
            {
                return null;
            }
        }

        private static Models.RepositorySettings LoadRepositorySettings(string repoPath)
        {
            try
            {
                // Find git directory
                var gitDir = Path.Combine(repoPath, ".git");

                // If .git is a file (worktree), read the gitdir path
                if (File.Exists(gitDir))
                {
                    var gitDirContent = File.ReadAllText(gitDir).Trim();
                    if (gitDirContent.StartsWith("gitdir: "))
                    {
                        gitDir = gitDirContent.Substring(8).Trim();
                        if (!Path.IsPathRooted(gitDir))
                            gitDir = Path.Combine(repoPath, gitDir);
                    }
                }

                if (!Directory.Exists(gitDir))
                    return null;

                // Check for common directory (worktree)
                var commonDirFile = Path.Combine(gitDir, "commondir");
                var gitCommonDir = gitDir;

                if (File.Exists(commonDirFile))
                {
                    var commonDir = File.ReadAllText(commonDirFile).Trim();
                    if (!Path.IsPathRooted(commonDir))
                        commonDir = Path.GetFullPath(Path.Combine(gitDir, commonDir));
                    gitCommonDir = commonDir;
                }

                // Load settings file
                var settingsFile = Path.Combine(gitCommonDir, "sourcegit.settings");
                if (!File.Exists(settingsFile))
                    return null;

                using var stream = File.OpenRead(settingsFile);
                return JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositorySettings);
            }
            catch
            {
                return null;
            }
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
