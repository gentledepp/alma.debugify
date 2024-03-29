﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CommandLine;

namespace alma.debugify
{
    [Verb("setup", isDefault:true, HelpText="Replaces *.dlls of a given project in the local nuget .package cache with a debuggable one so that you can actually debug its code")]
    public class DebugCommand
    {
        [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose{ get; set; }

        [Option('p', "path", Required = false, HelpText = "Path to *.csproj file or a folder that contains it (directly or in subfolder(s))")]
        public string Path { get; set; }

        [Option('v', "version", Required=false,HelpText = "Specify the version you'd like to debugify")]
        public string Version { get; set; }
        
        [Option( "packargs", Required=false,HelpText = "Additional arguments for dotnet pack. e.g. \" --ignore-failed-sources\"")]
        public string PackArguments { get; set; }
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


            DeleteNupkgFiles(cmd.Path);

            // make sur the version files are restored afterwards
            using (var cd = new CompositeDisposable(_logger))
            {
                // pack the thing up
                var packFailedCount = 0;
                var erroredCsprojInfos = new List<CsprojInfo>();
                // replace version in csproj files
                if (!string.IsNullOrWhiteSpace(cmd.Version))
                {
                    foreach (var projectFile in projectFiles)
                    {
                        try
                        {
                            if(cmd.Verbose) _logger.Debug($"changing package version of {Path.GetFileName(projectFile.Path)}");

                            cd.Add(projectFile.ReplaceVersion(cmd.Version));
                        }
                        catch (Exception x)
                        {
                            erroredCsprojInfos.Add(projectFile);
                            packFailedCount++;

                            _logger.Error(x.Message);
                        }
                    }
                }

                foreach (var projectFile in projectFiles.Except(erroredCsprojInfos))
                {
                    if (!DotnetPack(projectFile, cmd))
                    {
                        _logger.Error($"dotnet pack failed for {Path.GetFileName(projectFile.Path)}");
                        packFailedCount++;
                    }
                }

                if (packFailedCount != 0)
                    _logger.Warning(
                        $"WARNING: Failed to create {packFailedCount} of {projectFiles.Count} *.nupkg files");
            }

            // resolve nuget package cache
            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var packageCachePath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if(cmd.Verbose) _logger.Info($"nuget package cache fount at '{packageCachePath}'");

            // find each nupkg and extract it to cache
            foreach (var projectFile in projectFiles)
            {
                var packageName = projectFile.NugetPackageName;
                var alternativePackageName =  projectFile.NugetPackageNameShortVersion;

                var packagePath = Directory.EnumerateFiles(solutionDir, "*.nupkg", SearchOption.AllDirectories)
                    .FirstOrDefault(p => string.Equals(packageName, Path.GetFileName(p), StringComparison.InvariantCultureIgnoreCase) ||
                                         string.Equals(alternativePackageName, Path.GetFileName(p), StringComparison.InvariantCultureIgnoreCase));
                if (packagePath is null)
                {
                    _logger.Error($"Could not find package {packageName}");
                    continue;
                }

                // verify, that there is a nuget package with the same id and version in the cache
                var extractPath = Path.Combine(packageCachePath, projectFile.PackageId, projectFile.ActualVersion);
                if (!Directory.Exists(extractPath))
                {
                    _logger.Warning($"Cannot debugify {packageName} as the package cannot be found in the cache: {extractPath}");
                    continue;
                }
                
                // extract nupkg contents into package cache
                ExtractNupkg(cmd, extractPath, packagePath);

                _logger.Success($"Successfully debugified {projectFile.PackageId} version {projectFile.ActualVersion}");
            }
            
        }

        private void DeleteNupkgFiles(string cmdPath)
        {
            var directory = Directory.Exists(cmdPath) ? cmdPath : Path.GetDirectoryName(cmdPath);

            foreach (var f in Directory.EnumerateFileSystemEntries(directory, "*.symbols.nupkg", SearchOption.AllDirectories))
            {
                File.Delete(f);
                _logger.Debug($"deleting existing package {f}");
            }

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

        private void ExtractNupkg(DebugCommand cmd, string extractPath, string packagePath)
        {
            // add pseudo file to know when do delete such a folder
            if (cmd.Verbose) _logger.Debug($"Writing .debugified.txt to {extractPath}");
            File.WriteAllText(Path.Combine(extractPath, ".debugified.txt"), "this package was debugified");

            using var archive = new ZipArchive(File.OpenRead(packagePath));
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("lib") || entry.FullName.StartsWith("src"))
                {
                    if (cmd.Verbose) _logger.Debug($"Extracting {entry.FullName}");

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
                    if (cmd.Verbose) _logger.Debug($"Skipping {entry.FullName} as it does not start with 'src' or 'lib'");
                }
            }
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

        private bool DotnetPack(CsprojInfo projectFile, DebugCommand cmd)
        {
            bool verbose = cmd.Verbose;
            var csprojPath = projectFile.Path;
            if (!Path.IsPathRooted(csprojPath))
                throw new ArgumentException($"{nameof(csprojPath)} must be rooted");

            // dotnet pack --include-symbols --include-source -c Debug
            // see: https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-pack
            var name = "dotnet";
            //var args = $"pack '{csprojPath}' --include-symbols --include-source -c Debug";
            var args = $"pack --include-symbols --include-source -c Debug {cmd.PackArguments}";
            
            _logger.Info($"creating {projectFile.NugetPackageName}");

            var output = ExecuteProcess(name, args, csprojPath, verbose);


            const string fallbackTraceMessage =
                "error : If you are building projects that require targets from full MSBuild or MSBuildFrameworkToolsPath, you need to use desktop msbuild ('msbuild.exe') instead of 'dotnet build' or 'dotnet msbuild'";
            var fallbackToMsBuildRequired = output.Output.Any(l => l.Contains(fallbackTraceMessage));

            if (verbose)
            {
                foreach (var line in output.Output)
                    _logger.Debug(line);
                _logger.Debug($"dotnet pack returned {output.ExitCode}");
            }

            if (fallbackToMsBuildRequired)
            {
                if(verbose) _logger.Debug("falling back to full MSBuild as advanced targets are required...");

                return MsBuildPack(projectFile, verbose);
            }

            // only iif dotnet pack returns 0, everything is fine
            return output.ExitCode == 0;
        }

        private bool MsBuildPack(CsprojInfo projectFile, bool verbose)
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

            var o2 = ExecuteProcess(msBuildPath,
                $"\"{projectFile.Path}\" /t:pack /v:m /p:Configuration=Debug /p:IncludeSymbols=true", 
                workingDir, verbose);

            if (verbose)
            {
                foreach (var l in o2.Output)
                    _logger.Verbose(l);
            }

            // only iif msbuild pack returns 0, everything is fine
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
