using alma.debugify;
using CommandLine;

namespace alma.debugify
{
    class Program
    {
        static void Main(string[] args)
        {
            var logger = new ConsoleLogger();
            
            Parser.Default.ParseArguments<DebugCommand, ListCommand, CleanupCommand>(args)
                .WithParsed<DebugCommand>(c => new Debugifier(logger).Debugify(c))
                .WithParsed<ListCommand>(c => new DebugifiedListProvider(logger).List(c))
                .WithParsed<CleanupCommand>(c => new Undebugifier(logger).Cleanup(c));

        }
    }

}
