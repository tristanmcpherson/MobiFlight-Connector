using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MobiFlightWwFcu
{
    internal class WinwingTcasDevice : IWinwingDevice
    {
        public string Name { get; } = "WinWing TCAS";

        private IWinwingMessageSender MessageSender = null;
        private byte[] DestinationAddress = WinwingConstants.DEST_TCAS;

        private Dictionary<string, Action<string>> DisplayNameToActionMapping = new Dictionary<string, Action<string>>();
        private Dictionary<string, Action<byte>> OutputNameToActionMapping = new Dictionary<string, Action<byte>>();

        private const string BACK_BRIGHTNESS = "Backlight Percentage";
        private const string LED_BRIGHTNESS = "LED Percentage";
        private const string LCD_BRIGHTNESS = "LCD Percentage";

        private const string IDENT_NUMBER   = "Ident Value";
        private const string ANN_LIGHT = "LCD Test On/Off";
        private const string NUMBER_DIGITS = "Number Of Digits";

        private Dictionary<string, DisplaySegment> DisplayTestCommands = new Dictionary<string, DisplaySegment>()
        {
            { "AllOn",  new DisplaySegment(new Bit[] { new Bit(0, 0, true), new Bit(0, 1), new Bit(0, 2), new Bit(0, 3) }, false) },
            { "AllOff", new DisplaySegment(new Bit[] { new Bit(0, 0), new Bit(0, 1, true), new Bit(0, 2), new Bit(0, 3) }, false) },
        };

        // 7-segment digit packed within a single TCAS "ident" byte (Bits 7..1).
        // Bit-order in the constructor must match the segment order [T, TR, BR, B, BL, TL, M].
        private static DisplaySegment IdentDigit(int bit, char initChar)
        {
            var seg = new DisplaySegment(new Bit[] {
                new Bit(16, bit), // T
                new Bit(12, bit), // TR
                new Bit(8,  bit), // BR
                new Bit(4,  bit), // B
                new Bit(28, bit), // BL
                new Bit(24, bit), // TL
                new Bit(20, bit), // M
            }, true);
            seg.SetCharacter(initChar);
            return seg;
        }

        // Element top byte is byte number in data section. So 0 is start of data section. Header with 17 bytes is not included.
        private Dictionary<string, DisplaySegment> DisplaySetValueSegments = new Dictionary<string, DisplaySegment>()
        {
            { "IdentThousands", IdentDigit(0, '{') },
            { "IdentHundreds", IdentDigit(1, '}') },
            { "IdentTens", IdentDigit(2, 'o') },
            { "IdentOnes", IdentDigit(3, 'b') },
        };

        private Dictionary<string, byte> LedIdentifiers = new Dictionary<string, byte>()
        {
            { "ATC_FAIL", 0x03 },
        };

        private Dictionary<string, string> LcdCurrentValuesCache = new Dictionary<string, string>();
        private Dictionary<string, byte> LedCurrentValuesCache = new Dictionary<string, byte>();

        private byte[] DisplayTestCommand = new byte[0x12];
        private byte[] RefreshCommand = new byte[0x11];
        private byte[] SetValuesCommand = new byte[0x35];  // 53 bytes: 4 dest addr + 13 header + 36 data

        private int NumberOfCharsShown = 4;

        public WinwingTcasDevice(IWinwingMessageSender sender)
        {
            MessageSender = sender;

            // Add display options
            DisplayNameToActionMapping.Add(NUMBER_DIGITS, SetNumberOfCharsShown);
            DisplayNameToActionMapping.Add(IDENT_NUMBER, SetIdentNumber);            
            DisplayNameToActionMapping.Add(ANN_LIGHT, SetAnnunciatorLightOnOff);
            
            // Add output options
            OutputNameToActionMapping.Add(BACK_BRIGHTNESS, SetBacklightBrightness);
            OutputNameToActionMapping.Add(LED_BRIGHTNESS, SetLedBrightness);
            OutputNameToActionMapping.Add(LCD_BRIGHTNESS, SetLcdBrightness);

            foreach (var displayName in GetDisplayNames())
            {
                LcdCurrentValuesCache.Add(displayName, string.Empty);
            }

            foreach (var ledName in GetLedNames())
            {
                LedCurrentValuesCache.Add(ledName, 255);
            }

            PrepareCommands();
        }

        private void PrepareCommands()
        {
            var initDisplayTest = new List<byte>(DestinationAddress);
            initDisplayTest.AddRange(new byte[2]);
            initDisplayTest.AddRange(WinwingConstants.DisplayCmdHeaders["0401"]);
            initDisplayTest.CopyTo(DisplayTestCommand, 0);

            var initSetValues = new List<byte>(DestinationAddress);
            initSetValues.AddRange(new byte[2]);
            initSetValues.AddRange(WinwingConstants.DisplayCmdHeaders["0201_TCAS"]);
            initSetValues.CopyTo(SetValuesCommand, 0);

            var initRefresh = new List<byte>(DestinationAddress);
            initRefresh.AddRange(new byte[2]);
            initRefresh.AddRange(WinwingConstants.DisplayCmdHeaders["0301"]);
            initRefresh.CopyTo(RefreshCommand, 0);

            foreach (var segment in DisplaySetValueSegments.Values)
            {
                SetSegmentDisplayCommand(segment, SetValuesCommand);
            }
        }

        public void Connect()
        {
            SendDisplayCommand(SetValuesCommand);
            SetBacklightBrightness(50);
            SetLcdBrightness(100);

            // Testing
            // SetDisplay(IDENT_NUMBER, "11");
            // SetDisplay(IDENT_NUMBER, "7620");
            // SetDisplay(IDENT_NUMBER, "123456789");
            // SetDisplay(CHR_SEC, "22");
            // SetDisplay(ANN_LIGHT, "1");
            // SetLed("ATC_FAIL", 0);
        }

        private void TurnOffAllLEDs()
        {
            foreach (var ledName in LedIdentifiers.Keys)
            {
                SetLed(ledName, 0);
            }
        }

        public void Shutdown()
        {
            EmptyDisplay();
            SetBacklightBrightness(0);
            SetLcdBrightness(0);
            TurnOffAllLEDs();
        }

        public List<string> GetLedNames()
        {
            List<string> ledNames = new List<string>();
            ledNames.AddRange(LedIdentifiers.Keys.ToList());
            ledNames.AddRange(OutputNameToActionMapping.Keys.ToList());
            return ledNames;
        }

        public List<string> GetDisplayNames()
        {
            return DisplayNameToActionMapping.Keys.ToList();
        }

        public List<string> GetInternalDisplayNames()
        {
            return new List<string>();
        }

        public void SetLed(string led, byte state)
        {
            if (!string.IsNullOrEmpty(led) && LedCurrentValuesCache[led] != state)
            {
                if (LedIdentifiers.TryGetValue(led, out byte ledType))
                {
                    LedCurrentValuesCache[led] = state;
                    byte stateAdjusted = state == 0 ? (byte)0 : (byte)1;
                    MessageSender.SendLightControlMessage(DestinationAddress, ledType, stateAdjusted);
                }
                else if (OutputNameToActionMapping.TryGetValue(led, out Action<byte> action))
                {
                    action(state);
                }
            }
        }

        public void SetDisplay(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && LcdCurrentValuesCache[name] != value)
            {
                LcdCurrentValuesCache[name] = value;
                DisplayNameToActionMapping[name](value);
            }
        }

        private void SetAnnunciatorLightOnOff(string annLight)
        {
            int myAnnLight = (int)Convert.ToDouble(annLight, CultureInfo.InvariantCulture);
            if (myAnnLight == 1)
            {
                LcdTest("AllOn");
            }
            else
            {
                SendDisplayCommand(SetValuesCommand);
            }
        }

        private void SetIdentNumber(string number)
        {
            int value = (int)Convert.ToDouble(number, CultureInfo.InvariantCulture);
            string myNumber = value.ToString(CultureInfo.InvariantCulture);

            string shortened = (myNumber.Length > NumberOfCharsShown ? myNumber.Substring(0, NumberOfCharsShown) : myNumber);
            char[] chars = shortened.PadLeft(NumberOfCharsShown, '0').PadRight(4, '*').ToCharArray();

            string[] segmentNames = new string[] { "IdentThousands", "IdentHundreds", "IdentTens", "IdentOnes" };
            for (int i = 0; i < chars.Length; i++)
            {
                var segment = DisplaySetValueSegments[segmentNames[i]];
                segment.SetCharacter(chars[i]);
                SetSegmentDisplayCommand(segment, SetValuesCommand);
            }
            SendDisplayCommand(SetValuesCommand);
        }

        private void SetNumberOfCharsShown(string number)
        {
            int value = (int)Convert.ToDouble(number, CultureInfo.InvariantCulture);
            int clampedValue = Math.Min(4, Math.Max(0, value));
            NumberOfCharsShown = clampedValue;
            LcdCurrentValuesCache[IDENT_NUMBER] = string.Empty; // Reset
        }

        private void EmptyDisplay()
        {
            LcdTest("AllOff");
        }

        private void LcdTest(string command)
        {
            PrepareAndSendDisplayTestCommand(DisplayTestCommands[command]);
        }

        private void PrepareAndSendDisplayTestCommand(DisplaySegment segment)
        {
            SetSegmentDisplayCommand(segment, DisplayTestCommand);
            SendDisplayCommand(DisplayTestCommand);
        }

        private void SetBacklightBrightness(byte brightness)
        {
            MessageSender.SetBrightness(DestinationAddress, 0x00, brightness);
        }

        private void SetLedBrightness(byte brightness)
        {
            MessageSender.SetBrightness(DestinationAddress, 0x02, brightness);
        }

        private void SetLcdBrightness(byte brightness)
        {
            MessageSender.SetBrightness(DestinationAddress, 0x01, brightness);
        }

        private void SendDisplayCommand(byte[] message)
        {
            MessageSender.SendDisplayCommands(new byte[][] { message, RefreshCommand });
        }

        private void SetSegmentDisplayCommand(DisplaySegment e, byte[] mes)
        {
            foreach (Bit b in e.Bits)
            {
                int index = b.ByteNumber + 17; // with header
                mes[index] = b.Value ? (byte)(mes[index] | (1 << b.BitPosition))
                                     : (byte)(mes[index] & ~(1 << b.BitPosition));
            }
        }

        public void Stop()
        {
            TurnOffAllLEDs();
        }
    }
}
