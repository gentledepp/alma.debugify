using System;
using System.IO;
using CommandLine;

namespace nuget.debugify
{
    [Verb("cleanup",
        HelpText = "Removes all debuggable *dlls from the cache so that the original can be resolved again")]
    public class CleanupCommand
    {
        [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose{ get; set; }
    }

    internal class Undebugifier
    {
        private readonly ILogger _logger;

        public Undebugifier(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        public void Cleanup(CleanupCommand cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));

            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var extractPath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if (!Directory.Exists(extractPath))
            {
                _logger.Warning($"Could not find nuget package cache at {extractPath}");
                return;
            }

            var count = 0;
            // add pseudo file to know when do delete such a folder
            foreach (var file in Directory.GetFiles(extractPath, ".debugified.txt", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file);
                _logger.Info($"Undebugifying {dir.Substring(extractPath.Length, dir.Length-extractPath.Length)}");
                Directory.Delete(dir, true);
                count++;
            }

            if (count == 0)
                _logger.Success("All good. Nothing to undebugify");
            else
                _logger.Success($"Cleaned up {count} debugified packages");
        }


    }
}
