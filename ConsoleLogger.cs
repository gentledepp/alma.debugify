using System;

namespace nuget.debugify
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
            WriteWithColor(message, ConsoleColor.DarkGray);
        }
        public void Debug(string message)
        {
            WriteWithColor(message, ConsoleColor.Gray);
        }
        public void Info(string message)
        {
            WriteWithColor(message, ConsoleColor.White);
        }
        public void Success(string message)
        {
            WriteWithColor(message, ConsoleColor.Green);
        }
        public void Warning(string message)
        {
            WriteWithColor(message, ConsoleColor.DarkYellow);
        }
        public void Error(string message)
        {
            WriteWithColor(message, ConsoleColor.Red);
        }
        private void WriteWithColor(string message, ConsoleColor? foreground = null, ConsoleColor? background = null)
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
