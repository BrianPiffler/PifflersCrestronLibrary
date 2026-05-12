using Crestron.SimplSharp;

namespace PifflersCrestronLibrary
{
    public class Debug
    {
        private const string VERSION = "0.0.1.3";
        private bool _debug = true;
        private string _friendlyName = "";

        public Debug(string friendlyName)
        {
            _friendlyName = friendlyName;
            print("Debug initialized: old Vistalib Version");
            print("Try tu use the Deebug instead");
        }

        public bool status
        {
            get => _debug;
            set => _debug = value;
        }

        public void print(string message, params object[] args)
        {
            if (_debug)
            {
                string nachricht = string.Format(message, args);
                string formatedMessage = $"[ {_friendlyName} ] - {nachricht} ";
                CrestronConsole.PrintLine(formatedMessage);
            }
        }
    }
}