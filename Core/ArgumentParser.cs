using System;

namespace ProcessFileMonitor.Core
{
    public enum RunMode
    {
        Unknown,
        LaunchClaude,
        LaunchOpenclaw,
        AttachByPid,
        AttachByName
    }

    public class ParsedArgs
    {
        public RunMode Mode { get; init; }
        public int? Pid { get; init; }
        public string? ProcessName { get; init; }
    }

    public static class ArgumentParser
    {
        public static ParsedArgs Parse(string[] args)
        {
            if (args.Length == 0)
            {
                throw new ArgumentException(
                    "No arguments provided. Use 'claude', 'openclaw', '-p <pid>', or '-c <name>'.");
            }

            string first = args[0].Trim().ToLowerInvariant();

            if (first == "claude")
                return new ParsedArgs { Mode = RunMode.LaunchClaude };

            if (first == "openclaw")
                return new ParsedArgs { Mode = RunMode.LaunchOpenclaw };

            if (first == "-p" || first == "--pid")
            {
                if (args.Length < 2)
                    throw new ArgumentException("'-p' requires a PID value.");
                if (!int.TryParse(args[1], out int pid) || pid <= 0)
                    throw new ArgumentException($"Invalid PID: '{args[1]}'");
                return new ParsedArgs { Mode = RunMode.AttachByPid, Pid = pid };
            }

            if (first == "-c" || first == "--name")
            {
                if (args.Length < 2)
                    throw new ArgumentException("'-c' requires a process name.");
                string name = args[1].Trim();
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("Process name cannot be empty.");
                // Strip .exe if provided
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                return new ParsedArgs { Mode = RunMode.AttachByName, ProcessName = name };
            }

            throw new ArgumentException($"Unknown argument: '{args[0]}'");
        }
    }
}
