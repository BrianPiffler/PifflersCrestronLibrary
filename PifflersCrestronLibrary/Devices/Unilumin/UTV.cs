using System;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Unilumin
{
    public class UTV : BasicTCP
    {
        public enum InputSource
        {
            Hdmi1,
            Hdmi2,
            Ops,
            Home
        }

        public enum DisplayMode
        {
            Standard,
            Soft,
            Cinema,
            Meeting
        }

        public enum RemoteKey
        {
            Up,
            Down,
            Left,
            Right,
            Ok,
            Input,
            Menu,
            Return
        }

        private ushort deviceId = 1;

        public ushort DeviceId
        {
            get { return deviceId; }
        }

        public int? Volume { get; private set; }
        public int? Brightness { get; private set; }
        public bool? Mute { get; private set; }
        public bool? EyeProtection { get; private set; }
        public InputSource? CurrentInput { get; private set; }
        public DisplayMode? CurrentDisplayMode { get; private set; }
        public bool? IsPowerOn { get; private set; }

        public UTV(string host, string friendlyName)
            : base(host, friendlyName, 6688)
        {
        }

        protected override void InitializeRegexDictionary()
        {
            // No stable response format provided in the protocol table.
        }

        protected override void KeepAliveCallback(object _)
        {
            // Use supported sync command instead of generic text keepalive.
            SyncStartupStatus();
        }

        public bool SetDeviceId(ushort id)
        {
            if (id < 1 || id > 99)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid device id: " + id + " (allowed 1..99)");
                return false;
            }

            deviceId = id;
            return true;
        }

        public void PowerOn()
        {
            SendInstruction("s!001");
            IsPowerOn = true;
            RaiseDataEvent("Power");
        }

        public void Shutdown()
        {
            SendInstruction("s!000");
            IsPowerOn = false;
            RaiseDataEvent("Power");
        }

        public void SetInput(InputSource source)
        {
            if (source == InputSource.Hdmi1)
                SendInstruction("s\"004");
            else if (source == InputSource.Hdmi2)
                SendInstruction("s\"014");
            else if (source == InputSource.Ops)
                SendInstruction("s\"024");
            else
                SendInstruction("s\"00A");

            CurrentInput = source;
            RaiseDataEvent("Input");
        }

        public void CycleSignalSource()
        {
            SendInstruction("s\"00Z");
        }

        public void BrightnessUp()
        {
            SendInstruction("s$901");
        }

        public void BrightnessDown()
        {
            SendInstruction("s$900");
        }

        public bool SetDisplayBrightness(int value)
        {
            if (!IsPercent(value, "display brightness"))
                return false;

            SendInstruction("uv" + value.ToString("D3"));
            Brightness = value;
            RaiseDataEvent("Brightness");
            return true;
        }

        public void VolumeUp()
        {
            SendInstruction("s5901");
        }

        public void VolumeDown()
        {
            SendInstruction("s5900");
        }

        public bool SetVolume(int value)
        {
            if (!IsPercent(value, "volume"))
                return false;

            SendInstruction("s5" + value.ToString("D3"));
            Volume = value;
            RaiseDataEvent("Volume");
            return true;
        }

        public void SetMute(bool mute)
        {
            SendInstruction(mute ? "s6001" : "s6000");
            Mute = mute;
            RaiseDataEvent("Mute");
        }

        public bool SendNumberKey(int value)
        {
            if (value < 0 || value > 9)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid number key: " + value + " (allowed 0..9)");
                return false;
            }

            SendInstruction("s@00" + value);
            return true;
        }

        public void SetBlank(bool on)
        {
            SendInstruction(on ? "s(000" : "s(001");
        }

        public void OpenSystemSettings()
        {
            SendInstruction("st005");
        }

        public void PlayVideo()
        {
            SendInstruction("sv001");
        }

        public void PauseVideo()
        {
            SendInstruction("sv002");
        }

        public void SendRemoteKey(RemoteKey key)
        {
            if (key == RemoteKey.Up)
                SendInstruction("sA000");
            else if (key == RemoteKey.Down)
                SendInstruction("sA001");
            else if (key == RemoteKey.Left)
                SendInstruction("sA002");
            else if (key == RemoteKey.Right)
                SendInstruction("sA003");
            else if (key == RemoteKey.Ok)
                SendInstruction("sA004");
            else if (key == RemoteKey.Input)
                SendInstruction("sA005");
            else if (key == RemoteKey.Menu)
                SendInstruction("sA006");
            else
                SendInstruction("sA007");
        }

        public void Screenshot()
        {
            SendInstruction("jb001");
        }

        public void Delete()
        {
            SendInstruction("sd003");
        }

        public void OpenWhiteboard()
        {
            SendInstruction("bb001");
        }

        public void EnterScreenProjection()
        {
            SendInstruction("bb002");
        }

        public void ExitScreenProjection()
        {
            SendInstruction("bb003");
        }

        public void SetDisplayMode(DisplayMode mode)
        {
            if (mode == DisplayMode.Standard)
                SendInstruction("uu000");
            else if (mode == DisplayMode.Soft)
                SendInstruction("uu001");
            else if (mode == DisplayMode.Cinema)
                SendInstruction("uu002");
            else
                SendInstruction("uu003");

            CurrentDisplayMode = mode;
            RaiseDataEvent("DisplayMode");
        }

        public void SetEyeProtection(bool enabled)
        {
            SendInstruction(enabled ? "uw001" : "uw000");
            EyeProtection = enabled;
            RaiseDataEvent("EyeProtection");
        }

        public void SyncStartupStatus()
        {
            SendInstruction("ux000");
        }

        public void RequestCurrentVideoSourceSync()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] video source sync command code not provided in protocol table");
        }

        public void RequestCurrentDisplayModeSync()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] display mode sync command code not provided in protocol table");
        }

        public void RequestCurrentBrightnessSync()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] brightness sync command code not provided in protocol table");
        }

        public void RequestEyeProtectionSync()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] eye protection sync command code not provided in protocol table");
        }

        public void RequestScreenModel()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] screen model inquiry command code not provided in protocol table");
        }

        public void RequestScreenProjectionWizardStatus()
        {
            Debug.Warn("[TCP] [" + friendlyName + "] projection wizard status command code not provided in protocol table");
        }

        private bool IsPercent(int value, string label)
        {
            if (value >= 0 && value <= 100)
                return true;

            Debug.Warn("[TCP] [" + friendlyName + "] invalid " + label + ": " + value + " (allowed 0..100)");
            return false;
        }

        private void SendInstruction(string commandBody)
        {
            SendRaw(BuildCommand(commandBody));
        }

        private string BuildCommand(string commandBody)
        {
            return "8" + deviceId.ToString("D2") + commandBody + "\r";
        }
    }
}

