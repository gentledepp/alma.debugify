using System;
using System.IO;
using CommandLine;

namespace alma.debugify
{
    [Verb("list",
        HelpText = "List all NuGet packages in the local cache that have been debugified (shows packages with debug DLLs)")]
    public class ListCommand
    {
        [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }
    }

    internal class DebugifiedListProvider
    {
        private readonly ILogger _logger;

        public DebugifiedListProvider(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void List(ListCommand cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));

            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var extractPath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if (!Directory.Exists(extractPath))
            {
                _logger.Warning($"Could not find nuget package cache at {extractPath}");
                return;
            }

            _logger.Info($"Listing all debugified packages at {extractPath}");

            var count = 0;
            // add pseudo file to know when do delete such a folder
            foreach (var file in Directory.GetFiles(extractPath, ".debugified.txt", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file);
                _logger.Info($" - {dir.Substring(extractPath.Length, dir.Length - extractPath.Length)}");
                count++;
            }

            if (count == 0)
                _logger.Success("All good. Nothing debugified");
            else
                _logger.Success($"Found {count} debugified packages");
        }
    }

}
