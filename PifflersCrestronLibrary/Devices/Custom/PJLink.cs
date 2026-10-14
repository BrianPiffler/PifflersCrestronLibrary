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
        private CTimer pollAvMuteTimer;

        private bool greetingReceived;
        private bool authenticationRequired;
        private string authPrefix = string.Empty;

        public bool Polling { get; private set; } = true;
        public string CurrentSource { get; private set; }
        public uint LampHours { get; private set; }
        public string PowerState { get; private set; }
        public string LastError { get; private set; }
        /// <summary>true wenn der Beamer AV-Mute (Bild+Ton weg) aktiv hat.</summary>
        public bool AvMute { get; private set; }
        /// <summary>true wenn der Beamer Freeze aktiv hat (PJLink Class 2).</summary>
        public bool Freeze { get; private set; }

        public event EventHandler PJLinkEvent;
        /// <summary>Feuert nach jeder AVMT-Antwort mit dem aktuellen (neuen oder gleichen) State.</summary>
        public event Action<bool> AvMuteChanged;
        /// <summary>Feuert nach jeder FREZ-Abfrage-Antwort mit dem aktuellen State.</summary>
        public event Action<bool> FreezeChanged;

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

        /// <summary>Setzt A/V-Mute (Bild + Ton). PJLink Class1: %1AVMT 31 (on) / 30 (off).</summary>
        public void SetAvMute(bool on)
        {
            SendCommand(on ? "%1AVMT 31\r" : "%1AVMT 30\r");
            // Direkt danach Zustand abfragen, damit das Feedback nicht bis zum
            // naechsten Poll-Zyklus warten muss.
            SendCommand("%1AVMT ?\r");
        }

        public void GetAvMute()
        {
            SendCommand("%1AVMT ?\r");
        }

        /// <summary>Setzt Freeze (Standbild). Erst ab PJLink Class 2: %2FREZ 1/0.</summary>
        public void SetFreeze(bool on)
        {
            SendCommand(on ? "%2FREZ 1\r" : "%2FREZ 0\r");
            SendCommand("%2FREZ ?\r");
        }

        public void GetFreeze()
        {
            SendCommand("%2FREZ ?\r");
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
                pollPowerTimer = new CTimer(_ => PollPower(), null, 10000, 10000);
            else
                pollPowerTimer.Reset(10000, 10000);

            if (pollSourceTimer == null)
                pollSourceTimer = new CTimer(_ => PollSource(), null, 30000, 30000);
            else
                pollSourceTimer.Reset(30000, 30000);

            if (pollLampTimer == null)
                pollLampTimer = new CTimer(_ => PollLamp(), null, 300000, 300000);
            else
                pollLampTimer.Reset(300000, 300000);

            if (pollAvMuteTimer == null)
                pollAvMuteTimer = new CTimer(_ => PollAvMute(), null, 10000, 10000);
            else
                pollAvMuteTimer.Reset(10000, 10000);
        }

        private void StopPollTimers()
        {
            pollPowerTimer?.Stop();
            pollSourceTimer?.Stop();
            pollLampTimer?.Stop();
            pollAvMuteTimer?.Stop();
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

        private void PollAvMute()
        {
            if (Polling && Connected)
                GetAvMute();
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

            // AVMT-Antwort: 2-stellig, letzte Ziffer 1=on / 0=off (erste Ziffer 1=Video, 2=Audio, 3=AV).
            var avmt = Regex.Match(normalizedLine, @"^%1AVMT=([123][01])$");
            if (avmt.Success)
            {
                AvMute = avmt.Groups[1].Value.EndsWith("1");
                RaiseDataEvent("AvMute");
                var h = AvMuteChanged; if (h != null) h(AvMute);
                return;
            }

            var frez = Regex.Match(normalizedLine, @"^%2FREZ=([01])$");
            if (frez.Success)
            {
                Freeze = frez.Groups[1].Value == "1";
                RaiseDataEvent("Freeze");
                var h = FreezeChanged; if (h != null) h(Freeze);
                return;
            }

            if (normalizedLine == "%2FREZ=OK")
                return;

            // FREZ=ERR1: Beamer kann nur Class 1 -> kein Freeze moeglich. Kein ErrorLog-Spam, nur Warn.
            // ERR3: Beamer aus -> Freeze ist implizit weg.
            if (normalizedLine.StartsWith("%2FREZ=ERR"))
            {
                if (normalizedLine == "%2FREZ=ERR1")
                    Debug.Warn(friendlyName + " PJLink: Freeze nicht unterstuetzt (nur Class 1?)");
                if (Freeze)
                {
                    Freeze = false;
                    RaiseDataEvent("Freeze");
                    var h = FreezeChanged; if (h != null) h(false);
                }
                return;
            }

            // AVMT/INPT liefern ERR3 solange der Beamer aus ist — erwartet, kein Fehler.
            // Wir schlucken das (keine ErrorLog-Zeile), reflektieren aber AvMute=false
            // im Modell damit ein evtl. anhaengender UI-State sauber bleibt.
            if (Regex.IsMatch(normalizedLine, @"^%1(AVMT|INPT|LAMP)=ERR[234]$"))
            {
                if (normalizedLine.StartsWith("%1AVMT=") && AvMute)
                {
                    AvMute = false;
                    RaiseDataEvent("AvMute");
                    var h = AvMuteChanged; if (h != null) h(false);
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
            pollAvMuteTimer?.Dispose();
            md5?.Dispose();
            base.Dispose();
        }
    }
}