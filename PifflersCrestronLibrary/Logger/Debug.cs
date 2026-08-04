using Crestron.SimplSharp;

namespace PifflersCrestronLibrary.Logger
{
    public static class Debug
    {
        public const string DebugCommandName = "deebug"; // cause debug is already used =(
        public const string DebugCommandDescription = "Logger control: deebug on|off|status|help";

        /// <summary>App-/Projekt-Tag fuer die persistente ErrorLog-Ausgabe. Beim Start setzen.</summary>
        public static string AppName { get; set; } = "Pifflers";

        public static bool DebugMode { get; set; } = true;

        public static void RegisterConsoleCommand(ConsoleAccessLevelEnum accessLevel = ConsoleAccessLevelEnum.AccessOperator)
        {
            CrestronConsole.AddNewConsoleCommand(
                DebugConsoleCommand,
                DebugCommandName,
                DebugCommandDescription,
                accessLevel);

            CrestronConsole.PrintLine("[DEBUG] Registered command: {0} on|off|status|help", DebugCommandName);
        }

        public static void Log(string msg)
        {
            if (!DebugMode) return;
            CrestronConsole.PrintLine("[LOG] {0}", msg ?? string.Empty);
        }

        public static void Warn(string msg)
        {
            if (!DebugMode) return;
            CrestronConsole.PrintLine("[WARN] {0}", msg ?? string.Empty);
        }

        // Fehler werden immer ausgegeben, unabhaengig von DebugMode.
        public static void Error(string msg)
        {
            CrestronConsole.PrintLine("[ERROR] {0}", msg ?? string.Empty);
        }

        public static void LogToErrorLog(string msg)
        {
            ErrorLog.Error("[{0}] {1}", AppName, msg ?? string.Empty);
        }

        public static void DebugConsoleCommand(string args)
        {
            var command = (args ?? string.Empty).Trim().ToLowerInvariant();

            switch (command)
            {
                case "on":
                    DebugMode = true;
                    CrestronConsole.PrintLine("[DEBUG] Debug mode is now ON.");
                    break;

                case "off":
                    DebugMode = false;
                    CrestronConsole.PrintLine("[DEBUG] Debug mode is now OFF.");
                    break;

                case "status":
                    CrestronConsole.PrintLine("[DEBUG] Debug mode status: {0}", DebugMode ? "ON" : "OFF");
                    break;

                case "help":
                case "?":
                case "":
                    PrintDebugCommandHelp();
                    break;

                default:
                    CrestronConsole.PrintLine("[DEBUG] Unknown option: '{0}'", command);
                    PrintDebugCommandHelp();
                    break;
            }
        }

        private static void PrintDebugCommandHelp()
        {
            CrestronConsole.PrintLine("Usage: {0} <option>", DebugCommandName);
            CrestronConsole.PrintLine("Options:");
            CrestronConsole.PrintLine("  on     - Enable debug output in SSH console");
            CrestronConsole.PrintLine("  off    - Disable debug output in SSH console");
            CrestronConsole.PrintLine("  status - Show current debug status");
            CrestronConsole.PrintLine("  help   - Show this help");
            CrestronConsole.PrintLine("Examples: {0} on | {0} status | {0} off", DebugCommandName);
        }
    }
}