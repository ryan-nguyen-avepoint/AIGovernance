using ProcessFileMonitor.Logging;
using System.Collections.Frozen;
using System.Runtime.Versioning;

namespace ProcessFileMonitor.Core
{
    [SupportedOSPlatform("windows")]
    public sealed class FilePathFilter
    {
        private readonly Lazy<HashSet<string>> SystemFolders;
        private readonly AuditLogger _logger;

        private static readonly string[] SystemSegmentExcludes =
        [
            @"\dotnet\shared\",
            @"\dotnet\host\",
            @"\.nuget\packages\",
            @"\nuget\",
            @"\$Recycle.Bin\",
            @"\obj\",
            @"\node_modules\",
            @"\.vs",
            @"\.git",
            @"\.svn",
            @"\.hg",
            @"\.vscode",
            @"\.idea",
            @"\.next\",
            @"\.nuxt",
            @"\coverage\",
            @"\dist\",
            @"\build\",
        ];
        private static readonly FrozenSet<string> BlackListFileName = FrozenSet.Create(
            StringComparer.OrdinalIgnoreCase, new[]
            {
                "pagefile.sys",
                "swapfile.sys",
                "hiberfil.sys",
                "ntuser.dat",
                "ntuser.dat.log",
                "ntuser.dat.log1",
                "ntuser.dat.log2",
                "usrclass.dat",
                "usrclass.dat.log1",
                "usrclass.dat.log2",
                "desktop.ini",
                "thumbs.db",
            }
        );
        private static readonly FrozenSet<string> TextFileExtension = FrozenSet.Create(
            StringComparer.OrdinalIgnoreCase, new[]
            {
                ".cs",
                ".csx",
                ".js",
                ".mjs",
                ".cjs",
                ".ts",
                ".tsx",
                ".jsx",
                ".java",
                ".kt",
                ".kts",
                ".go",
                ".rs",
                ".py",
                ".php",
                ".rb",
                ".swift",
                ".dart",
                ".c",
                ".h",
                ".cpp",
                ".cc",
                ".cxx",
                ".hpp",
                ".hh",
                ".vb",
                ".fs",
                ".fsx",
                ".html",
                ".htm",
                ".css",
                ".scss",
                ".sass",
                ".less",
                ".vue",
                ".svelte",
                ".astro",
                ".json",
                ".jsonc",
                ".yaml",
                ".yml",
                ".toml",
                ".ini",
                ".cfg",
                ".conf",
                ".env",
                ".editorconfig",
                ".gitignore",
                ".gitattributes",
                ".npmrc",
                ".yarnrc",
                ".dockerignore",
                ".csproj",
                ".vbproj",
                ".fsproj",
                ".props",
                ".targets",
                ".config",
                ".resx",
                ".nuspec",
                ".xaml",
                ".kubeconfig",
                ".helm",
                ".tf",
                ".tfvars",
                ".bicep",
                ".nomad",
                ".psql",
                ".pgsql",
                ".log",
                ".out",
                ".trace",
                ".etl",
                ".txt",
                ".md",
                ".markdown",
                ".rst",
                ".adoc",
                ".bat",
                ".cmd",
                ".ps1",
                ".psm1",
                ".sh",
                ".bash",
                ".zsh",
                ".fish",
                ".xml",
                ".sql",
                ".sln",
                ".pem",
                ".pub"
            }
        );
        private static readonly FrozenSet<string> WhiteListFileName = FrozenSet.Create(
            StringComparer.OrdinalIgnoreCase,
            new[]
            {
                "Dockerfile",
                "Jenkinsfile",
                "Makefile",
                "LICENSE",
                "README",
                "CHANGELOG",
                "AUTHORS",
                "certs"
            }
        );

        public FilePathFilter(AuditLogger logger)
        {
            _logger = logger;
            SystemFolders = new(BuildSystemFolders);
        }
        bool HasHiddenFolder(string path) => path.Split('\\', '/').Any(x => x.StartsWith(".") && x != "." && x != "..");
        public bool QuickValidateFile(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || Path.EndsInDirectorySeparator(rawPath))
            {
                return false;
            }
            string path = rawPath.ToLower();
            foreach (string systemFolder in SystemFolders.Value)
            {
                if (path.StartsWith(systemFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            foreach (var segment in SystemSegmentExcludes)
            {
                if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            foreach (var name in BlackListFileName)
            {
                if (path.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            if (HasHiddenFolder(path)) return false;
            return true;
        }

        public bool IsValidFile(string rawPath, bool needCheckExist = true)
        {
            string path = NtPathToDrivePath(rawPath.ToLower()).ToLowerInvariant();
            foreach (string systemFolder in SystemFolders.Value)
            {
                if (path.StartsWith(systemFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            if (needCheckExist)
            {
                try
                {
                    if (!File.Exists(path)) return false;
                    var attrs = File.GetAttributes(path);
                    if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) return false;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"[WARN]Can not get file {path} -- {ex}");
                    return false;
                }
            }
            foreach (var name in WhiteListFileName)
            {
                if (path.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            string ext = Path.GetExtension(path);
            if (!string.IsNullOrWhiteSpace(ext) && TextFileExtension.Contains(ext))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Converts NT namespace paths like \Device\HarddiskVolumeN\... to C:\...
        /// Uses QueryDosDevice in reverse (cached at first call).
        /// Falls back to returning the original if no mapping found.
        /// </summary>
        private static string NtPathToDrivePath(string ntPath)
        {
            // Already looks like a drive-letter path
            if (ntPath.Length >= 2 && ntPath[1] == ':')
                return ntPath;
            // Try each drive letter
            foreach (var mapping in DriveLetterCache.Value)
            {
                if (ntPath.StartsWith(mapping.NtPrefix, StringComparison.OrdinalIgnoreCase))
                    return mapping.DriveLetter + ntPath[mapping.NtPrefix.Length..];
            }
            return ntPath; // Return unchanged if we can't map it
        }
        // Lazily built cache of \Device\HarddiskVolumeN -> X: mappings
        private static readonly Lazy<DriveMapping[]> DriveLetterCache = new(BuildDriveCache);
        private static DriveMapping[] BuildDriveCache()
        {
            var result = new List<DriveMapping>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                string letter = drive.Name.TrimEnd('\\'); // e.g. "C:"
                string ntDevice = QueryDosDeviceInternal(letter);
                if (!string.IsNullOrEmpty(ntDevice))
                    result.Add(new DriveMapping(ntDevice, letter + @"\"));
            }
            return [.. result];
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern uint QueryDosDevice(string lpDeviceName, System.Text.StringBuilder lpTargetPath, uint ucchMax);
        private static string QueryDosDeviceInternal(string driveLetter)
        {
            var sb = new System.Text.StringBuilder(260);
            return QueryDosDevice(driveLetter, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : "";
        }
        private record DriveMapping(string NtPrefix, string DriveLetter);

        private static string NormalizeFolder(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        private void Add(HashSet<string> folders, Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    folders.Add(NormalizeFolder(path));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[WARN]Can not parse {folder} -- {ex}");
            }
        }
        private HashSet<string> BuildSystemFolders()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Add(result, Environment.SpecialFolder.Windows);
            Add(result, Environment.SpecialFolder.System);
            Add(result, Environment.SpecialFolder.SystemX86);
            Add(result, Environment.SpecialFolder.Fonts);
            Add(result, Environment.SpecialFolder.Resources);
            Add(result, Environment.SpecialFolder.LocalizedResources);

            Add(result, Environment.SpecialFolder.ProgramFiles);
            Add(result, Environment.SpecialFolder.ProgramFilesX86);
            Add(result, Environment.SpecialFolder.CommonProgramFiles);
            Add(result, Environment.SpecialFolder.CommonProgramFilesX86);

            Add(result, Environment.SpecialFolder.ApplicationData);
            Add(result, Environment.SpecialFolder.LocalApplicationData);
            Add(result, Environment.SpecialFolder.CommonApplicationData);
            Add(result, Environment.SpecialFolder.InternetCache);
            Add(result, Environment.SpecialFolder.Cookies);
            Add(result, Environment.SpecialFolder.History);
            Add(result, Environment.SpecialFolder.Recent);
            Add(result, Environment.SpecialFolder.Templates);

            Add(result, Environment.SpecialFolder.CommonPrograms);
            Add(result, Environment.SpecialFolder.CommonStartup);
            Add(result, Environment.SpecialFolder.CommonStartMenu);
            Add(result, Environment.SpecialFolder.Programs);
            Add(result, Environment.SpecialFolder.Startup);
            Add(result, Environment.SpecialFolder.StartMenu);
            Add(result, Environment.SpecialFolder.SendTo);
            Add(result, Environment.SpecialFolder.NetworkShortcuts);
            Add(result, Environment.SpecialFolder.PrinterShortcuts);

            Add(result, Environment.SpecialFolder.AdminTools);
            Add(result, Environment.SpecialFolder.CommonAdminTools);

            return result;
        }
    }
}
