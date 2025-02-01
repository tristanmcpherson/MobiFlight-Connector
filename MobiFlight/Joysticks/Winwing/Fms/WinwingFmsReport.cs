using System;

namespace MobiFlight.Joysticks.Winwing.Fms
{
    internal class WinwingFmsReport
    {
        public uint ReportId { get; set; }
        public byte[] ButtonBytes { get; } = new byte[9]; // Stores bytes 0-11


        private const uint BUTTONS_REPORT = 1;

        public void CopyTo(WinwingFmsReport targetReport)
        {
            targetReport.ReportId = this.ReportId;
            Buffer.BlockCopy(this.ButtonBytes, 0, targetReport.ButtonBytes, 0, 9);
        }

        public bool IsPressed(WinwingFmsHardwareButton button)
        {
            // Decode byte and bit from enum value
            var byteIndex = (int)button >> 8;
            var bitPosition = (int)button & 0xFF;

            // Safety check for array bounds
            if (byteIndex >= ButtonBytes.Length) return false;

            return (ButtonBytes[byteIndex] & (1 << bitPosition)) != 0;
        }

        public void ParseReport(HidBuffer hidBuffer)
        {
            byte[] data = hidBuffer.HidReport.TransferResult.Data;
            ReportId = hidBuffer.HidReport.ReportId;
            if (ReportId == BUTTONS_REPORT)
            {
                Buffer.BlockCopy(data, 0, ButtonBytes, 0, 9);
            }
        }
    }
}
