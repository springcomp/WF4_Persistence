using System;
using NDesk.Options;

namespace Workflow
{
    public sealed class CommandLine
    {
        public enum Operations
        {
            Run,
            Resume,
        }

        private CommandLine()
        { }

        public static CommandLine Parse(string[] args)
        {
            try
            {
                var cmdLine = new CommandLine();

                var options = new OptionSet()
                {
                    {"c|crash", v => cmdLine.Crash = (v != null) },
                    {"r|run", v => cmdLine.Operation = Operations.Run },
                    {"m|resume", v => cmdLine.Operation = Operations.Resume },
                };

                var remainingArgs = options.Parse(args);

                return cmdLine;
            }
            catch (OptionException e)
            {
                Console.Error.WriteLine(e.Message);
                Environment.Exit(42);
                throw;
            }
        }

        public Operations Operation { get; private set; }
        public bool Crash { get; private set; }
    }
}