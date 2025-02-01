using Device.Net;
using Hid.Net;
using Hid.Net.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MobiFlight.Joysticks.Winwing.Fms
{
    internal class HidBuffer
    {
        public Report HidReport { get; set; }
    }

    internal class WinwingFms : Joystick
    {
        private readonly int VendorId = 0x4098;
        // 0xBB35 = PFP-3N-CAPTAIN
        // 0xBB39 = PFP-3N-OBSERVER
        // 0xBB3D = PFP-3N-CO-PILOT
        private int ProductId = 0xBB35;
        IHidDevice Device { get; set; }

        private const uint BUTTONS_REPORT = 1;

        private Dictionary<WinwingFmsHardwareButton, JoystickDevice> ButtonMap = new Dictionary<WinwingFmsHardwareButton, JoystickDevice>();
        private JoystickDefinition Definition;
        private volatile bool DoInitialize = true;
        private volatile bool DoReadHidReports = false;
        private WinwingFmsReport CurrentReport = new WinwingFmsReport();
        private WinwingFmsReport PreviousReport = new WinwingFmsReport();
        private HidBuffer HidDataBuffer = new HidBuffer();
        private WinwingFmsHardwareButton[] HardwareButtons = Enum.GetValues(typeof(WinwingFmsHardwareButton)).Cast<WinwingFmsHardwareButton>().ToArray();

        public WinwingFms(SharpDX.DirectInput.Joystick joystick, JoystickDefinition def, int productId) : base(joystick, def)
        {
            Definition = def;
            ProductId = productId;
        }


        public async override void Connect(IntPtr handle)
        {
            base.Connect(handle);

            var hidFactory = new FilterDeviceDefinition(vendorId: (uint)VendorId, productId: (uint)ProductId).CreateWindowsHidDeviceFactory();
            var deviceDefinitions = (await hidFactory.GetConnectedDeviceDefinitionsAsync().ConfigureAwait(false)).ToList();
            Device = (IHidDevice)await hidFactory.GetDeviceAsync(deviceDefinitions.First()).ConfigureAwait(false);
            await Device.InitializeAsync().ConfigureAwait(false);
            DoReadHidReports = true;

            await Task.Run(async () =>
            {
                while (DoReadHidReports)
                {
                    try
                    {
                        HidDataBuffer.HidReport = await Device.ReadReportAsync().ConfigureAwait(false);
                        InputReportReceived(HidDataBuffer);
                    }
                    catch
                    {
                        // Exception when disconnecting fms while mobiflight is running.
                        Shutdown();
                    }
                }
            });
        }
        protected override void EnumerateDevices()
        {
            foreach (var input in Definition.Inputs)
            {
                var button = HardwareButtons[input.Id - 1];
                var device = new JoystickDevice()
                {
                    Name = input.Name,
                    Label = input.Label,
                    Type = DeviceType.Button,
                    JoystickDeviceType = JoystickDeviceType.Button
                };

                Buttons.Add(device);
                ButtonMap[button] = device;

                Log.Instance.log($"Mapped WINWING FMS Button: {button} to {input.Label}", LogSeverity.Debug);
            }
        }

        private void InputReportReceived(HidBuffer hidBuffer)
        {
            CurrentReport.ParseReport(hidBuffer);

            if (CurrentReport.ReportId == BUTTONS_REPORT)
            {
                if (DoInitialize)
                {
                    CurrentReport.CopyTo(PreviousReport);
                    DoInitialize = false;
                }

                DetectButtonTransitions();
                CurrentReport.CopyTo(PreviousReport);
            }
        }

        private void DetectButtonTransitions()
        {
            foreach (var button in ButtonMap.Keys)
            {
                var current = CurrentReport.IsPressed(button);
                var previous = PreviousReport.IsPressed(button);

                if (current && !previous)
                {
                    TriggerButtonPress(ButtonMap[button], MobiFlightButton.InputEvent.PRESS);
                }
                else if (!current && previous)
                {
                    TriggerButtonPress(ButtonMap[button], MobiFlightButton.InputEvent.RELEASE);
                }
            }
        }

        protected void TriggerButtonPress(JoystickDevice device, MobiFlightButton.InputEvent inputEvent)
        {
            TriggerButtonPressed(this, new InputEventArgs()
            {
                Name = Name,
                DeviceId = device.Name,
                DeviceLabel = device.Label,
                Serial = SerialPrefix + DIJoystick.Information.InstanceGuid.ToString(),
                Type = DeviceType.Button,
                Value = (int)inputEvent
            });
        }

        public override void Retrigger()
        {
            DoInitialize = true;
        }

        public override IEnumerable<DeviceType> GetConnectedOutputDeviceTypes()
        {
            return new List<DeviceType>() { DeviceType.Output };
        }

        public override void Update()
        {
            // do nothing, update is event based not polled
        }

        public override void UpdateOutputDeviceStates()
        {
            // do nothing, update is event based not polled
        }

        protected override void SendData(byte[] data)
        {
            // do nothing, data is directly sent in SetOutputDeviceState
        }

        public override void Shutdown()
        {
            DoReadHidReports = false;
            Device?.Close();
            Device = null;
        }
    }

    public enum WinwingFmsHardwareButton
    {
        LineSelectKey1L = 0x0000,
        LineSelectKey2L = 0x0001,
        LineSelectKey3L = 0x0002,
        LineSelectKey4L = 0x0003,
        LineSelectKey5L = 0x0004,
        LineSelectKey6L = 0x0005,
        LineSelectKey1R = 0x0006,
        LineSelectKey2R = 0x0007,
        LineSelectKey3R = 0x0100,
        LineSelectKey4R = 0x0101,
        LineSelectKey5R = 0x0102,
        LineSelectKey6R = 0x0103,
        InitRef = 0x0104,
        RTE = 0x0105,
        CLB = 0x0106,
        CRZ = 0x0107,
        DES = 0x0200,
        MENU = 0x0203,
        LEGS = 0x0204,
        DEPARR = 0x0205,
        HOLD = 0x0206,
        PROG = 0x0207,
        BRTMinus = 0x0201,
        BRTPlus = 0x0202,
        EXEC = 0x0300,
        N1LIMIT = 0x0301,
        FIX = 0x0302,
        PREVPAGE = 0x0303,
        NEXTPAGE = 0x0304,
        Num0 = 0x0407,
        Num1 = 0x0305,
        Num2 = 0x0306,
        Num3 = 0x0307,
        Num4 = 0x0400,
        Num5 = 0x0401,
        Num6 = 0x0402,
        Num7 = 0x0403,
        Num8 = 0x0404,
        Num9 = 0x0405,
        Dot = 0x0406,
        PlusMin = 0x0500,
        A = 0x0501,
        B = 0x0502,
        C = 0x0503,
        D = 0x0504,
        E = 0x0505,
        F = 0x0506,
        G = 0x0507,
        H = 0x0600,
        I = 0x0601,
        J = 0x0602,
        K = 0x0603,
        L = 0x0604,
        M = 0x0605,
        N = 0x0606,
        O = 0x0607,
        P = 0x0700,
        Q = 0x0701,
        R = 0x0702,
        S = 0x0703,
        T = 0x0704,
        U = 0x0705,
        V = 0x0706,
        W = 0x0707,
        X = 0x0800,
        Y = 0x0801,
        Z = 0x0802,
        SP = 0x0803,
        DEL = 0x0804,
        Slash = 0x0805,
        CLR = 0x0806
    }

}
