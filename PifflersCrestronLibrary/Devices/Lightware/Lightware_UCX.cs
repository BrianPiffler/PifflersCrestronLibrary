using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PifflersCrestronLibrary.Communication;

namespace PifflersCrestronLibrary.Devices.Lightware
{
    public class Lightware_UCX : BasicTCP
    {
        public enum Model
        {
            Ucx2x2H40,
            Ucx4x2Hc40,
            Ucx4x3Hc40,
            Ucx4x3Hcm40
        }

        public enum MatrixInput
        {
            Break = 0,
            Input1 = 1,
            Input2 = 2,
            Input3 = 3,
            Input4 = 4,
            Input5 = 5,
            Input1A = 101,
            Input1B = 102
        }

        public enum AudioInput
        {
            Break = 0,
            Input1 = 1,
            Input2 = 2,
            Input3 = 3,
            Input4 = 4,
            Input1A = 101,
            Input1B = 102
        }

        public enum VideoOutput
        {
            Output1 = 1,
            Output2 = 2,
            Output3 = 3
        }

        public enum UsbInput
        {
            Break = 0,
            Input1 = 1,
            Input2 = 2,
            Input3 = 3,
            Input4 = 4
        }

        public enum HdcpMode
        {
            Auto,
            Always
        }

        public enum HdcpSetting
        {
            Off,
            Hdcp14,
            Hdcp22
        }

        public enum UsbAutoselectPolicy
        {
            FollowVideo,
            Off
        }

        private readonly Dictionary<string, string> activeHdcpVersion = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> inputSignalPresent = new Dictionary<string, bool>();
        private readonly Dictionary<int, string> videoOutputTieStatus = new Dictionary<int, string>();

        private int analogAudioOutputVolume;
        private string audioInput = "0";
        private UsbInput usbInput = UsbInput.Break;

        private readonly HashSet<string> validMatrixInputs = new HashSet<string>();
        private readonly HashSet<string> validAudioInputs = new HashSet<string>();
        private int outputs;

        public Model DeviceModel { get; private set; }

        public int AnalogAudioOutputVolume { get { return analogAudioOutputVolume; } }
        public string AudioInputCode { get { return audioInput; } }
        public UsbInput UsbInputSelection { get { return usbInput; } }

        public Lightware_UCX(string host, string friendlyName, Model model)
            : base(host, friendlyName, 6107)
        {
            ConfigureModel(model);
        }

        public Lightware_UCX(string host, string friendlyName)
            : this(host, friendlyName, Model.Ucx4x3Hc40)
        {
        }

        protected override void InitializeRegexDictionary()
        {
            matchStringDict.Add(@"/V1/MEDIA/VIDEO/XP/I([1-5][AB]?)\.ActiveHdcpVersion=(N/A|None|HDCP 1\.4|HDCP 2\.2)\r\n", MatchActiveHdcpVersion);
            matchStringDict.Add(@"/V1/MEDIA/VIDEO/I([1-5][AB]?)\.SignalPresent=(true|false)\r\n", MatchInputSignalStatus);
            matchStringDict.Add(@"/V1/MEDIA/USB/XP/H1\.ConnectedSource=U?([0-4])\r\n", MatchUsbInput);
            matchStringDict.Add(@"/V1/MEDIA/VIDEO/XP/O([1-3])\.ConnectedSource=I?([0-5][AB]?)\r\n", MatchVideoOutputTieStatus);
            matchStringDict.Add(@"/V1/MEDIA/AUDIO/O([1-4])\.VolumePercent=(\d{1,3})(\.\d+)?\r\n", MatchAnalogAudioOutputVolume);
            matchStringDict.Add(@"/V1/MEDIA/AUDIO/XP/O([1-4])\.ConnectedSource=I([1-4][AB]?)\r\n", MatchAudioInput);
            matchStringDict.Add(@"[npm]E .+? %E(\d{3}:.*?)\r\n", MatchError);
        }

        protected override void KeepAliveCallback(object _)
        {
            GetUsbInput();
        }

        public void SetAnalogAudioOutputVolume(int value)
        {
            if (value < 0 || value > 100)
                return;

            SendRaw(string.Format("SET /V1/MEDIA/AUDIO/O{0}.VolumePercent={1}\r\n", outputs + 1, value));
        }

        public void GetAnalogAudioOutputVolume()
        {
            SendRaw(string.Format("GET /V1/MEDIA/AUDIO/O{0}.VolumePercent\r\n", outputs + 1));
        }

        public void SetAudioInput(AudioInput input)
        {
            var code = ToAudioInputCode(input);
            if (!validAudioInputs.Contains(code))
                return;

            SendRaw(string.Format("CALL /V1/MEDIA/AUDIO/XP:switch(I{0}:O{1})\r\n", code, outputs + 1));
        }

        public void GetAudioInput()
        {
            SendRaw(string.Format("GET /V1/MEDIA/AUDIO/XP/O{0}.ConnectedSource\r\n", outputs + 1));
        }

        public void SetAudioPort1Mute(MatrixInput input, bool mute)
        {
            var code = ToMatrixInputCode(input);
            if (!validMatrixInputs.Contains(code) || code == "0")
                return;

            SendRaw(string.Format("SET /V1/MEDIA/AUDIO/XP/I{0}.Mute={1}\r\n", code, mute ? "true" : "false"));
        }

        public void SetAudioPort2Mute(bool mute)
        {
            SendRaw(string.Format("SET /V1/MEDIA/AUDIO/O{0}.Mute={1}\r\n", outputs + 1, mute ? "true" : "false"));
        }

        public void SetHdcpMode(VideoOutput output, HdcpMode mode)
        {
            var outputNo = (int)output;
            if (outputNo < 1 || outputNo > outputs)
                return;

            SendRaw(string.Format("SET /V1/MEDIA/VIDEO/O{0}.HdcpMode={1}\r\n", outputNo, mode == HdcpMode.Always ? "Always" : "Auto"));
        }

        public void SetHdcpSetting(MatrixInput input, HdcpSetting setting)
        {
            var inputCode = ToMatrixInputCode(input);
            if (!validMatrixInputs.Contains(inputCode) || inputCode == "0")
                return;

            SendRaw(string.Format("SET /V1/MEDIA/VIDEO/I{0}/HDCP.AllowedHdcpVersion={1}\r\n", inputCode, ToHdcpSettingValue(setting)));
        }

        public void GetInputSignalStatus(MatrixInput input)
        {
            var inputCode = ToMatrixInputCode(input);
            if (!validMatrixInputs.Contains(inputCode) || inputCode == "0")
                return;

            SendRaw(string.Format("GET /V1/MEDIA/VIDEO/I{0}.SignalPresent\r\n", inputCode));
        }

        public void SetUsbAutoselect(UsbAutoselectPolicy policy)
        {
            SendRaw(string.Format("SET /V1/MEDIA/USB/AUTOSELECT/H1.Policy={0}\r\n", policy == UsbAutoselectPolicy.FollowVideo ? "Follow Video" : "Off"));
        }

        public void SetUsbFollowVideoPort(VideoOutput output)
        {
            var outputNo = (int)output;
            if (outputNo < 1 || outputNo > outputs)
                return;

            SendRaw(string.Format("SET /V1/MEDIA/USB/AUTOSELECT/H1.VideoFollowPort=O{0}\r\n", outputNo));
        }

        public void SetUsbInput(UsbInput input)
        {
            var code = input == UsbInput.Break ? "0" : "U" + (int)input;
            SendRaw(string.Format("CALL /V1/MEDIA/USB/XP:switch({0}:H1)\r\n", code));
        }

        public void GetUsbInput()
        {
            SendRaw("GET /V1/MEDIA/USB/XP/H1.ConnectedSource\r\n");
        }

        public void SetVideoMatrixTie(MatrixInput input, VideoOutput output)
        {
            var inputCode = ToMatrixInputCode(input);
            var outputNo = (int)output;
            if (!validMatrixInputs.Contains(inputCode) || outputNo < 1 || outputNo > outputs)
                return;

            SendRaw(string.Format("CALL /V1/MEDIA/VIDEO/XP:switch(I{0}:O{1})\r\n", inputCode, outputNo));
        }

        public void SetVideoMatrixTieAll(MatrixInput input)
        {
            var inputCode = ToMatrixInputCode(input);
            if (!validMatrixInputs.Contains(inputCode))
                return;

            SendRaw(string.Format("CALL /V1/MEDIA/VIDEO/XP:switchAll(I{0})\r\n", inputCode));
        }

        public void GetVideoOutputTieStatus(VideoOutput output)
        {
            var outputNo = (int)output;
            if (outputNo < 1 || outputNo > outputs)
                return;

            SendRaw(string.Format("GET /V1/MEDIA/VIDEO/XP/O{0}.ConnectedSource\r\n", outputNo));
        }

        public void SetVideoPortMute(MatrixInput input, bool mute)
        {
            var inputCode = ToMatrixInputCode(input);
            if (!validMatrixInputs.Contains(inputCode) || inputCode == "0")
                return;

            SendRaw(string.Format("SET /V1/MEDIA/VIDEO/XP/I{0}.Mute={1}\r\n", inputCode, mute ? "true" : "false"));
        }

        public bool TryGetActiveHdcpVersion(MatrixInput input, out string version)
        {
            return activeHdcpVersion.TryGetValue(ToMatrixInputCode(input), out version);
        }

        public bool TryGetInputSignalPresent(MatrixInput input, out bool present)
        {
            return inputSignalPresent.TryGetValue(ToMatrixInputCode(input), out present);
        }

        public bool TryGetVideoOutputTieStatus(VideoOutput output, out string inputCode)
        {
            return videoOutputTieStatus.TryGetValue((int)output, out inputCode);
        }

        private void ConfigureModel(Model model)
        {
            DeviceModel = model;
            validMatrixInputs.Clear();
            validAudioInputs.Clear();

            if (model == Model.Ucx2x2H40)
            {
                outputs = 2;
                AddMatrixInputs("1", "2", "0");
                AddAudioInputs("1", "2", "0");
                return;
            }

            if (model == Model.Ucx4x2Hc40)
            {
                outputs = 2;
                AddMatrixInputs("1", "2", "3", "4", "5", "0");
                AddAudioInputs("1", "2", "3", "4", "0");
                return;
            }

            if (model == Model.Ucx4x3Hcm40)
            {
                outputs = 3;
                AddMatrixInputs("1A", "1B", "2", "3", "0");
                AddAudioInputs("1A", "1B", "2", "3", "0");
                return;
            }

            outputs = 3;
            AddMatrixInputs("1", "2", "3", "4", "5", "0");
            AddAudioInputs("1", "2", "3", "4", "0");
        }

        private void AddMatrixInputs(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
                validMatrixInputs.Add(values[i]);
        }

        private void AddAudioInputs(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
                validAudioInputs.Add(values[i]);
        }

        private static string ToMatrixInputCode(MatrixInput input)
        {
            if (input == MatrixInput.Break)
                return "0";
            if (input == MatrixInput.Input1)
                return "1";
            if (input == MatrixInput.Input2)
                return "2";
            if (input == MatrixInput.Input3)
                return "3";
            if (input == MatrixInput.Input4)
                return "4";
            if (input == MatrixInput.Input5)
                return "5";
            if (input == MatrixInput.Input1A)
                return "1A";
            if (input == MatrixInput.Input1B)
                return "1B";
            return "0";
        }

        private static string ToAudioInputCode(AudioInput input)
        {
            if (input == AudioInput.Break)
                return "0";
            if (input == AudioInput.Input1)
                return "1";
            if (input == AudioInput.Input2)
                return "2";
            if (input == AudioInput.Input3)
                return "3";
            if (input == AudioInput.Input4)
                return "4";
            if (input == AudioInput.Input1A)
                return "1A";
            if (input == AudioInput.Input1B)
                return "1B";
            return "0";
        }

        private static string ToHdcpSettingValue(HdcpSetting setting)
        {
            if (setting == HdcpSetting.Hdcp14)
                return "HDCP 1.4";
            if (setting == HdcpSetting.Hdcp22)
                return "HDCP 2.2";
            return "Off";
        }

        private void MatchActiveHdcpVersion(MatchCollection match)
        {
            var inputCode = match[0].Groups[1].Value;
            var value = match[0].Groups[2].Value;
            activeHdcpVersion[inputCode] = value;
            RaiseDataEvent("ActiveHDCPVersion");
        }

        private void MatchInputSignalStatus(MatchCollection match)
        {
            var inputCode = match[0].Groups[1].Value;
            var present = match[0].Groups[2].Value == "true";
            inputSignalPresent[inputCode] = present;
            RaiseDataEvent("InputSignalStatus");
        }

        private void MatchUsbInput(MatchCollection match)
        {
            var value = int.Parse(match[0].Groups[1].Value);
            usbInput = (UsbInput)value;
            RaiseDataEvent("USBInput");
        }

        private void MatchVideoOutputTieStatus(MatchCollection match)
        {
            var output = int.Parse(match[0].Groups[1].Value);
            var inputCode = match[0].Groups[2].Value;
            videoOutputTieStatus[output] = inputCode;
            RaiseDataEvent("VideoOutputTieStatus");
        }

        private void MatchAnalogAudioOutputVolume(MatchCollection match)
        {
            var output = int.Parse(match[0].Groups[1].Value);
            if (output != outputs + 1)
                return;

            var value = int.Parse(match[0].Groups[2].Value);
            if (value < 0 || value > 100)
                return;

            analogAudioOutputVolume = value;
            RaiseDataEvent("AnalogAudioOutputVolume");
        }

        private void MatchAudioInput(MatchCollection match)
        {
            var output = int.Parse(match[0].Groups[1].Value);
            if (output != outputs + 1)
                return;

            audioInput = match[0].Groups[2].Value;
            RaiseDataEvent("AudioInput");
        }

        private void MatchError(MatchCollection match)
        {
            RaiseDataEvent("Error");
        }
    }
}

