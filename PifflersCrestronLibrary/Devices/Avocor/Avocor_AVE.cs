using PifflersCrestronLibrary.Communication;
using System.Text.RegularExpressions;

namespace PifflersCrestronLibrary.Devices.Displays
{
    public class Avocor_AVE : BasicTCP
    {
        private bool powerStatus;

        public bool PowerStatus
        {
            get { return powerStatus; }
            set 
            {
                powerStatus = value;
                SendRaw(powerStatus ? "\x07\x01\x02POW\x01\x08" : "\x07\x01\x02POW\x00\x08");
            }
        }

        public Avocor_AVE(string host, string name)
            : base(host, name, 23)
        {
        }

        protected override void InitializeRegexDictionary()
        {
            matchStringDict.Add(@"\x07\x01\x00\x50\x4F\x57([0-1])\x08", __MatchPowerStatus);
        }

        private void __MatchPowerStatus(MatchCollection match)
        {
            powerStatus = (match[0].Groups[1].Value == "1");
            RaiseDataEvent("PowerStatus");
        }

        protected override void KeepAliveCallback(object _)
        {
            SendRaw("\x07\x01\x01POW\x08");
        }
    }
}