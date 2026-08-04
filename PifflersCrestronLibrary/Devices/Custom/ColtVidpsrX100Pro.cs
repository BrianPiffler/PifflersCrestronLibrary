using System;
using System.Collections.Generic;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Custom
{
    public class ColtVidpsrX100Pro : BasicTCP
    {
        public enum InputLayer
        {
            Main,
            Pip1,
            Pip2
        }

        public enum InputCardModel
        {
            DviX4,
            HdmiX4,
            TwoInOne,
            ThreeGSdi,
            Hdmi20,
            DisplayPort12,
            VgaX4,
            VgaX2CvbsX2,
            CvbsX4,
            MixedInput
        }

        public enum InputInterface
        {
            Dvi,
            Hdmi14,
            Hdmi20,
            DisplayPort14,
            TwelveGSdi,
            ThreeGSdi,
            Vga,
            Cvbs
        }

        public enum ScreenState
        {
            Wakeup,
            Blackout
        }

        private static readonly byte[] ReservedBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        private ushort senderUnit = 1;

        public ushort SenderUnit
        {
            get { return senderUnit; }
        }

        public int Brightness { get; private set; }
        public int LastPreset { get; private set; }
        public ScreenState LastScreenState { get; private set; }

        public ColtVidpsrX100Pro(string host, string friendlyName, int port)
            : base(host, friendlyName, port)
        {
            LastScreenState = ScreenState.Blackout;
        }

        protected override void InitializeRegexDictionary()
        {
            // Protocol template is command-driven binary TX only; no documented RX parser patterns.
        }

        protected override void KeepAliveCallback(object _)
        {
            // Do not send BasicTCP text keepalive on this binary protocol.
        }

        public void SetSenderAll()
        {
            senderUnit = 0;
        }

        public bool SetSenderUnit(ushort unit)
        {
            if (unit < 1 || unit > 15)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid sender unit: " + unit + " (allowed 1..15)");
                return false;
            }

            senderUnit = unit;
            return true;
        }

        public bool SetBrightness(int value)
        {
            if (value < 0 || value > 100)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid brightness: " + value + " (allowed 0..100)");
                return false;
            }

            var brightnessValue = checked(value * 100);
            var payload = ToLittleEndianInt16(brightnessValue);
            SendRaw(BuildPacket(new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00 }, payload));
            Brightness = value;
            RaiseDataEvent("Brightness");
            return true;
        }

        public bool SetInput(InputLayer layer, int outputBoardSlotNumber, InputCardModel inputCardModel, InputInterface inputInterface, int interfaceNumber)
        {
            if (outputBoardSlotNumber < 1 || outputBoardSlotNumber > 32)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid output board slot number: " + outputBoardSlotNumber + " (allowed 1..32)");
                return false;
            }

            if (interfaceNumber < 1 || interfaceNumber > 10)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid interface number: " + interfaceNumber + " (allowed 1..10)");
                return false;
            }

            var payload = new List<byte>(7)
            {
                ToLayerByte(layer)
            };

            payload.AddRange(ToLittleEndianInt16(15 + outputBoardSlotNumber));
            payload.Add(ToInputCardModelByte(inputCardModel));
            payload.Add(0x00);
            payload.Add(ToInputInterfaceByte(inputInterface));
            payload.Add((byte)(interfaceNumber - 1));

            SendRaw(BuildPacket(new byte[] { 0x20, 0x10, 0x00, 0x18, 0x00, 0x00, 0x00 }, payload.ToArray()));
            RaiseDataEvent("Input");
            return true;
        }

        public bool RecallPreset(int preset)
        {
            if (preset < 1 || preset > 128)
            {
                Debug.Warn("[TCP] [" + friendlyName + "] invalid preset: " + preset + " (allowed 1..128)");
                return false;
            }

            var payload = ToLittleEndianInt16(preset - 1);
            SendRaw(BuildPacket(new byte[] { 0x07, 0x10, 0x03, 0x13, 0x00, 0x00, 0x00 }, payload));
            LastPreset = preset;
            RaiseDataEvent("PresetRecall");
            return true;
        }

        public void SetScreen(ScreenState state)
        {
            var payload = new byte[] { state == ScreenState.Wakeup ? (byte)0x01 : (byte)0x00 };
            SendRaw(BuildPacket(new byte[] { 0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00 }, payload));
            LastScreenState = state;
            RaiseDataEvent("Screen");
        }

        private byte[] BuildPacket(byte[] command, byte[] payload)
        {
            var senderBytes = GetSenderBytes();
            var frame = new byte[command.Length + senderBytes.Length + ReservedBytes.Length + payload.Length];

            Buffer.BlockCopy(command, 0, frame, 0, command.Length);
            Buffer.BlockCopy(senderBytes, 0, frame, command.Length, senderBytes.Length);
            Buffer.BlockCopy(ReservedBytes, 0, frame, command.Length + senderBytes.Length, ReservedBytes.Length);
            Buffer.BlockCopy(payload, 0, frame, command.Length + senderBytes.Length + ReservedBytes.Length, payload.Length);

            return frame;
        }

        private byte[] GetSenderBytes()
        {
            if (senderUnit == 0)
                return new byte[] { 0xFF, 0xFF };

            var value = senderUnit - 1;
            return new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
        }

        private static byte[] ToLittleEndianInt16(int value)
        {
            short int16Value = (short)value;
            var bytes = BitConverter.GetBytes(int16Value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private static byte ToLayerByte(InputLayer layer)
        {
            switch (layer)
            {
                case InputLayer.Main:
                    return 0x00;
                case InputLayer.Pip1:
                    return 0x01;
                case InputLayer.Pip2:
                    return 0x02;
                default:
                    return 0x00;
            }
        }

        private static byte ToInputCardModelByte(InputCardModel model)
        {
            switch (model)
            {
                case InputCardModel.DviX4:
                    return 0x10;
                case InputCardModel.HdmiX4:
                    return 0x11;
                case InputCardModel.TwoInOne:
                    return 0x12;
                case InputCardModel.ThreeGSdi:
                    return 0x16;
                case InputCardModel.Hdmi20:
                    return 0x1E;
                case InputCardModel.DisplayPort12:
                    return 0x1F;
                case InputCardModel.VgaX4:
                    return 0x20;
                case InputCardModel.VgaX2CvbsX2:
                    return 0x21;
                case InputCardModel.CvbsX4:
                    return 0x22;
                case InputCardModel.MixedInput:
                    return 0x18;
                default:
                    return 0x10;
            }
        }

        private static byte ToInputInterfaceByte(InputInterface inputInterface)
        {
            switch (inputInterface)
            {
                case InputInterface.Dvi:
                    return 0x10;
                case InputInterface.Hdmi14:
                    return 0x11;
                case InputInterface.Hdmi20:
                    return 0x20;
                case InputInterface.DisplayPort14:
                    return 0x21;
                case InputInterface.TwelveGSdi:
                    return 0x22;
                case InputInterface.ThreeGSdi:
                    return 0x12;
                case InputInterface.Vga:
                    return 0x13;
                case InputInterface.Cvbs:
                    return 0x14;
                default:
                    return 0x11;
            }
        }
    }
}



