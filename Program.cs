using alma.debugify;
using CommandLine;
using Spectre.Console;
using System;
using System.Linq;

namespace alma.debugify
{
    class Program
    {
        static void Main(string[] args)
        {
            var logger = new ConsoleLogger();

            // Show hero section unless asking for help
            if (args.Length == 0 || (!args.Contains("--help") && !args.Contains("-h") && !args.Contains("help")))
            {
                ShowHero();
            }

            Parser.Default.ParseArguments<DebugCommand, ListCommand, CleanupCommand>(args)
                .WithParsed<DebugCommand>(c => new Debugifier(logger).Debugify(c))
                .WithParsed<ListCommand>(c => new DebugifiedListProvider(logger).List(c))
                .WithParsed<CleanupCommand>(c => new Undebugifier(logger).Cleanup(c));

        }

        static void ShowHero()
        {
            AnsiConsole.Write(
                new FigletText("Debugify")
                    .LeftJustified()
                    .Color(Color.Cyan1));

            AnsiConsole.MarkupLine("[dim]Debug your NuGet packages locally[/]");
            AnsiConsole.WriteLine();
        }
    }

}
