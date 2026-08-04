using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PifflersCrestronLibrary.Communication;

namespace PifflersCrestronLibrary.Devices.Shure
{
    public class Shure_P300 : BasicTCP
    {
        public enum AudioChannel
        {
            AllChannels = 0,
            DanteInput1MicProcessing = 1,
            DanteInput2MicProcessing = 2,
            DanteInput3MicProcessing = 3,
            DanteInput4MicProcessing = 4,
            DanteInput5MicProcessing = 5,
            DanteInput6MicProcessing = 6,
            DanteInput7MicProcessing = 7,
            DanteInput8MicProcessing = 8,
            DanteInput9 = 9,
            DanteInput10 = 10,
            AnalogInput1 = 11,
            AnalogInput2 = 12,
            UsbInput = 13,
            MobileInput = 14,
            DanteOutput1 = 15,
            DanteOutput2 = 16,
            AnalogOutput1 = 17,
            AnalogOutput2 = 18,
            UsbOutput = 19,
            MobileOutput = 20,
            AutomixerOutput = 21,
            AecReferenceGateInhibitReference = 22,
            DanteOutput3 = 23,
            DanteOutput4 = 24,
            DanteOutput5 = 25,
            DanteOutput6 = 26,
            DanteOutput7 = 27,
            DanteOutput8 = 28
        }

        public enum AnalogInputGainMode
        {
            LineLevel,
            AuxLevel
        }

        public enum AnalogOutputGainMode
        {
            LineLevel,
            AuxLevel,
            MicLevel
        }

        public enum AutomixerMode
        {
            Manual,
            GainShare,
            Gating
        }

        public enum MatrixInput
        {
            DanteInput1MicProcessing = 1,
            DanteInput2MicProcessing = 2,
            DanteInput3MicProcessing = 3,
            DanteInput4MicProcessing = 4,
            DanteInput5MicProcessing = 5,
            DanteInput6MicProcessing = 6,
            DanteInput7MicProcessing = 7,
            DanteInput8MicProcessing = 8,
            DanteInput9 = 9,
            DanteInput10 = 10,
            AnalogInput1 = 11,
            AnalogInput2 = 12,
            UsbInput = 13,
            MobileInput = 14,
            AutomixerOutput = 21
        }

        public enum MatrixOutput
        {
            DanteOutput1 = 15,
            DanteOutput2 = 16,
            AnalogOutput1 = 17,
            AnalogOutput2 = 18,
            UsbOutput = 19,
            MobileOutput = 20,
            DanteOutput3 = 23,
            DanteOutput4 = 24,
            DanteOutput5 = 25,
            DanteOutput6 = 26,
            DanteOutput7 = 27,
            DanteOutput8 = 28
        }

        public enum AutomixerGateChannel
        {
            Channel1 = 1,
            Channel2 = 2,
            Channel3 = 3,
            Channel4 = 4,
            Channel5 = 5,
            Channel6 = 6,
            Channel7 = 7,
            Channel8 = 8,
            GateInhibit = 22
        }
        private readonly Dictionary<int, double> audioGain = new Dictionary<int, double>();
        private readonly Dictionary<int, bool> audioMute = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> automixerGate = new Dictionary<int, bool>();
        private readonly Dictionary<string, double> matrixMixerGain = new Dictionary<string, double>();
        private readonly Dictionary<string, bool> matrixMixerRouting = new Dictionary<string, bool>();
        private readonly Dictionary<int, int> analogInputGainSwitch = new Dictionary<int, int>();
        private readonly Dictionary<int, int> analogOutputGainSwitch = new Dictionary<int, int>();
        private readonly Dictionary<int, string> automixerMode = new Dictionary<int, string>();
        private readonly Dictionary<int, double> automixerOffAttenuation = new Dictionary<int, double>();
        private readonly Dictionary<int, bool> automixerPostGateMute = new Dictionary<int, bool>();

        private bool deviceAudioMute;
        private bool flashLights;
        private bool callStatusOnHook;
        private bool callStatusModeEnabled;
        private int preset;

        private static readonly Dictionary<int, string> ChannelCodeByNumber = new Dictionary<int, string>
        {
            { 0, "00" },
            { 1, "01" }, { 2, "02" }, { 3, "03" }, { 4, "04" }, { 5, "05" }, { 6, "06" }, { 7, "07" }, { 8, "08" },
            { 9, "09" }, { 10, "10" }, { 11, "11" }, { 12, "12" }, { 13, "13" }, { 14, "14" },
            { 15, "15" }, { 16, "16" }, { 17, "17" }, { 18, "18" }, { 19, "19" }, { 20, "20" },
            { 21, "21" }, { 22, "22" }, { 23, "23" }, { 24, "24" }, { 25, "25" }, { 26, "26" }, { 27, "27" }, { 28, "28" }
        };

        public bool DeviceAudioMute { get { return deviceAudioMute; } }
        public bool FlashLights { get { return flashLights; } }
        public bool CallStatusOnHook { get { return callStatusOnHook; } }
        public bool CallStatusModeEnabled { get { return callStatusModeEnabled; } }
        public int Preset { get { return preset; } }

        public bool TryGetAudioGain(int channel, out double value)
        {
            return audioGain.TryGetValue(channel, out value);
        }

        public bool TryGetAudioGain(AudioChannel channel, out double value)
        {
            return TryGetAudioGain((int)channel, out value);
        }

        public bool TryGetAudioMute(int channel, out bool muted)
        {
            return audioMute.TryGetValue(channel, out muted);
        }

        public bool TryGetAudioMute(AudioChannel channel, out bool muted)
        {
            return TryGetAudioMute((int)channel, out muted);
        }

        public bool TryGetAutomixerGateStatus(int channel, out bool active)
        {
            return automixerGate.TryGetValue(channel, out active);
        }

        public bool TryGetAutomixerGateStatus(AutomixerGateChannel channel, out bool active)
        {
            return TryGetAutomixerGateStatus((int)channel, out active);
        }

        public bool TryGetAutomixerMode(int channel, out string mode)
        {
            return automixerMode.TryGetValue(channel, out mode);
        }

        public bool TryGetAutomixerMode(AudioChannel channel, out string mode)
        {
            return TryGetAutomixerMode((int)channel, out mode);
        }

        public bool TryGetAutomixerOffAttenuation(int channel, out double value)
        {
            return automixerOffAttenuation.TryGetValue(channel, out value);
        }

        public bool TryGetAutomixerOffAttenuation(AudioChannel channel, out double value)
        {
            return TryGetAutomixerOffAttenuation((int)channel, out value);
        }

        public bool TryGetAutomixerPostGateMute(int channel, out bool muted)
        {
            return automixerPostGateMute.TryGetValue(channel, out muted);
        }

        public bool TryGetAutomixerPostGateMute(AudioChannel channel, out bool muted)
        {
            return TryGetAutomixerPostGateMute((int)channel, out muted);
        }

        public bool TryGetAnalogInputGainSwitch(int channel, out int level)
        {
            return analogInputGainSwitch.TryGetValue(channel, out level);
        }

        public bool TryGetAnalogInputGainSwitch(AudioChannel channel, out int level)
        {
            return TryGetAnalogInputGainSwitch((int)channel, out level);
        }

        public bool TryGetAnalogOutputGainSwitch(int channel, out int level)
        {
            return analogOutputGainSwitch.TryGetValue(channel, out level);
        }

        public bool TryGetAnalogOutputGainSwitch(AudioChannel channel, out int level)
        {
            return TryGetAnalogOutputGainSwitch((int)channel, out level);
        }

        public bool TryGetMatrixMixerGain(int input, int output, out double value)
        {
            return matrixMixerGain.TryGetValue(BuildCrosspointKey(input, output), out value);
        }

        public bool TryGetMatrixMixerGain(MatrixInput input, MatrixOutput output, out double value)
        {
            return TryGetMatrixMixerGain((int)input, (int)output, out value);
        }

        public bool TryGetMatrixMixerRouting(int input, int output, out bool enabled)
        {
            return matrixMixerRouting.TryGetValue(BuildCrosspointKey(input, output), out enabled);
        }

        public bool TryGetMatrixMixerRouting(MatrixInput input, MatrixOutput output, out bool enabled)
        {
            return TryGetMatrixMixerRouting((int)input, (int)output, out enabled);
        }

        public Shure_P300(string host, string friendlyName)
            : base(host, friendlyName, 2202)
        {
        }

        protected override void InitializeRegexDictionary()
        {
            matchStringDict.Add(@"< REP (0?0|1[12]) AUDIO_IN_LVL_SWITCH (LINE|AUX)_LVL >", MatchAnalogInputGainSwitch);
            matchStringDict.Add(@"< REP (0?0|1[78]) AUDIO_OUT_LVL_SWITCH (LINE|AUX|MIC)_LVL >", MatchAnalogOutputGainSwitch);
            matchStringDict.Add(@"< REP (0?[0-9]|1[0-9]|2[02-8]) AUDIO_GAIN_HI_RES (\d{4}) >", MatchAudioGain);
            matchStringDict.Add(@"< REP (0?[0-9]|1[0-9]|2[03-8]) AUDIO_MUTE (ON|OFF) >", MatchAudioMute);
            matchStringDict.Add(@"< REP (0?[1-8]|22) AUTOMXR_GATE (ON|OFF) >", MatchAutomixerGateStatus);
            matchStringDict.Add(@"< REP (0?0|21) AUTOMXR_MODE (MANUAL|GAINSHARE|GATING) >", MatchAutomixerMode);
            matchStringDict.Add(@"< REP (0?0|21) AUTOMXR_OFF_ATT (\d{3}) >", MatchAutomixerOffAttenuation);
            matchStringDict.Add(@"< REP (0?0|21) AUTOMXR_MUTE (ON|OFF) >", MatchAutomixerPostGateMute);
            matchStringDict.Add(@"< REP ONHOOK_STATE (ON|OFF)HOOK >", MatchCallStatus);
            matchStringDict.Add(@"< REP ONHOOK_ENABLE (ON|OFF) >", MatchCallStatusMode);
            matchStringDict.Add(@"< REP DEVICE_AUDIO_MUTE (ON|OFF) >", MatchDeviceAudioMute);
            matchStringDict.Add(@"< REP FLASH (ON|OFF) >", MatchFlashLights);
            matchStringDict.Add(@"< REP (0?[1-9]|1[0-4]|21) MATRIX_MXR_GAIN (1[5-9]|2[03-8]) (\d{4}) >", MatchMatrixMixerGain);
            matchStringDict.Add(@"< REP (0?[1-9]|1[0-4]|21) MATRIX_MXR_ROUTE (1[5-9]|2[03-8]) (ON|OFF) >", MatchMatrixMixerRouting);
            matchStringDict.Add(@"< REP PRESET (0[1-9]|10) >", MatchPreset);
            matchStringDict.Add(@"< REP ERR >", MatchError);
        }

        protected override void KeepAliveCallback(object _)
        {
            SendRaw("< GET DEVICE_AUDIO_MUTE >");
        }

        public void SetAnalogInputGainSwitch(int channel, int level)
        {
            if (!IsChannelValid(channel, 0, 11, 12))
                return;

            var protocolLevel = ToAnalogInputLevelProtocol(level);
            if (protocolLevel == null)
                return;

            SendRaw(string.Format("< SET {0} AUDIO_IN_LVL_SWITCH {1}_LVL >", ChannelCodeByNumber[channel], protocolLevel));
        }

        public void SetAnalogInputGainSwitch(AudioChannel channel, AnalogInputGainMode level)
        {
            SetAnalogInputGainSwitch((int)channel, (int)level);
        }

        public void GetAnalogInputGainSwitch(int channel)
        {
            if (!IsChannelValid(channel, 0, 11, 12))
                return;

            SendRaw(string.Format("< GET {0} AUDIO_IN_LVL_SWITCH >", ChannelCodeByNumber[channel]));
        }

        public void GetAnalogInputGainSwitch(AudioChannel channel)
        {
            GetAnalogInputGainSwitch((int)channel);
        }

        public void SetAnalogOutputGainSwitch(int channel, int level)
        {
            if (!IsChannelValid(channel, 0, 17, 18))
                return;

            var protocolLevel = ToAnalogOutputLevelProtocol(level);
            if (protocolLevel == null)
                return;

            SendRaw(string.Format("< SET {0} AUDIO_OUT_LVL_SWITCH {1}_LVL >", ChannelCodeByNumber[channel], protocolLevel));
        }

        public void SetAnalogOutputGainSwitch(AudioChannel channel, AnalogOutputGainMode level)
        {
            SetAnalogOutputGainSwitch((int)channel, (int)level);
        }

        public void GetAnalogOutputGainSwitch(int channel)
        {
            if (!IsChannelValid(channel, 0, 17, 18))
                return;

            SendRaw(string.Format("< GET {0} AUDIO_OUT_LVL_SWITCH >", ChannelCodeByNumber[channel]));
        }

        public void GetAnalogOutputGainSwitch(AudioChannel channel)
        {
            GetAnalogOutputGainSwitch((int)channel);
        }

        public void SetAudioGain(int channel, double gainDb)
        {
            if (!IsChannelValid(channel, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 23, 24, 25, 26, 27, 28))
                return;

            if (gainDb < -110 || gainDb > 30)
                return;

            var encoded = (int)Math.Round((gainDb + 110) * 10.0);
            SendRaw(string.Format("< SET {0} AUDIO_GAIN_HI_RES {1:D4} >", ChannelCodeByNumber[channel], encoded));
        }

        public void SetAudioGain(AudioChannel channel, double gainDb)
        {
            SetAudioGain((int)channel, gainDb);
        }

        public void GetAudioGain(int channel)
        {
            if (!IsChannelValid(channel, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 23, 24, 25, 26, 27, 28))
                return;

            SendRaw(string.Format("< GET {0} AUDIO_GAIN_HI_RES >", ChannelCodeByNumber[channel]));
        }

        public void GetAudioGain(AudioChannel channel)
        {
            GetAudioGain((int)channel);
        }

        public void SetAudioMute(int channel, bool mute)
        {
            if (!IsChannelValid(channel, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 23, 24, 25, 26, 27, 28))
                return;

            SendRaw(string.Format("< SET {0} AUDIO_MUTE {1} >", ChannelCodeByNumber[channel], mute ? "ON" : "OFF"));
        }

        public void SetAudioMute(AudioChannel channel, bool mute)
        {
            SetAudioMute((int)channel, mute);
        }

        public void GetAudioMute(int channel)
        {
            if (!IsChannelValid(channel, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 23, 24, 25, 26, 27, 28))
                return;

            SendRaw(string.Format("< GET {0} AUDIO_MUTE >", ChannelCodeByNumber[channel]));
        }

        public void GetAudioMute(AudioChannel channel)
        {
            GetAudioMute((int)channel);
        }

        public void GetAutomixerGateStatus(int channel)
        {
            if (!IsChannelValid(channel, 1, 2, 3, 4, 5, 6, 7, 8, 22))
                return;

            SendRaw(string.Format("< GET {0} AUTOMXR_GATE >", ChannelCodeByNumber[channel]));
        }

        public void GetAutomixerGateStatus(AutomixerGateChannel channel)
        {
            GetAutomixerGateStatus((int)channel);
        }

        public void SetAutomixerMode(int channel, string mode)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            var normalized = NormalizeLevel(mode, "MANUAL", "GAINSHARE", "GATING");
            if (normalized == null)
                return;

            SendRaw(string.Format("< SET {0} AUTOMXR_MODE {1} >", ChannelCodeByNumber[channel], normalized));
        }

        public void SetAutomixerMode(AudioChannel channel, AutomixerMode mode)
        {
            SetAutomixerMode((int)channel, ToProtocol(mode));
        }

        public void GetAutomixerMode(int channel)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            SendRaw(string.Format("< GET {0} AUTOMXR_MODE >", ChannelCodeByNumber[channel]));
        }

        public void GetAutomixerMode(AudioChannel channel)
        {
            GetAutomixerMode((int)channel);
        }

        public void SetAutomixerOffAttenuation(int channel, double valueDb)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            if (valueDb < -110 || valueDb > -3)
                return;

            var encoded = (int)Math.Round(valueDb + 110);
            SendRaw(string.Format("< SET {0} AUTOMXR_OFF_ATT {1:D3} >", ChannelCodeByNumber[channel], encoded));
        }

        public void SetAutomixerOffAttenuation(AudioChannel channel, double valueDb)
        {
            SetAutomixerOffAttenuation((int)channel, valueDb);
        }

        public void GetAutomixerOffAttenuation(int channel)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            SendRaw(string.Format("< GET {0} AUTOMXR_OFF_ATT >", ChannelCodeByNumber[channel]));
        }

        public void GetAutomixerOffAttenuation(AudioChannel channel)
        {
            GetAutomixerOffAttenuation((int)channel);
        }

        public void SetAutomixerPostGateMute(int channel, bool mute)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            SendRaw(string.Format("< SET {0} AUTOMXR_MUTE {1} >", ChannelCodeByNumber[channel], mute ? "ON" : "OFF"));
        }

        public void SetAutomixerPostGateMute(AudioChannel channel, bool mute)
        {
            SetAutomixerPostGateMute((int)channel, mute);
        }

        public void GetAutomixerPostGateMute(int channel)
        {
            if (!IsChannelValid(channel, 0, 21))
                return;

            SendRaw(string.Format("< GET {0} AUTOMXR_MUTE >", ChannelCodeByNumber[channel]));
        }

        public void GetAutomixerPostGateMute(AudioChannel channel)
        {
            GetAutomixerPostGateMute((int)channel);
        }

        public void GetCallStatus()
        {
            SendRaw("< GET ONHOOK_STATE >");
        }

        public void SetCallStatusMode(bool enable)
        {
            SendRaw(string.Format("< SET ONHOOK_ENABLE {0} >", enable ? "ON" : "OFF"));
        }

        public void GetCallStatusMode()
        {
            SendRaw("< GET ONHOOK_ENABLE >");
        }

        public void SetDeviceAudioMute(bool mute)
        {
            SendRaw(string.Format("< SET DEVICE_AUDIO_MUTE {0} >", mute ? "ON" : "OFF"));
        }

        public void GetDeviceAudioMute()
        {
            SendRaw("< GET DEVICE_AUDIO_MUTE >");
        }

        public void SetFlashLights(bool enabled)
        {
            SendRaw(string.Format("< SET FLASH {0} >", enabled ? "ON" : "OFF"));
        }

        public void GetFlashLights()
        {
            SendRaw("< GET FLASH >");
        }

        public void SetMatrixMixerGain(int input, int output, double gainDb)
        {
            if (!IsMatrixInputValid(input) || !IsMatrixOutputValid(output))
                return;

            if (gainDb < -110 || gainDb > 30)
                return;

            var encoded = (int)Math.Round((gainDb + 110) * 10.0);
            SendRaw(string.Format("< SET {0} MATRIX_MXR_GAIN {1} {2:D4} >", ChannelCodeByNumber[input], ChannelCodeByNumber[output], encoded));
        }

        public void SetMatrixMixerGain(MatrixInput input, MatrixOutput output, double gainDb)
        {
            SetMatrixMixerGain((int)input, (int)output, gainDb);
        }

        public void GetMatrixMixerGain(int input, int output)
        {
            if (!IsMatrixInputValid(input) || !IsMatrixOutputValid(output))
                return;

            SendRaw(string.Format("< GET {0} MATRIX_MXR_GAIN {1} >", ChannelCodeByNumber[input], ChannelCodeByNumber[output]));
        }

        public void GetMatrixMixerGain(MatrixInput input, MatrixOutput output)
        {
            GetMatrixMixerGain((int)input, (int)output);
        }

        public void SetMatrixMixerRouting(int input, int output, bool enabled)
        {
            if (!IsMatrixInputValid(input) || !IsMatrixOutputValid(output))
                return;

            SendRaw(string.Format("< SET {0} MATRIX_MXR_ROUTE {1} {2} >", ChannelCodeByNumber[input], ChannelCodeByNumber[output], enabled ? "ON" : "OFF"));
        }

        public void SetMatrixMixerRouting(MatrixInput input, MatrixOutput output, bool enabled)
        {
            SetMatrixMixerRouting((int)input, (int)output, enabled);
        }

        public void GetMatrixMixerRouting(int input, int output)
        {
            if (!IsMatrixInputValid(input) || !IsMatrixOutputValid(output))
                return;

            SendRaw(string.Format("< GET {0} MATRIX_MXR_ROUTE {1} >", ChannelCodeByNumber[input], ChannelCodeByNumber[output]));
        }

        public void GetMatrixMixerRouting(MatrixInput input, MatrixOutput output)
        {
            GetMatrixMixerRouting((int)input, (int)output);
        }

        public void SetPreset(int number)
        {
            if (number < 1 || number > 10)
                return;

            SendRaw(string.Format("< SET PRESET {0:D2} >", number));
        }

        public void GetPreset()
        {
            SendRaw("< GET PRESET >");
        }

        public void Reboot()
        {
            SendRaw("< SET REBOOT >");
        }

        private void MatchAnalogInputGainSwitch(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = FromAnalogInputLevelProtocol(match[0].Groups[2].Value);
            if (value < 0)
                return;
            analogInputGainSwitch[channel] = value;
            RaiseDataEvent("AnalogInputGainSwitch");
        }

        private void MatchAnalogOutputGainSwitch(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = FromAnalogOutputLevelProtocol(match[0].Groups[2].Value);
            if (value < 0)
                return;
            analogOutputGainSwitch[channel] = value;
            RaiseDataEvent("AnalogOutputGainSwitch");
        }

        private void MatchAudioGain(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = (int.Parse(match[0].Groups[2].Value) / 10.0) - 110.0;
            audioGain[channel] = value;
            RaiseDataEvent("AudioGain");
        }

        private void MatchAudioMute(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = match[0].Groups[2].Value == "ON";
            audioMute[channel] = value;
            RaiseDataEvent("AudioMute");
        }

        private void MatchAutomixerGateStatus(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = match[0].Groups[2].Value == "ON";
            automixerGate[channel] = value;
            RaiseDataEvent("AutomixerGateStatus");
        }

        private void MatchAutomixerMode(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = match[0].Groups[2].Value;
            automixerMode[channel] = value;
            RaiseDataEvent("AutomixerMode");
        }

        private void MatchAutomixerOffAttenuation(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = int.Parse(match[0].Groups[2].Value) - 110.0;
            automixerOffAttenuation[channel] = value;
            RaiseDataEvent("AutomixerOffAttenuation");
        }

        private void MatchAutomixerPostGateMute(MatchCollection match)
        {
            var channel = int.Parse(match[0].Groups[1].Value);
            var value = match[0].Groups[2].Value == "ON";
            automixerPostGateMute[channel] = value;
            RaiseDataEvent("AutomixerPostGateMute");
        }

        private void MatchCallStatus(MatchCollection match)
        {
            callStatusOnHook = match[0].Groups[1].Value == "ON";
            RaiseDataEvent("CallStatus");
        }

        private void MatchCallStatusMode(MatchCollection match)
        {
            callStatusModeEnabled = match[0].Groups[1].Value == "ON";
            RaiseDataEvent("CallStatusMode");
        }

        private void MatchDeviceAudioMute(MatchCollection match)
        {
            deviceAudioMute = match[0].Groups[1].Value == "ON";
            RaiseDataEvent("DeviceAudioMute");
        }

        private void MatchFlashLights(MatchCollection match)
        {
            flashLights = match[0].Groups[1].Value == "ON";
            RaiseDataEvent("FlashLights");
        }

        private void MatchMatrixMixerGain(MatchCollection match)
        {
            var input = int.Parse(match[0].Groups[1].Value);
            var output = int.Parse(match[0].Groups[2].Value);
            var value = (int.Parse(match[0].Groups[3].Value) / 10.0) - 110.0;
            matrixMixerGain[BuildCrosspointKey(input, output)] = value;
            RaiseDataEvent("MatrixMixerGain");
        }

        private void MatchMatrixMixerRouting(MatchCollection match)
        {
            var input = int.Parse(match[0].Groups[1].Value);
            var output = int.Parse(match[0].Groups[2].Value);
            var enabled = match[0].Groups[3].Value == "ON";
            matrixMixerRouting[BuildCrosspointKey(input, output)] = enabled;
            RaiseDataEvent("MatrixMixerRouting");
        }

        private void MatchPreset(MatchCollection match)
        {
            preset = int.Parse(match[0].Groups[1].Value);
            RaiseDataEvent("Preset");
        }

        private void MatchError(MatchCollection match)
        {
            RaiseDataEvent("Error");
        }

        private static string BuildCrosspointKey(int input, int output)
        {
            return input + ":" + output;
        }

        private static bool IsChannelValid(int channel, params int[] allowed)
        {
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == channel)
                    return true;
            }

            return false;
        }

        private static bool IsMatrixInputValid(int channel)
        {
            return IsChannelValid(channel, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 21);
        }

        private static bool IsMatrixOutputValid(int channel)
        {
            return IsChannelValid(channel, 15, 16, 17, 18, 19, 20, 23, 24, 25, 26, 27, 28);
        }

        private static string NormalizeLevel(string value, params string[] allowed)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var upper = value.Trim().ToUpper();
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == upper)
                    return upper;
            }

            return null;
        }

        private static string ToProtocol(AnalogInputGainMode mode)
        {
            return mode == AnalogInputGainMode.AuxLevel ? "AUX" : "LINE";
        }

        private static string ToAnalogInputLevelProtocol(int level)
        {
            if (level == 0)
                return "LINE";

            if (level == 1)
                return "AUX";

            return null;
        }

        private static int FromAnalogInputLevelProtocol(string protocolValue)
        {
            if (protocolValue == "LINE")
                return 0;

            if (protocolValue == "AUX")
                return 1;

            return -1;
        }

        private static string ToProtocol(AnalogOutputGainMode mode)
        {
            if (mode == AnalogOutputGainMode.AuxLevel)
                return "AUX";

            if (mode == AnalogOutputGainMode.MicLevel)
                return "MIC";

            return "LINE";
        }

        private static string ToAnalogOutputLevelProtocol(int level)
        {
            if (level == 0)
                return "LINE";

            if (level == 1)
                return "AUX";

            if (level == 2)
                return "MIC";

            return null;
        }

        private static int FromAnalogOutputLevelProtocol(string protocolValue)
        {
            if (protocolValue == "LINE")
                return 0;

            if (protocolValue == "AUX")
                return 1;

            if (protocolValue == "MIC")
                return 2;

            return -1;
        }

        private static string ToProtocol(AutomixerMode mode)
        {
            if (mode == AutomixerMode.GainShare)
                return "GAINSHARE";

            if (mode == AutomixerMode.Gating)
                return "GATING";

            return "MANUAL";
        }
    }
}

