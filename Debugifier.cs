using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CommandLine;
using Spectre.Console;

namespace alma.debugify
{
    [Verb("setup", isDefault:true, HelpText="Build your projects in debug mode and replace DLLs in the NuGet cache. Allows you to step through your package code while debugging applications that consume it.")]
    public class DebugCommand
    {
        [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose{ get; set; }

        [Option('p', "path", Required = false, HelpText = "Path to *.csproj file or a folder that contains it (directly or in subfolder(s))")]
        public string Path { get; set; }

        [Option('v', "version", Required=false,HelpText = "Specify the version you'd like to debugify")]
        public string Version { get; set; }

        [Option('c', "configuration", Required=false, Default = "Debug", HelpText = "Build configuration (Debug or Release). Default is Debug.")]
        public string Configuration { get; set; }

        [Option('r', "rebuild", Required=false, Default = false, HelpText = "Force a full rebuild of projects (slower but ensures fresh DLLs). Default is false.")]
        public bool Rebuild { get; set; }

        [Option( "buildargs", Required=false,HelpText = "Additional arguments for dotnet build. e.g. \" --no-restore\"")]
        public string BuildArguments { get; set; }
    }

    internal class Debugifier
    {
        private readonly ILogger _logger;

        public Debugifier(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        public void Debugify(DebugCommand cmd)
        {
            if (!string.IsNullOrWhiteSpace(cmd.Version))
            {
                var versionValidator = new Regex(@"\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?");
                if (!versionValidator.IsMatch(cmd.Version))
                {
                    _logger.Error($"Error: '{cmd.Version}' is not a valid nuget package version");
                    return;
                }
            }

            if (string.IsNullOrEmpty(cmd.Path))
            {
                if(cmd.Verbose) _logger.Debug($"No path provided. Will run in current directory: {Environment.CurrentDirectory}");
                cmd.Path = Environment.CurrentDirectory;
            }
            else 
            {
                if (!Path.IsPathRooted(cmd.Path))
                    cmd.Path = Path.GetFullPath(cmd.Path);
                
                if (cmd.Verbose) _logger.Debug($"Path: {cmd.Path}");

            }

            // find it within the current solution
            var solutionDir = File.Exists(cmd.Path) && string.Equals(Path.GetExtension(cmd.Path), ".sln",
                StringComparison.InvariantCultureIgnoreCase)
                ? Path.GetDirectoryName(cmd.Path)
                : RecursivelyFindSolutionDir(cmd.Path, cmd.Verbose);
            if (solutionDir is null)
            {
                _logger.Warning($"Could not find any solution in '{(File.Exists(cmd.Path) ? Path.GetDirectoryName(cmd.Path) : cmd.Path)}'");
                return;
            }
            if(cmd.Verbose) _logger.Debug($"Found sln directory: {solutionDir}");

            // find all compatible project files
            var projectFiles = EnumerateDebugifiableProjects(cmd).ToList();
            if (!projectFiles.Any())
            {
                _logger.Warning($"Could not find any debugifiable *.csproj files in '{(File.Exists(cmd.Path) ? Path.GetDirectoryName(cmd.Path) : cmd.Path)}'");
                return;
            }

            // resolve nuget package cache early to filter projects
            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var packageCachePath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if(cmd.Verbose) _logger.Info($"nuget package cache found at '{packageCachePath}'");

            // filter to only projects that exist in the cache (avoid unnecessary builds)
            var projectsInCache = projectFiles.Where(p =>
            {
                var packageBasePath = Path.Combine(packageCachePath, p.PackageId);
                var existsInCache = Directory.Exists(packageBasePath);

                if (!existsInCache && cmd.Verbose)
                    _logger.Debug($"Skipping {p.PackageId} - not found in cache at {packageBasePath}");

                return existsInCache;
            }).ToList();

            if (!projectsInCache.Any())
            {
                _logger.Warning($"None of the {projectFiles.Count} project(s) were found in the nuget package cache. Please restore packages first.");
                return;
            }

            if (projectsInCache.Count < projectFiles.Count)
            {
                _logger.Info($"Building {projectsInCache.Count} of {projectFiles.Count} project(s) (skipping projects not in cache)");
            }

            // Display operation settings
            var settingsTable = new Table()
                .BorderColor(Color.Grey)
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("[cyan]Setting[/]").LeftAligned())
                .AddColumn(new TableColumn("[white]Value[/]").LeftAligned())
                .AddRow("[grey]Configuration[/]", $"[yellow]{cmd.Configuration}[/]")
                .AddRow("[grey]Rebuild[/]", cmd.Rebuild ? "[green]Enabled[/]" : "[dim]Disabled[/]")
                .AddRow("[grey]Projects to build[/]", $"[cyan]{projectsInCache.Count}[/] of [white]{projectFiles.Count}[/]");

            AnsiConsole.Write(settingsTable);
            AnsiConsole.WriteLine();

            // build the projects in Debug configuration
            var buildFailedCount = 0;
            var erroredCsprojInfos = new List<CsprojInfo>();

            // make sure the version files are restored afterwards
            using (var cd = new CompositeDisposable(_logger))
            {

                // replace version in csproj files if specified
                if (!string.IsNullOrWhiteSpace(cmd.Version))
                {
                    foreach (var projectFile in projectsInCache)
                    {
                        try
                        {
                            if(cmd.Verbose) _logger.Debug($"changing package version of {Path.GetFileName(projectFile.Path)}");

                            cd.Add(projectFile.ReplaceVersion(cmd.Version));
                        }
                        catch (Exception x)
                        {
                            erroredCsprojInfos.Add(projectFile);
                            buildFailedCount++;

                            _logger.Error(x.Message);
                        }
                    }
                }

                var projectsToBuild = projectsInCache.Except(erroredCsprojInfos).ToList();

                AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(new ProgressColumn[]
                    {
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn(),
                    })
                    .Start(ctx =>
                    {
                        var task = ctx.AddTask($"[cyan]Building {projectsToBuild.Count} project(s)[/]", maxValue: projectsToBuild.Count);

                        foreach (var projectFile in projectsToBuild)
                        {
                            task.Description = $"[cyan]Building[/] [white]{Path.GetFileName(projectFile.Path)}[/]";

                            if (!DotnetBuild(projectFile, cmd))
                            {
                                _logger.Error($"dotnet build failed for {Path.GetFileName(projectFile.Path)}");
                                buildFailedCount++;
                                erroredCsprojInfos.Add(projectFile);
                            }

                            task.Increment(1);
                        }

                        task.Description = buildFailedCount == 0
                            ? $"[green]Built {projectsToBuild.Count} project(s) successfully[/]"
                            : $"[yellow]Built {projectsToBuild.Count - buildFailedCount}/{projectsToBuild.Count} project(s) successfully[/]";
                    });

                if (buildFailedCount != 0)
                    _logger.Warning(
                        $"WARNING: Failed to build {buildFailedCount} of {projectsInCache.Count} projects");
            }

            // find and replace DLLs in the package cache with built binaries
            var projectsToDebugify = projectsInCache.Except(erroredCsprojInfos).ToList();
            var totalReplacedCount = 0;

            AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                })
                .Start(ctx =>
                {
                    var task = ctx.AddTask($"[cyan]Debugifying {projectsToDebugify.Count} package(s)[/]", maxValue: projectsToDebugify.Count);

                    foreach (var projectFile in projectsToDebugify)
                    {
                        task.Description = $"[cyan]Debugifying[/] [white]{projectFile.PackageId}[/]";

                        // find bin\<Configuration> folder for this project
                        var projectDir = Path.GetDirectoryName(projectFile.Path);
                        var binConfigPath = Path.Combine(projectDir, "bin", cmd.Configuration);

                        if (!Directory.Exists(binConfigPath))
                        {
                            _logger.Error($"Could not find bin\\{cmd.Configuration} folder for {projectFile.PackageId}");
                            task.Increment(1);
                            continue;
                        }

                        // find all DLLs in bin\<Configuration> (recursively to handle different frameworks)
                        var builtDlls = Directory.EnumerateFiles(binConfigPath, "*.dll", SearchOption.AllDirectories).ToList();
                        var builtPdbs = Directory.EnumerateFiles(binConfigPath, "*.pdb", SearchOption.AllDirectories).ToList();

                        if (!builtDlls.Any())
                        {
                            _logger.Warning($"No DLLs found in {binConfigPath}");
                            task.Increment(1);
                            continue;
                        }

                        if(cmd.Verbose) _logger.Debug($"Found {builtDlls.Count} DLL(s) in {binConfigPath}");

                        // replace DLLs in all versions of the package (or specific version if provided)
                        var packageBasePath = Path.Combine(packageCachePath, projectFile.PackageId);
                        var replacedCount = FindAndReplaceDlls(cmd, packageBasePath, builtDlls, builtPdbs, projectFile.PackageId);

                        if (replacedCount > 0)
                        {
                            _logger.Success($"Successfully debugified {projectFile.PackageId} - replaced {replacedCount} file(s)");
                            totalReplacedCount += replacedCount;
                        }
                        else
                            _logger.Warning($"No matching DLLs found in cache for {projectFile.PackageId}");

                        task.Increment(1);
                    }

                    task.Description = totalReplacedCount > 0
                        ? $"[green]Debugified {projectsToDebugify.Count} package(s) - {totalReplacedCount} file(s) replaced[/]"
                        : $"[yellow]Processed {projectsToDebugify.Count} package(s) - no files replaced[/]";
                });
            
        }


        private IEnumerable<CsprojInfo> EnumerateDebugifiableProjects(DebugCommand cmd)
        {
            var path = cmd.Path;
            var isCsprojFile = File.Exists(path) && string.Equals(Path.GetExtension(path), ".csproj", StringComparison.InvariantCultureIgnoreCase);
            if (isCsprojFile)
            {
                // by default, ignore Test projects by convention
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Debug(
                        $"Project file {Path.GetFileName(path)} seems to be a test project and is therefore ignored");
                }
                else
                {
                    var csproj = path;
                    if (TryGetVersion(csproj, out string version) || !string.IsNullOrWhiteSpace(cmd.Version))
                    {
                        // to be sure that the csproj is nuget-compatible, ensure it is the new csproj format
                        if (!File.ReadLines(csproj).First().Trim().StartsWith("<Project Sdk="))
                            _logger.Warning(
                                $"Project file {Path.GetFileName(csproj)} is not supported: Only latest csproj format is supported");
                        else
                        {
                            var packageId = GetPackageId(csproj);

                            yield return new CsprojInfo(packageId, version ?? cmd.Version, csproj);
                        }
                    }
                    else
                        _logger.Warning(
                            $"Project file  {Path.GetFileName(csproj)} does not contain a <Version> element. Please specify a version using the -v commandline argument.");
                }
            }
            else
            {
                // ensure path is a directory
                path = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                foreach(var csproj in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
                {
                    if (TryGetVersion(csproj, out string version) || !string.IsNullOrWhiteSpace(cmd.Version))
                    {
                        // to be sure that the csproj is nuget-compatible, ensure it is the new csproj format
                        if (!File.ReadLines(csproj).First().Trim().StartsWith("<Project Sdk="))
                            _logger.Warning($"Project file {Path.GetFileName(csproj)} is not supported: Only latest csproj format is supported");
                        else
                        {
                            var packageId = GetPackageId(csproj);

                            yield return new CsprojInfo(packageId, version ?? cmd.Version, csproj);   
                        }
                    }
                    else
                        _logger.Warning($"Project file  {Path.GetFileName(csproj)} does not contain a <Version> element. Please specify a version using the -v commandline argument.");
                }
            }
        }

        private bool TryGetVersion(string filePath, out string version)
        {
            EnsureCsprojFile(filePath);

            var findVersion = new Regex(@"<Version>\s*(?<version>\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?)\s*</Version>");
            
            var m = findVersion.Match(File.ReadAllText(filePath));
            if (!m.Success)
            {
                version = null;
                return false;
            }

            version = m.Groups["version"].Value;
            return true;
        }

        private string GetPackageId(string filePath)
        {
            EnsureCsprojFile(filePath);

            if (TryFindXmlElement(filePath, "PackageId", out var packageId)) 
                return packageId;

            if (TryFindXmlElement(filePath, "AssemblyName", out var assemblyName)) 
                return assemblyName;
            
            return Path.GetFileNameWithoutExtension(filePath);
        }

        private static bool TryFindXmlElement(string filePath, string elementName, out string value)
        {
            var findPackageId = new Regex($@"<{elementName}>(?<packageId>[\w\.]+)</{elementName}>");
            var m = findPackageId.Match(File.ReadAllText(filePath));
            if (m.Success)
            {
                value = m.Groups["packageId"].Value;
                return true;
            }

            value = null;
            return false;
        }

        private static string ExtractTargetFrameworkFromPath(string filePath)
        {
            // Try to extract TFM from path like:
            // bin\Debug\net6.0\MyLib.dll -> net6.0
            // lib\netstandard2.0\MyLib.dll -> netstandard2.0
            // Common TFM patterns: net6.0, net8.0, netstandard2.0, netstandard2.1, net472, net48, etc.

            var pathParts = filePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            // Common TFM patterns (in order of priority)
            var tfmPatterns = new[]
            {
                @"^net\d+\.\d+$",           // net6.0, net8.0
                @"^netstandard\d+\.\d+$",   // netstandard2.0, netstandard2.1
                @"^netcoreapp\d+\.\d+$",    // netcoreapp3.1
                @"^net\d+$",                 // net48, net472, net6, net8
                @"^net\d{3}$",               // net472, net48, etc.
            };

            // Search backwards through path parts to find TFM (usually closer to filename)
            for (int i = pathParts.Length - 2; i >= 0; i--)
            {
                var part = pathParts[i];
                foreach (var pattern in tfmPatterns)
                {
                    if (Regex.IsMatch(part, pattern, RegexOptions.IgnoreCase))
                    {
                        return part.ToLowerInvariant();
                    }
                }
            }

            return null;
        }

        private int FindAndReplaceDlls(DebugCommand cmd, string packageBasePath, List<string> debugDlls, List<string> debugPdbs, string packageId)
        {
            int replacedCount = 0;

            // iterate through all versions of the package in the cache
            foreach (var versionDir in Directory.EnumerateDirectories(packageBasePath))
            {
                // add marker file to know when to delete such a folder during cleanup
                var markerFile = Path.Combine(versionDir, ".debugified.txt");
                var timestamp = DateTime.UtcNow.ToString("o"); // ISO 8601 format
                if (cmd.Verbose) _logger.Debug($"Writing .debugified.txt to {versionDir}");
                File.WriteAllText(markerFile, $"Debugified at {timestamp} UTC");

                // search for lib folder in the package cache
                var libPath = Path.Combine(versionDir, "lib");
                if (!Directory.Exists(libPath))
                {
                    if (cmd.Verbose) _logger.Debug($"No 'lib' folder found in {versionDir}");
                    continue;
                }

                // find all DLLs and PDBs in the lib folder
                var cachedDlls = Directory.EnumerateFiles(libPath, "*.dll", SearchOption.AllDirectories).ToList();
                var cachedPdbs = Directory.EnumerateFiles(libPath, "*.pdb", SearchOption.AllDirectories).ToList();

                // match and replace DLLs by filename AND target framework
                foreach (var cachedDll in cachedDlls)
                {
                    var cachedFileName = Path.GetFileName(cachedDll);
                    var cachedTfm = ExtractTargetFrameworkFromPath(cachedDll);

                    var matchingDebugDll = debugDlls.FirstOrDefault(d =>
                    {
                        // Filename must match
                        if (!string.Equals(Path.GetFileName(d), cachedFileName, StringComparison.OrdinalIgnoreCase))
                            return false;

                        // If both have TFM, they must match
                        var debugTfm = ExtractTargetFrameworkFromPath(d);
                        if (cachedTfm != null && debugTfm != null)
                            return string.Equals(cachedTfm, debugTfm, StringComparison.OrdinalIgnoreCase);

                        // If at least one has no TFM, allow match (backward compatibility)
                        return true;
                    });

                    if (matchingDebugDll != null)
                    {
                        if (cmd.Verbose)
                        {
                            // Show relative path from version directory for cleaner output
                            var relativePath = Path.GetRelativePath(versionDir, cachedDll);
                            _logger.Debug($"  → {relativePath}");
                        }
                        File.Copy(matchingDebugDll, cachedDll, true);
                        replacedCount++;
                    }
                }

                // match and replace PDBs by filename AND target framework
                foreach (var cachedPdb in cachedPdbs)
                {
                    var cachedFileName = Path.GetFileName(cachedPdb);
                    var cachedTfm = ExtractTargetFrameworkFromPath(cachedPdb);

                    var matchingDebugPdb = debugPdbs.FirstOrDefault(p =>
                    {
                        // Filename must match
                        if (!string.Equals(Path.GetFileName(p), cachedFileName, StringComparison.OrdinalIgnoreCase))
                            return false;

                        // If both have TFM, they must match
                        var debugTfm = ExtractTargetFrameworkFromPath(p);
                        if (cachedTfm != null && debugTfm != null)
                            return string.Equals(cachedTfm, debugTfm, StringComparison.OrdinalIgnoreCase);

                        // If at least one has no TFM, allow match (backward compatibility)
                        return true;
                    });

                    if (matchingDebugPdb != null)
                    {
                        if (cmd.Verbose)
                        {
                            // Show relative path from version directory for cleaner output
                            var relativePath = Path.GetRelativePath(versionDir, cachedPdb);
                            _logger.Debug($"  → {relativePath}");
                        }
                        File.Copy(matchingDebugPdb, cachedPdb, true);
                        replacedCount++;
                    }
                }
            }

            return replacedCount;
        }

        private string RecursivelyFindSolutionDir(string path, bool verboseLogging)
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

            if(verboseLogging) _logger.Debug("Searching sln directory");

            string sln = null;
            for (int i = 0; i < 10; i++)
            {
                sln = Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                
                // if solution is not found within the current folder, proceed with the next parent folder
                if (sln is null)
                {
                    if(verboseLogging) _logger.Debug($" - No sln in '{dir}'");
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    sln = Path.GetDirectoryName(sln);
                    break;
                }
            }

            if (sln is null)
                _logger.Error("Could not find any *.sln file within {path} or any of its 10 parent folders!");

            return sln;
        }

        private bool DotnetBuild(CsprojInfo projectFile, DebugCommand cmd)
        {
            bool verbose = cmd.Verbose;
            var csprojPath = projectFile.Path;
            if (!Path.IsPathRooted(csprojPath))
                throw new ArgumentException($"{nameof(csprojPath)} must be rooted");

            // dotnet build -c <Configuration> [--no-incremental if rebuild requested]
            // see: https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-build
            var name = "dotnet";
            var rebuildFlag = cmd.Rebuild ? "--no-incremental" : "";
            var args = $"build -c {cmd.Configuration} {rebuildFlag} {cmd.BuildArguments}".Trim();

            _logger.Info($"building {Path.GetFileName(projectFile.Path)} ({cmd.Configuration})");

            var output = ExecuteProcess(name, args, csprojPath, verbose);

            const string fallbackTraceMessage =
                "error : If you are building projects that require targets from full MSBuild or MSBuildFrameworkToolsPath, you need to use desktop msbuild ('msbuild.exe') instead of 'dotnet build' or 'dotnet msbuild'";
            var fallbackToMsBuildRequired = output.Output.Any(l => l.Contains(fallbackTraceMessage));

            if (verbose)
            {
                foreach (var line in output.Output)
                    _logger.Debug(line);
                _logger.Debug($"dotnet build returned {output.ExitCode}");
            }

            if (fallbackToMsBuildRequired)
            {
                if(verbose) _logger.Debug("falling back to full MSBuild as advanced targets are required...");

                return MsBuildBuild(projectFile, cmd.Configuration, cmd.Rebuild, verbose);
            }

            // only if dotnet build returns 0, everything is fine
            return output.ExitCode == 0;
        }

        private bool MsBuildBuild(CsprojInfo projectFile, string configuration, bool rebuild, bool verbose)
        {
            var workingDir = Path.GetDirectoryName(projectFile.Path);

            // call vswhere - see: https://github.com/Microsoft/vswhere
            var vswhereEnv = @"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe";
            var vswherePath = Environment.ExpandEnvironmentVariables(vswhereEnv);

            var o = ExecuteProcess(vswherePath,
                @"-latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe",
                workingDir, verbose);

            var msBuildPath = o.ExitCode == 0 ? o.Output.Single() : null;

            if (verbose && o.ExitCode == 0)
                _logger.Debug($"found msbuild at '{msBuildPath}'");
            if (o.ExitCode != 0)
            {
                _logger.Error("Could not find msbuild.exe. Please ensure that you have Visual Studio 2017 or higher installed on your machine!");
                return false;
            }

            var target = rebuild ? "rebuild" : "build";
            var o2 = ExecuteProcess(msBuildPath,
                $"\"{projectFile.Path}\" /t:{target} /v:m /p:Configuration={configuration}",
                workingDir, verbose);

            if (verbose)
            {
                foreach (var l in o2.Output)
                    _logger.Verbose(l);
            }

            // only if msbuild build returns 0, everything is fine
            return o2.ExitCode == 0;
        }

        private ProcessOutput ExecuteProcess(string name, string args, string workingDirectory, bool verbose)
        {
            if(verbose)
                _logger.Info($"{name} {args}");

            var pi = new ProcessStartInfo(name, args);
            pi.RedirectStandardError = true;
            pi.RedirectStandardOutput = true;
            // if the user specified just a folder, set it as working dir
            pi.WorkingDirectory = Path.GetDirectoryName(workingDirectory);

            var result = new ProcessOutput();

            var output = new DataReceivedEventHandler((s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    result.Output.Add(e.Data);
            });
            var error = new DataReceivedEventHandler((s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    result.Output.Add(e.Data);
            });

            // how to forward process output to console: https://stackoverflow.com/questions/4291912/process-start-how-to-get-the-output
            var process = new Process();
            process.StartInfo = pi;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += output;
            process.ErrorDataReceived += error;
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                result.ExitCode = process.ExitCode;

                return result;
            }
            finally
            {
                process.OutputDataReceived -= output;
                process.ErrorDataReceived -= error;
            }
        }

        private class ProcessOutput
        {
            public List<string> Output { get; } = new List<string>();
            public int ExitCode { get; set; }
        }

        private void EnsureCsprojFile(string filePath)
        {
            if (!string.Equals(Path.GetExtension(filePath), ".csproj", StringComparison.InvariantCultureIgnoreCase))
                throw new ArgumentException("filePath must point to *.csproj file");
        }

        [DebuggerDisplay("{" + nameof(NugetPackageName) + "}")]
        private class CsprojInfo
        {
            public string PackageId { get; }
            public string Version { get; private set; }
            public string ActualVersion { get; private set; }
            public string Path { get; }

            public CsprojInfo(string packageId, string version, string path)
            {
                PackageId = packageId;
                Version = version;
                Path = path;
                ActualVersion = version;
            }
            
            public string NugetPackageName => $"{PackageId}.{ActualVersion}.symbols.nupkg";

            public string NugetPackageNameShortVersion => $"{PackageId}.{GetActialVersionAsWithoutRevision()}.symbols.nupkg";

            private string GetActialVersionAsWithoutRevision()
            {
                var longVersionString = ActualVersion.ExtractVersionNumber();
                var shortVersionString = ActualVersion.ToThreeDigitVersion();
                return ActualVersion.Replace(longVersionString, shortVersionString);
            }

            public IDisposable ReplaceVersion(string newVersion)
            {
                var findVersion = new Regex(@"<Version>\s*(?<version>\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?)\s*</Version>");
                var txt = File.ReadAllText(Path);

                var m = findVersion.Match(txt);
                if (m.Success)
                {

                    var versionElement = m.Value;

                    // if we do not have to replace the version, ignore
                    if (string.Equals(m.Groups["version"].Value, newVersion))
                        return NullDisposable.Instance;

                    // move original csproj to temp folder and afterwards back again so GIT does not complain about any changes
                    var tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"debugify_{DateTime.UtcNow:yyyMMddhhmmss}_{Guid.NewGuid():N}");
                    var tempPath = System.IO.Path.Combine(tempFolder,
                        System.IO.Path.GetFileName(Path));

                    Directory.CreateDirectory(tempFolder);
                    File.Move(Path, tempPath);

                    var newVersionElement = $"<Version>{newVersion}</Version>";
                    File.WriteAllText(Path, txt.Replace(versionElement, newVersionElement));
                    ActualVersion = newVersion;

                    return new VersionRestorer(Path, tempPath);
                }
                else
                {
                    var findPropertyGroup = new Regex(@"</PackageId>");
                    var fm = findPropertyGroup.Match(txt);
                    if(!fm.Success)
                        throw new InvalidOperationException($"Not a single </PackageId> element could be found in '{Path}'");

                    // move original csproj to temp folder and afterwards back again so GIT does not complain about any changes
                    var tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"debugify_{DateTime.UtcNow:yyyMMddhhmmss}_{Guid.NewGuid():N}");
                    var tempPath = System.IO.Path.Combine(tempFolder,
                        System.IO.Path.GetFileName(Path));

                    Directory.CreateDirectory(tempFolder);
                    File.Move(Path, tempPath);

                    var newVersionElement = $"<Version>{newVersion}</Version>";
                    // sneak in version element by replacing the FIRST <PropertyGroup> element with a </PackageId>\n<Version>....</Version> element
                    File.WriteAllText(Path, findPropertyGroup.Replace(txt, $"</PackageId>\n" +newVersionElement, 1));
                    ActualVersion = newVersion;

                    return new VersionRestorer(Path, tempPath);
                }
            }

            private class NullDisposable : IDisposable
            {
                public static IDisposable Instance = new NullDisposable();

                private NullDisposable()
                { }

                public void Dispose()
                { }
            }

            private class VersionRestorer : IDisposable
            {
                private readonly string _path;
                private readonly string _tempPath;

                public VersionRestorer(string path, string tempPath)
                {
                    _path = path;
                    _tempPath = tempPath;
                }

                public void Dispose()
                {
                    File.Delete(_path);
                    File.Move(_tempPath, _path);

                    // clean up temporary data
                    Directory.Delete(System.IO.Path.GetDirectoryName(_tempPath), true);
                }
            }
        }

        private class CompositeDisposable : IDisposable
        {
            private readonly ILogger _logger;
            private readonly List<IDisposable> _disposables = new List<IDisposable>();

            public CompositeDisposable(ILogger logger)
            {
                _logger = logger;
            }

            public void Add(IDisposable child) => _disposables.Add(child);

            public void Dispose()
            {
                foreach(var d in _disposables)
                {
                    try
                    {
                        d.Dispose();
                    }
                    catch (Exception x)
                    {
                        _logger.Error($"Error while disposing: {x.GetType().Name} {x.Message}");
                    }
                }
            }
        }
    }

    internal static class VersionExtensions
    {
        internal static string ExtractVersionNumber(this string version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            var regex = new Regex("^(?<version>\\d+\\.\\d+\\.\\d+(\\.\\d+)?).*$");
            if (regex.IsMatch(version))
                return regex.Match(version).Groups["version"].Value;

            return null;
        }

        internal static string ToThreeDigitVersion(this string version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            
            var regex = new Regex(@"^((?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(\.(?<revision>\d+))?).*$");
            if (!regex.IsMatch(version))
                return version;

            var m = regex.Match(version);

            var major = m.Groups["major"].Value;
            var minor = m.Groups["minor"].Value;
            var build = m.Groups["build"].Value;
            var revision = m.Groups["revision"].Value;
            
            if(!string.IsNullOrEmpty(revision) && !string.Equals(revision, "0",StringComparison.OrdinalIgnoreCase))
                return $"{major}.{minor}.{build}.{revision}";

            // if revision does not exist or equals "0", we can ignore it
            return $"{major}.{minor}.{build}";
        }
    }
}
