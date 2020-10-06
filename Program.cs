using System;
using System.Collections;
using System.Collections.Generic;
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
        static void Main(string[] args)
        {
            var logger = new ConsoleLogger();
            
            Parser.Default.ParseArguments<DebugCommand, CleanupCommand>(args)
                .WithParsed<DebugCommand>(c => new Debugifier(logger).Debugify(c))
                .WithParsed<CleanupCommand>(c => new Undebugifier(logger).Cleanup(c));

        }
    }

}
