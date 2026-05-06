using MobiFlightWwFcu;

namespace MobiFlightWwFcuUnitTests.Mocks
{
    internal class MockWinwingMessageSender : IWinwingMessageSender
    {
        public List<DisplayCommandMessage> DisplayCommandsSent { get; } = new List<DisplayCommandMessage>();
        public List<LightControlMessage> LightControlCommands { get; } = new List<LightControlMessage>();
        public List<BrightnessMessage> BrightnessCommands { get; } = new List<BrightnessMessage>();
        public List<byte[]> CduDisplayBytes { get; } = new List<byte[]>();

        public void Reset()
        {
            DisplayCommandsSent.Clear();
            LightControlCommands.Clear();
            BrightnessCommands.Clear();
            CduDisplayBytes.Clear();
        }

        public bool IsConnected() => true;
        public void Connect() { }
        public void Shutdown() { }

        public void SendDisplayCommands(IList<byte[]> commands)
        {
            DisplayCommandsSent.Add(new DisplayCommandMessage
            {
                Commands = commands.Select(c => (byte[])c.Clone()).ToList()
            });

            // Console output captured by the TRX logger; supports the
            // "test-then-extract-expected-bytes" workflow described in CLAUDE.md.
            Console.WriteLine("SendDisplayCommands called with {0} command(s):", commands.Count);
            for (int i = 0; i < commands.Count; i++)
            {
                var bytes = commands[i];
                var hexValues = string.Join(", ", bytes.Select(b => string.Format("0x{0:X2}", b)));
                Console.WriteLine("  Command {0}: new byte[] {{ {1} }}", i, hexValues);
            }
            Console.WriteLine();
        }

        public void SendCduDisplayBytes(byte[] byteList)
        {
            CduDisplayBytes.Add((byte[])byteList.Clone());
        }

        public void SendLightControlMessage(byte[] destination, byte type, byte value)
        {
            LightControlCommands.Add(new LightControlMessage
            {
                Destination = (byte[])destination.Clone(),
                Type = type,
                Value = value
            });
        }

        public void SetBrightness(byte[] destinationAddress, byte type, string brightness)
        {
            BrightnessCommands.Add(new BrightnessMessage
            {
                DestinationAddress = (byte[])destinationAddress.Clone(),
                Type = type,
                Brightness = brightness
            });
        }

        public void SetBrightness(byte[] destinationAddress, byte type, int brightness)
        {
            BrightnessCommands.Add(new BrightnessMessage
            {
                DestinationAddress = (byte[])destinationAddress.Clone(),
                Type = type,
                Brightness = brightness.ToString()
            });
        }

        public void SetVibration(byte[] destinationAddress, byte type, byte level) { }
        public void SetPulseLight(byte[] destinationAddress, bool isOn) { }
        public void SendHeartBeatMessage() { }
        public void SendRequestFirmwareMessage() { }
    }

    internal class DisplayCommandMessage
    {
        public required List<byte[]> Commands { get; set; }
    }

    internal class LightControlMessage
    {
        public required byte[] Destination { get; set; }
        public byte Type { get; set; }
        public byte Value { get; set; }
    }

    internal class BrightnessMessage
    {
        public required byte[] DestinationAddress { get; set; }
        public byte Type { get; set; }
        public required string Brightness { get; set; }
    }
}
