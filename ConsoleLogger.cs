using System;
using Spectre.Console;

namespace alma.debugify
{
    internal interface ILogger
    {
        void Verbose(string message);
        void Debug(string message);
        void Info(string message);
        void Success(string message);
        void Warning(string message);
        void Error(string message);
    }

    internal class ConsoleLogger : ILogger
    {
        public void Verbose(string message)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");
        }
        public void Debug(string message)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }
        public void Info(string message)
        {
            AnsiConsole.MarkupLine($"[white]{Markup.Escape(message)}[/]");
        }
        public void Success(string message)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
        }
        public void Warning(string message)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(message)}");
        }
        public void Error(string message)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}");
        }
    }
}
