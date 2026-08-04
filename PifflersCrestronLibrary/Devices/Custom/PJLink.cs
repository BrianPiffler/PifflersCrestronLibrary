using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PifflersCrestronLibrary.Devices.Custom
{
    public class PJLink : BasicTCP
    {
        public const string ProjOff = "0";
        public const string ProjOn = "1";
        public const string ProjCooling = "2";
        public const string ProjWarming = "3";
        public const string ProjUnavailable = "ERR3";
        public const string ProjFailure = "ERR4";

        private readonly string password;
        private readonly Crestron.SimplSharp.Cryptography.MD5 md5 = Crestron.SimplSharp.Cryptography.MD5.Create();
        private readonly Queue<string> pendingCommands = new Queue<string>();

        private CTimer pollPowerTimer;
        private CTimer pollSourceTimer;
        private CTimer pollLampTimer;

        private bool greetingReceived;
        private bool authenticationRequired;
        private string authPrefix = string.Empty;

        public bool Polling { get; private set; } = true;
        public string CurrentSource { get; private set; }
        public uint LampHours { get; private set; }
        public string PowerState { get; private set; }
        public string LastError { get; private set; }

        public event EventHandler PJLinkEvent;

        public PJLink(string host, string pass, string friendlyName)
            : base(host, friendlyName, 4352)
        {
            password = pass ?? string.Empty;
            PowerState = ProjOff;

            // Keep polling control close to socket state, like the other BasicTCP-based drivers.
            client.SocketStatusChange += SocketStatusChangedInternal;
        }

        protected override void InitializeRegexDictionary()
        {
            // One parser regex allows us to process all lines in a single receive chunk.
            matchStringDict.Add(@"[^\r\n]+", MatchAnyLine);
        }

        protected override void KeepAliveCallback(object _)
        {
            GetPowerState();
        }

        public void SetFriendlyName(string name)
        {
            if (!string.IsNullOrEmpty(name))
                friendlyName = name;
        }

        public void PollStatus(bool poll)
        {
            Polling = poll;
            if (!poll)
                StopPollTimers();
            else if (Connected)
                StartPollTimers();
        }

        public void Power(bool powerOn)
        {
            SendCommand(powerOn ? "%1POWR 1\r" : "%1POWR 0\r");
        }

        public void InputChange(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return;

            SendCommand("%1INPT " + source.Trim() + "\r");
        }

        public void GetPowerState()
        {
            SendCommand("%1POWR ?\r");
        }

        public void GetInputState()
        {
            SendCommand("%1INPT ?\r");
        }

        public void GetLampHours()
        {
            SendCommand("%1LAMP ?\r");
        }

        private void SocketStatusChangedInternal(TCPClient _, SocketStatus status)
        {
            Debug.Log(friendlyName + " PJLink socket status (internal): " + status);
            if (status == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                greetingReceived = false;
                authenticationRequired = false;
                authPrefix = string.Empty;

                if (Polling)
                    StartPollTimers();
                return;
            }

            StopPollTimers();
            pendingCommands.Clear();
        }

        private void StartPollTimers()
        {
            if (pollPowerTimer == null)
                pollPowerTimer = new CTimer(_ => PollPower(), null, 1000, 1000);
            else
                pollPowerTimer.Reset(1000, 1000);

            if (pollSourceTimer == null)
                pollSourceTimer = new CTimer(_ => PollSource(), null, 5000, 5000);
            else
                pollSourceTimer.Reset(5000, 5000);

            if (pollLampTimer == null)
                pollLampTimer = new CTimer(_ => PollLamp(), null, 300000, 300000);
            else
                pollLampTimer.Reset(300000, 300000);
        }

        private void StopPollTimers()
        {
            pollPowerTimer?.Stop();
            pollSourceTimer?.Stop();
            pollLampTimer?.Stop();
        }

        private void PollPower()
        {
            if (Polling && Connected)
                GetPowerState();
        }

        private void PollSource()
        {
            if (Polling && Connected)
                GetInputState();
        }

        private void PollLamp()
        {
            if (Polling && Connected)
                GetLampHours();
        }

        private void SendCommand(string command)
        {
            if (!Connected)
                return;

            if (!CanSendImmediately())
            {
                pendingCommands.Enqueue(command);
                Debug.Log(friendlyName + " queue command (await greeting), size=" + pendingCommands.Count);
                return;
            }

            SendRaw(PreparePayload(command));
        }

        private bool CanSendImmediately()
        {
            if (!greetingReceived)
                return false;

            return !authenticationRequired || !string.IsNullOrEmpty(authPrefix);
        }

        private string PreparePayload(string command)
        {
            return authenticationRequired ? authPrefix + command : command;
        }

        private void FlushPendingCommands()
        {
            while (pendingCommands.Count > 0 && CanSendImmediately() && Connected)
            {
                SendRaw(PreparePayload(pendingCommands.Dequeue()));
            }
        }

        private void MatchAnyLine(MatchCollection matches)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var line = matches[i].Value.Trim();
                if (line.Length == 0)
                    continue;

                var visible = line.Replace("\0", "\\0");
                Debug.Log(friendlyName + " PJLink line raw: '" + visible + "'");

                ProcessLine(line);
            }
        }

        private void ProcessLine(string line)
        {
            var normalizedLine = NormalizeIncomingLine(line);

            if (normalizedLine.StartsWith("PJLINK "))
            {
                HandleGreeting(normalizedLine);
                return;
            }

            var power = Regex.Match(normalizedLine, @"^%1POWR=(0|1|2|3|ERR3|ERR4)$");
            if (power.Success)
            {
                PowerState = power.Groups[1].Value;
                RaiseDataEvent("PowerState");
                PJLinkEvent?.Invoke(this, EventArgs.Empty);
                return;
            }

            var input = Regex.Match(normalizedLine, @"^%1INPT=([A-Z0-9]+|ERR2|ERR3)$");
            if (input.Success)
            {
                CurrentSource = input.Groups[1].Value;
                RaiseDataEvent("CurrentSource");
                return;
            }

            var lamp = Regex.Match(normalizedLine, @"^%1LAMP=(\d+)");
            if (lamp.Success)
            {
                uint value;
                if (uint.TryParse(lamp.Groups[1].Value, out value))
                {
                    LampHours = value;
                    RaiseDataEvent("LampHours");
                }
                return;
            }

            if (normalizedLine.Contains("ERR"))
            {
                LastError = normalizedLine;
                ErrorLog.Error("{0}: PJLink error: {1}", friendlyName, normalizedLine);
                RaiseDataEvent("Error");
            }
        }

        private void HandleGreeting(string line)
        {
            var greet = Regex.Match(line, @"^PJLINK\s+(0|1)(?:\s+([0-9A-Fa-f]{8}))?$");
            if (!greet.Success)
            {
                Debug.Warn(friendlyName + " greeting parse failed: '" + line + "'");
                return;
            }

            greetingReceived = true;
            authenticationRequired = greet.Groups[1].Value == "1";
            authPrefix = string.Empty;
            Debug.Log(friendlyName + " greeting accepted, auth=" + (authenticationRequired ? "required" : "none"));

            if (authenticationRequired)
            {
                var challenge = greet.Groups[2].Value;
                if (challenge.Length == 8)
                    authPrefix = ToMd5LowerHex(challenge + password);
            }

            if (pendingCommands.Count == 0)
                pendingCommands.Enqueue("%1POWR ?\r");

            FlushPendingCommands();
        }

        private string NormalizeIncomingLine(string line)
        {
            return (line ?? string.Empty).Trim('\0', ' ', '\t', '\r', '\n');
        }

        private string ToMd5LowerHex(string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            byte[] hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLower();
        }

        public new void Dispose()
        {
            pollPowerTimer?.Dispose();
            pollSourceTimer?.Dispose();
            pollLampTimer?.Dispose();
            md5?.Dispose();
            base.Dispose();
        }
    }
}