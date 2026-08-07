using System;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.ArtNet
{
    /// <summary>
    /// Generischer Art-Net-Sender (Herstellerneutral).
    /// Sendet ArtDMX (<c>OpOutput</c> = 0x5000) an ein Ziel-Node/-Gerät auf UDP 6454.
    /// Ein Universum je Instanz; DMX-Werte werden lokal gepuffert und mit
    /// <see cref="Send"/> uebertragen (oder <see cref="SetChannel"/>/<see cref="SetRgb"/>
    /// nach Belieben mit anschliessendem <see cref="Send"/>).
    ///
    /// Protokoll-Referenz: Art-Net 4 Spec, Section "ArtDmx".
    /// - Header:  'A','r','t','-','N','e','t',0x00
    /// - OpCode:  0x5000 (little-endian im Paket)
    /// - ProtVer: 14 (0x000E, big-endian)
    /// - Sequence, Physical, SubUni, Net, Length (big-endian), dann 1..512 DMX-Bytes.
    /// </summary>
    public class ArtNetSender : BasicUDP
    {
        public const int DefaultPort = 6454;

        private static readonly byte[] Header = { (byte)'A', (byte)'r', (byte)'t', (byte)'-', (byte)'N', (byte)'e', (byte)'t', 0x00 };
        private const ushort OpOutput = 0x5000;
        private const ushort ProtVer = 14;

        private readonly byte[] channels = new byte[512];
        private readonly byte subUni;   // untere 4 Bit = Universum, obere 4 Bit = SubNet
        private readonly byte net;      // 0..127
        private byte sequence;          // 0 = deaktiviert; sonst 1..255, wrap

        /// <param name="host">Ziel-IP des Art-Net-Nodes/Geraetes.</param>
        /// <param name="friendlyName">Anzeigename fuer Logging.</param>
        /// <param name="universe">Kombiniertes 15-Bit-Universum (Net|SubNet|Uni). Meist 0.</param>
        public ArtNetSender(string host, string friendlyName, int universe = 0)
            : base(host, friendlyName, DefaultPort)
        {
            var u = (ushort)(universe & 0x7FFF);
            this.subUni = (byte)(u & 0xFF);
            this.net = (byte)((u >> 8) & 0x7F);
        }

        /// <summary>Setzt einen einzelnen DMX-Kanal (1-basiert, 1..512).</summary>
        public void SetChannel(int channel, byte value)
        {
            if (channel < 1 || channel > 512)
            {
                Debug.Error("[ArtNet] [" + friendlyName + "] SetChannel out of range: " + channel);
                return;
            }
            channels[channel - 1] = value;
        }

        /// <summary>Setzt N aufeinanderfolgende Kanaele ab startChannel (1-basiert).</summary>
        public void SetChannels(int startChannel, byte[] values)
        {
            if (values == null || values.Length == 0) return;
            if (startChannel < 1 || startChannel + values.Length - 1 > 512)
            {
                Debug.Error("[ArtNet] [" + friendlyName + "] SetChannels out of range: " + startChannel + "+" + values.Length);
                return;
            }
            Buffer.BlockCopy(values, 0, channels, startChannel - 1, values.Length);
        }

        /// <summary>Setzt RGB-Fixture ab startChannel (Reihenfolge R, G, B).</summary>
        public void SetRgb(int startChannel, byte r, byte g, byte b)
        {
            SetChannels(startChannel, new[] { r, g, b });
        }

        /// <summary>Setzt RGBW-Fixture ab startChannel (Reihenfolge R, G, B, W).</summary>
        public void SetRgbw(int startChannel, byte r, byte g, byte b, byte w)
        {
            SetChannels(startChannel, new[] { r, g, b, w });
        }

        /// <summary>Setzt alle 512 Kanaele auf 0 (im Puffer). Sendet nicht.</summary>
        public void Blackout()
        {
            Array.Clear(channels, 0, channels.Length);
        }

        /// <summary>Baut ArtDMX-Paket und sendet es. Standardmaessig genau <paramref name="length"/> Kanaele (2..512, gerade Zahl empfohlen). Default 512.</summary>
        public bool Send(int length = 512)
        {
            if (length < 2) length = 2;
            if (length > 512) length = 512;
            // Art-Net erwartet gerade Byte-Anzahl fuer Length
            if ((length & 1) == 1) length++;

            var packet = new byte[18 + length];
            Buffer.BlockCopy(Header, 0, packet, 0, Header.Length);   // 0..7  Header
            packet[8]  = (byte)(OpOutput & 0xFF);                    // 8..9  OpCode (LE)
            packet[9]  = (byte)((OpOutput >> 8) & 0xFF);
            packet[10] = (byte)((ProtVer >> 8) & 0xFF);              // 10..11 ProtVer (BE)
            packet[11] = (byte)(ProtVer & 0xFF);
            packet[12] = sequence;                                    // Sequence (0=disabled)
            packet[13] = 0;                                           // Physical
            packet[14] = subUni;                                      // SubUni (Uni|SubNet)
            packet[15] = net;                                         // Net
            packet[16] = (byte)((length >> 8) & 0xFF);                // Length (BE)
            packet[17] = (byte)(length & 0xFF);
            Buffer.BlockCopy(channels, 0, packet, 18, length);        // DMX-Daten

            // Sequence weiterzaehlen; 0 = deaktiviert, ueberspringen.
            unchecked { sequence++; if (sequence == 0) sequence = 1; }

            return base.Send(packet);
        }
    }
}
