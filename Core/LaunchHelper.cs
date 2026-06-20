using System;
using System.Diagnostics;
using System.IO;
using ProcessFileMonitor.Logging;

namespace ProcessFileMonitor.Core
{
    /// <summary>
    /// Resolves executable paths for Claude and Openclaw using environment variables,
    /// then builds a ProcessStartInfo for launching them.
    /// </summary>
    public static class LaunchHelper
    {
        // Environment variables checked (in priority order)
        private static readonly string[] ClaudeEnvVars =
        [
            "CLAUDE_EXECUTABLE",    // Explicit override
            "CLAUDE_PATH",          // Common custom var
            "CLAUDE_HOME",          // Fallback: home dir, exe inside
        ];

        private static readonly string[] OpenclawEnvVars =
        [
            "OPENCLAW_EXECUTABLE",
            "OPENCLAW_PATH",
            "OPENCLAW_HOME",
        ];

        // Known default install locations to try if env vars are empty
        private static readonly string[] ClaudeDefaults =
        [
            @"%LOCALAPPDATA%\AnthropicClaude\claude.exe",
            @"%PROGRAMFILES%\Anthropic\Claude\claude.exe",
            @"%PROGRAMFILES(X86)%\Anthropic\Claude\claude.exe",
            @"%APPDATA%\Claude\claude.exe",
        ];

        private static readonly string[] OpenclawDefaults =
        [
            @"%LOCALAPPDATA%\Openclaw\openclaw.exe",
            @"%PROGRAMFILES%\Openclaw\openclaw.exe",
        ];

        public static ProcessStartInfo? BuildClaudeLaunchInfo(AuditLogger logger)
        {
            string? exe = ResolveExecutable("claude", ClaudeEnvVars, ClaudeDefaults, logger);
            if (exe == null) return null;

            logger.LogInfo($"[LaunchHelper] Resolved Claude executable: {exe}");
            return new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false,
            };
        }

        public static ProcessStartInfo? BuildOpenclawLaunchInfo(AuditLogger logger)
        {
            string? exe = ResolveExecutable("openclaw", OpenclawEnvVars, OpenclawDefaults, logger);
            if (exe == null) return null;

            logger.LogInfo($"[LaunchHelper] Resolved Openclaw executable: {exe}");
            return new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false,
            };
        }

        private static string? ResolveExecutable(
            string appName,
            string[] envVars,
            string[] defaults,
            AuditLogger logger)
        {
            // 1) Try environment variables
            foreach (var envVar in envVars)
            {
                string? val = Environment.GetEnvironmentVariable(envVar);
                if (string.IsNullOrWhiteSpace(val)) continue;

                string expanded = Environment.ExpandEnvironmentVariables(val);
                logger.LogDebug($"[LaunchHelper] Checking env var {envVar} = '{expanded}'");

                // If it's a directory, look for an exe inside
                if (Directory.Exists(expanded))
                {
                    string candidate = Path.Combine(expanded, $"{appName}.exe");
                    if (File.Exists(candidate))
                    {
                        logger.LogInfo($"[LaunchHelper] Found via env var {envVar} (directory): {candidate}");
                        return candidate;
                    }
                }
                else if (File.Exists(expanded))
                {
                    logger.LogInfo($"[LaunchHelper] Found via env var {envVar}: {expanded}");
                    return expanded;
                }
                else
                {
                    logger.LogWarning($"[LaunchHelper] Env var {envVar} points to non-existent path: {expanded}");
                }
            }

            // 2) Try PATH (system-wide lookup)
            string? onPath = FindOnPath(appName, logger);
            if (onPath != null) return onPath;

            // 3) Try default install locations
            foreach (var defaultPath in defaults)
            {
                string expanded = Environment.ExpandEnvironmentVariables(defaultPath);
                logger.LogDebug($"[LaunchHelper] Checking default: {expanded}");
                if (File.Exists(expanded))
                {
                    logger.LogInfo($"[LaunchHelper] Found at default location: {expanded}");
                    return expanded;
                }
            }

            logger.LogError($"[LaunchHelper] Could not find '{appName}' executable.");
            logger.LogError($"  Set one of these environment variables to the path:");
            foreach (var v in envVars)
                logger.LogError($"    {v}=C:\\path\\to\\{appName}.exe");

            return null;
        }

        private static string? FindOnPath(string exeName, AuditLogger logger)
        {
            string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), exeName + ext);
                    if (File.Exists(candidate))
                    {
                        logger.LogInfo($"[LaunchHelper] Found '{exeName}' on PATH: {candidate}");
                        return candidate;
                    }
                }
            }
            return null;
        }
    }
}
