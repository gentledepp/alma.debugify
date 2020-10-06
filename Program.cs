using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CommandLine;

namespace nuget.debugify
{
    class Program
    {
        [Verb("debug", HelpText="Replaces dlls of a given project in the local nuget .package cache with a debuggable one")]
        public class DebugCommand
        {
            [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose{ get; set; }

            [Option('p', "path", Required = false, HelpText = "Path to *.csproj or *.sln file")]
            public string Path { get; set; }

            [Option('v', "version", Required=false,HelpText = "Specify the version you'd like to debugify")]
            public string Version { get; set; }
        }

        [Verb("cleanup", HelpText = "Removes all debuggable dlls from the cache so that the original can be resolved again")]
        public class CleanupCommand
        {}


        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<DebugCommand, CleanupCommand>(args)
                .WithParsed<DebugCommand>(Debugify)
                .WithParsed<CleanupCommand>(Cleanup);

        }

        private static void Debugify(DebugCommand debugCommand)
        {
            string path;
            if (string.IsNullOrEmpty(debugCommand.Path))
            {
                if(debugCommand.Verbose) Debug($"No path provided. Will run in current directory: {Environment.CurrentDirectory}");
                path = Environment.CurrentDirectory;
            }
            else
            {
                if(debugCommand.Verbose) Debug($"Path: {debugCommand.Path}");
                path = debugCommand.Path;
            }
        
            // replace version in csproj
            var version = EnsureVersion(path, debugCommand);
            
            // pack the thing up
            DotnetPack(path);

            // find it within the current solution
            var solutionDir = File.Exists(path) && string.Equals(Path.GetExtension(path), ".sln",
                StringComparison.InvariantCultureIgnoreCase)
                ? path
                : RecursivelyFindSolutionDir(path, debugCommand.Verbose);
            if (solutionDir is null)
                return;
            if(debugCommand.Verbose) Debug($"Found sln directory: {solutionDir}");


            var packageId = ResolveNugetFileName(path, debugCommand);
            var packageName = $"{packageId}.{version}.symbols.nupkg";
            var packagePath = Directory.EnumerateFiles(solutionDir, "*.nupkg", SearchOption.AllDirectories)
                .FirstOrDefault(p => string.Equals(packageName, Path.GetFileName(p)));

            if (packagePath is null)
            {
                Error($"Could not find package {packageName}");
                return;
            }

            // verify, that there is a nuget package with the same id and version in the cache
            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var packageCachePath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if(debugCommand.Verbose) Info($"nuget package cache fount at '{packageCachePath}'");

            var extractPath = Path.Combine(packageCachePath, packageId, version);

            if (!Directory.Exists(extractPath))
            {
                Warning($"Cannot debugify {packageName} as the package cannot be found in the cache: {extractPath}");
                return;
            }
            
            // add pseudo file to know when do delete such a folder
            if(debugCommand.Verbose) Debug($"Writing .debugified.txt to {extractPath}");
            File.WriteAllText(Path.Combine(extractPath, ".debugified.txt"),  "this package was debugified");

            using var archive = new ZipArchive(File.OpenRead(packagePath));
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("lib") || entry.FullName.StartsWith("src"))
                {
                    if (debugCommand.Verbose) Debug($"Extracting {entry.FullName}");

                    // Gets the full path to ensure that relative segments are removed.
                    string destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));

                    // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that
                    // are case-insensitive.
                    if (destinationPath.StartsWith(extractPath, StringComparison.Ordinal))
                    {
                        // ensure the subdirectories exist
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
                else
                {
                    if (debugCommand.Verbose) Debug($"Skipping {entry.FullName} as it does not start with 'src' or 'lib'");
                }

            }

            Success($"Successfully debugified {packageId} version {version}");
        }

        private static string RecursivelyFindSolutionDir(string path, bool verboseLogging)
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

            if(verboseLogging) Debug("Searching sln directory");

            string sln = null;
            for (int i = 0; i < 10; i++)
            {
                sln = Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                
                // if solution is not found within the current folder, proceed with the next parent folder
                if (sln is null)
                {
                    if(verboseLogging) Debug($"\tNo sln in '{dir}'");
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    sln = Path.GetDirectoryName(sln);
                    break;
                }
            }

            if (sln is null)
                Error("Could not find any *.sln file within {path} or any of its 10 parent folders!");

            return sln;
        }

        private static string EnsureVersion(string path, DebugCommand debugCommand)
        {
            var csproj = path;
            var hasVersionSpecified = !string.IsNullOrEmpty(debugCommand.Version);
            var isCsprojFile = File.Exists(path) && string.Equals(Path.GetExtension(path), ".csproj",
                StringComparison.InvariantCultureIgnoreCase);
            if (!isCsprojFile)
                csproj = Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();

            if(csproj == null)
                throw new InvalidOperationException("requires csproj");

            var findVersion = new Regex(@"<Version>\s*(?<version>\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?)\s*</Version>");

            var txt = File.ReadAllText(csproj);

            var m = findVersion.Match(txt);
            if(!m.Success)
                throw new InvalidOperationException("No version element found");

            var versionElement = m.Value;

            if(hasVersionSpecified)
            {
                File.WriteAllText(csproj, txt.Replace(versionElement, $"<Version>{debugCommand.Version}</Version>"));
                return debugCommand.Version;
            }

            return m.Groups["version"].Value;
        }

        private static string ResolveNugetFileName(string path, DebugCommand debugCommand)
        {
            var csproj = path;
            var isCsprojFile = File.Exists(csproj) && string.Equals(Path.GetExtension(csproj), ".csproj",
                StringComparison.InvariantCultureIgnoreCase);
            if (!isCsprojFile)
                csproj = Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();

            if(csproj == null)
                throw new InvalidOperationException("requires csproj");

            var findPackageId = new Regex(@"<PackageId>(?<packageId>[\w\.]+)</PackageId>");
            var m = findPackageId.Match(File.ReadAllText(csproj));
            if (m.Success)
                return $"{m.Groups["packageId"].Value}.{debugCommand.Version}";

            return Path.GetFileNameWithoutExtension(csproj);
        }

        private static void DotnetPack(string path)
        {
            // dotnet pack --include-symbols --include-source -c Debug --force
            // see: https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-pack
            var pi = new ProcessStartInfo("dotnet", "pack --include-symbols --include-source -c Debug --force");
            pi.RedirectStandardError = true;
            pi.RedirectStandardOutput = true;

            // if the user specified just a folder, set it as working dir
            if (Directory.Exists(path))
                pi.WorkingDirectory = path;

            var process = Process.Start(pi);
            process.EnableRaisingEvents = true;
            process.WaitForExit();
        }

        private static void Cleanup(CleanupCommand cleanupCommand)
        {
            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var extractPath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if (!Directory.Exists(extractPath))
            {
                Warning($"Could not find nuget package cache at {extractPath}");
                return;
            }

            var count = 0;
            // add pseudo file to know when do delete such a folder
            foreach (var file in Directory.GetFiles(extractPath, ".debugified.txt", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file);
                Info($"Undebugifying {dir.Substring(extractPath.Length, dir.Length-extractPath.Length)}");
                Directory.Delete(dir, true);
                count++;
            }

            if (count == 0)
                Success("All good. Nothing to undebugify");
            else
                Success($"Cleaned up {count} debugified packages");
        }

        private static void Debug(string message)
        {
            WriteWithColor(message, ConsoleColor.DarkGray);
        }
        private static void Info(string message)
        {
            WriteWithColor(message, ConsoleColor.White);
        }
        private static void Success(string message)
        {
            WriteWithColor(message, ConsoleColor.Green);
        }
        private static void Warning(string message)
        {
            WriteWithColor(message, ConsoleColor.DarkYellow);
        }
        private static void Error(string message)
        {
            WriteWithColor(message, ConsoleColor.Red);
        }

        private static void WriteWithColor(string message, ConsoleColor? foreground = null, ConsoleColor? background = null)
        {
            var c = Console.ForegroundColor;
            var b = Console.BackgroundColor;
            if(foreground.HasValue)
                Console.ForegroundColor = foreground.Value;
            if(background.HasValue)
                Console.BackgroundColor = background.Value;
            Console.WriteLine(message);
            Console.ForegroundColor = c;
            Console.BackgroundColor = b;
        }
    }
}
