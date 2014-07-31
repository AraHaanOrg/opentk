﻿#region License
//
// WinRawJoystick.cs
//
// Author:
//       Stefanos A. <stapostol@gmail.com>
//
// Copyright (c) 2014 Stefanos Apostolopoulos
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Input;
using OpenTK.Platform.Common;

namespace OpenTK.Platform.Windows
{
    class WinRawJoystick : IJoystickDriver2
    {
        class Device
        {
            public IntPtr Handle;
            JoystickCapabilities Capabilities;
            JoystickState State;
            Guid Guid;

            internal readonly List<HidProtocolValueCaps> AxisCaps =
                new List<HidProtocolValueCaps>();
            internal readonly List<HidProtocolButtonCaps> ButtonCaps =
                new List<HidProtocolButtonCaps>();

            readonly Dictionary<int, JoystickAxis> axes =
                new Dictionary<int,JoystickAxis>();
            readonly Dictionary<int, JoystickButton> buttons =
                new Dictionary<int, JoystickButton>();
            readonly Dictionary<int, JoystickHat> hats =
                new Dictionary<int, JoystickHat>();

            #region Constructors

            public Device(IntPtr handle, Guid guid)
            {
                Handle = handle;
                Guid = guid;
            }

            #endregion

            #region Public Members

            public void ClearButtons()
            {
                State.ClearButtons();
            }

            public void SetAxis(HIDPage page, short usage, short value)
            {
                JoystickAxis axis = GetAxis(page, usage);
                State.SetAxis(axis, value);
            }

            public void SetButton(HIDPage page, short usage, bool value)
            {
                JoystickButton button = GetButton(page, usage);
                State.SetButton(button, value);
            }

            public void SetHat(HIDPage page, short usage, HatPosition pos)
            {
                JoystickHat hat = GetHat(page, usage);
                State.SetHat(hat, new JoystickHatState(pos));
            }

            public void SetConnected(bool value)
            {
                Capabilities.SetIsConnected(value);
                State.SetIsConnected(value);
            }

            public JoystickCapabilities GetCapabilities()
            {
                Capabilities = new JoystickCapabilities(
                    axes.Count, buttons.Count, hats.Count,
                    Capabilities.IsConnected);
                return Capabilities;
            }

            internal void SetCapabilities(JoystickCapabilities caps)
            {
                Capabilities = caps;
            }

            public Guid GetGuid()
            {
                return Guid;
            }

            public JoystickState GetState()
            {
                return State;
            }

            #endregion

            #region Private Members

            static int MakeKey(HIDPage page, short usage)
            {
                return ((ushort)page << 16) | unchecked((ushort)usage);
            }

            JoystickAxis GetAxis(HIDPage page, short usage)
            {
                int key = MakeKey(page, usage);
                if (!axes.ContainsKey(key))
                {
                    JoystickAxis axis = HidHelper.TranslateJoystickAxis(page, usage);
                    axes.Add(key, axis);
                }
                return axes[key];
            }

            JoystickButton GetButton(HIDPage page, short usage)
            {
                int key = MakeKey(page, usage);
                if (!buttons.ContainsKey(key))
                {
                    buttons.Add(key, JoystickButton.Button0 + buttons.Count);
                }
                return buttons[key];
            }

            JoystickHat GetHat(HIDPage page, short usage)
            {
                int key = MakeKey(page, usage);
                if (!hats.ContainsKey(key))
                {
                    hats.Add(key, JoystickHat.Hat0 + hats.Count);
                }
                return hats[key];
            }

            #endregion
        }

        // Defines which types of HID devices we are interested in
        readonly RawInputDevice[] DeviceTypes;

        readonly IntPtr Window;
        readonly object UpdateLock = new object();
        readonly DeviceCollection<Device> Devices = new DeviceCollection<Device>();

        byte[] HIDData = new byte[1024];
        byte[] PreparsedData = new byte[1024];
        HidProtocolData[] DataBuffer = new HidProtocolData[16];

        public WinRawJoystick(IntPtr window)
        {
            Debug.WriteLine("Using WinRawJoystick.");
            Debug.Indent();

            if (window == IntPtr.Zero)
                throw new ArgumentNullException("window");

            Window = window;
            DeviceTypes = new RawInputDevice[]
            {
                new RawInputDevice(HIDUsageGD.Joystick, RawInputDeviceFlags.DEVNOTIFY | RawInputDeviceFlags.INPUTSINK, window),
                new RawInputDevice(HIDUsageGD.GamePad, RawInputDeviceFlags.DEVNOTIFY | RawInputDeviceFlags.INPUTSINK, window),
            };

            if (!Functions.RegisterRawInputDevices(DeviceTypes, DeviceTypes.Length, API.RawInputDeviceSize))
            {
                Debug.Print("[Warning] Raw input registration failed with error: {0}.",
                    Marshal.GetLastWin32Error());
            }
            else
            {
                Debug.Print("[WinRawJoystick] Registered for raw input");
            }

            RefreshDevices();

            Debug.Unindent();
        }

        #region Public Members

        public void RefreshDevices()
        {
            // Mark all devices as disconnected. We will check which of those
            // are connected below.
            foreach (var device in Devices)
            {
                device.SetConnected(false);
            }

            // Discover joystick devices
            foreach (RawInputDeviceList dev in WinRawInput.GetDeviceList())
            {
                // Skip non-joystick devices
                if (dev.Type != RawInputDeviceType.HID)
                    continue;

                // We use the device handle as the hardware id.
                // This works, but the handle will change whenever the
                // device is unplugged/replugged. We compensate for this
                // by checking device GUIDs, below.
                // Note: we cannot use the GUID as the hardware id,
                // because it is costly to query (and we need to query
                // that every time we process a device event.)
                IntPtr handle = dev.Device;
                Guid guid = GetDeviceGuid(handle);
                long hardware_id = handle.ToInt64();

                Device device = Devices.FromHardwareId(hardware_id);
                if (device != null)
                {
                    // We have already opened this device, mark it as connected
                    device.SetConnected(true);
                }
                else
                {
                    device = new Device(handle, guid);

                    // This is a new device, query its capabilities and add it
                    // to the device list
                    QueryDeviceCaps(device);

                    device.SetConnected(true);

                    // Check if a disconnected device with identical GUID already exists.
                    // If so, replace that device with this instance.
                    Device match = null;
                    foreach (Device candidate in Devices)
                    {
                        if (candidate.GetGuid() == guid && !candidate.GetCapabilities().IsConnected)
                        {
                            match = candidate;
                        }
                    }
                    if (match != null)
                    {
                        Devices.Remove(match.Handle.ToInt64());
                    }

                    Devices.Add(hardware_id, device);

                    Debug.Print("[{0}] Connected joystick {1} ({2})",
                        GetType().Name, device.GetGuid(), device.GetCapabilities());
                }
            }
        }

        public unsafe bool ProcessEvent(IntPtr raw)
        {
            // Query the size of the raw HID data buffer
            int size = 0;
            Functions.GetRawInputData(raw, GetRawInputDataEnum.INPUT, IntPtr.Zero, ref size, RawInputHeader.SizeInBytes);
            if (size > HIDData.Length)
            {
                Array.Resize(ref HIDData, size);
            }

            // Retrieve the raw HID data buffer
            if (Functions.GetRawInputData(raw, HIDData) > 0)
            {
                fixed (byte* pdata = HIDData)
                {
                    RawInput* rin = (RawInput*)pdata;

                    IntPtr handle = rin->Header.Device;
                    Device stick = GetDevice(handle);
                    if (stick == null)
                    {
                        Debug.Print("[WinRawJoystick] Unknown device {0}", handle);
                        return false;
                    }

                    if (!GetPreparsedData(handle, ref PreparsedData))
                    {
                        return false;
                    }

                    // Query current state
                    // Allocate enough storage to hold the data of the current report
                    int report_count = HidProtocol.MaxDataListLength(HidProtocolReportType.Input, PreparsedData);
                    if (report_count == 0)
                    {
                        Debug.Print("[WinRawJoystick] HidProtocol.MaxDataListLength() failed with {0}",
                            Marshal.GetLastWin32Error());
                        return false;
                    }

                    // Fill the data buffer
                    if (DataBuffer.Length < report_count)
                    {
                        Array.Resize(ref DataBuffer, report_count);
                    }

                    UpdateAxes(rin, stick);
                    UpdateButtons(rin, stick);
                    return true;
                }
            }

            return false;
        }

        HatPosition GetHatPosition(uint value, HidProtocolValueCaps caps)
        {
            if (caps.LogicalMax == 8)
                return (HatPosition)value;
            else
                return HatPosition.Centered;
        }

        unsafe void UpdateAxes(RawInput* rin, Device stick)
        {
            for (int i = 0; i < stick.AxisCaps.Count; i++)
            {
                if (stick.AxisCaps[i].IsRange)
                {
                    Debug.Print("[{0}] Axis range collections not implemented. Please report your controller type at http://www.opentk.com",
                        GetType().Name);
                    continue;
                }

                HIDPage page = stick.AxisCaps[i].UsagePage;
                short usage = stick.AxisCaps[i].NotRange.Usage;
                uint value = 0;

                HidProtocolStatus status = HidProtocol.GetUsageValue(
                    HidProtocolReportType.Input,
                    page, 0, usage, ref value,
                    PreparsedData,
                    new IntPtr((void*)&rin->Data.HID.RawData),
                    rin->Data.HID.Size);

                if (status != HidProtocolStatus.Success)
                {
                    Debug.Print("[{0}] HidProtocol.GetScaledUsageValue() failed. Error: {1}",
                        GetType().Name, status);
                    continue;
                }

                if (page == HIDPage.GenericDesktop && (HIDUsageGD)usage == HIDUsageGD.Hatswitch)
                {
                    stick.SetHat(page, usage, GetHatPosition(value, stick.AxisCaps[i]));
                }
                else
                {
                    short scaled_value = (short)HidHelper.ScaleValue(
                        (int)((long)value + stick.AxisCaps[i].LogicalMin),
                        stick.AxisCaps[i].LogicalMin, stick.AxisCaps[i].LogicalMax,
                        Int16.MinValue, Int16.MaxValue);
                    stick.SetAxis(page, usage, scaled_value);
                }
            }
        }

        unsafe void UpdateButtons(RawInput* rin, Device stick)
        {
            stick.ClearButtons();

            for (int i = 0; i < stick.ButtonCaps.Count; i++)
            {
                short* usage_list = stackalloc short[(int)JoystickButton.Last + 1];
                int usage_length = (int)JoystickButton.Last;
                HIDPage page = stick.ButtonCaps[i].UsagePage;

                HidProtocolStatus status = HidProtocol.GetUsages(
                    HidProtocolReportType.Input,
                    page, 0, usage_list, ref usage_length,
                    PreparsedData,
                    new IntPtr((void*)&rin->Data.HID.RawData),
                    rin->Data.HID.Size);

                if (status != HidProtocolStatus.Success)
                {
                    Debug.Print("[WinRawJoystick] HidProtocol.GetUsages() failed with {0}",
                        Marshal.GetLastWin32Error());
                    continue;
                }

                for (int j = 0; j < usage_length; j++)
                {
                    short usage = *(usage_list + j);
                    stick.SetButton(page, usage, true);
                }
            }
        }

        #endregion

        #region Private Members

        static bool GetPreparsedData(IntPtr handle, ref byte[] prepared_data)
        {
            // Query the size of the _HIDP_PREPARSED_DATA structure for this event.
            int preparsed_size = 0;
            Functions.GetRawInputDeviceInfo(handle, RawInputDeviceInfoEnum.PREPARSEDDATA,
                IntPtr.Zero, ref preparsed_size);
            if (preparsed_size == 0)
            {
                Debug.Print("[WinRawJoystick] Functions.GetRawInputDeviceInfo(PARSEDDATA) failed with {0}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            // Allocate space for _HIDP_PREPARSED_DATA.
            // This is an untyped blob of data.
            if (prepared_data.Length < preparsed_size)
            {
                Array.Resize(ref prepared_data, preparsed_size);
            }

            if (Functions.GetRawInputDeviceInfo(handle, RawInputDeviceInfoEnum.PREPARSEDDATA,
                prepared_data, ref preparsed_size) < 0)
            {
                Debug.Print("[WinRawJoystick] Functions.GetRawInputDeviceInfo(PARSEDDATA) failed with {0}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            return true;
        }

        void QueryDeviceCaps(Device stick)
        {
            HidProtocolCaps caps;

            // Discovered elements
            int axes = 0;
            int dpads = 0;
            int buttons = 0;

            if (GetPreparsedData(stick.Handle, ref PreparsedData) &&
                GetDeviceCaps(stick, PreparsedData, out caps))
            {
                for (int i = 0; i < stick.AxisCaps.Count; i++)
                {
                    if (stick.AxisCaps[i].IsRange)
                    {
                        Debug.Print("[WinRawJoystick] Range axis elements not implemented.");
                        continue;
                    }

                    HIDPage page = stick.AxisCaps[i].UsagePage;
                    switch (page)
                    {
                        case HIDPage.GenericDesktop:
                            switch ((HIDUsageGD)stick.AxisCaps[i].NotRange.Usage)
                            {
                                case HIDUsageGD.X:
                                case HIDUsageGD.Y:
                                case HIDUsageGD.Z:
                                case HIDUsageGD.Rx:
                                case HIDUsageGD.Ry:
                                case HIDUsageGD.Rz:
                                case HIDUsageGD.Slider:
                                case HIDUsageGD.Dial:
                                case HIDUsageGD.Wheel:
                                    stick.SetAxis(page, stick.AxisCaps[i].NotRange.Usage, 0);
                                    break;

                                case HIDUsageGD.Hatswitch:
                                    stick.SetHat(page, stick.AxisCaps[i].NotRange.Usage, HatPosition.Centered);
                                    break;
                            }
                            break;

                        case HIDPage.Simulation:
                            switch ((HIDUsageSim)stick.AxisCaps[i].NotRange.Usage)
                            {
                                case HIDUsageSim.Rudder:
                                case HIDUsageSim.Throttle:
                                    stick.SetAxis(page, stick.AxisCaps[i].NotRange.Usage, 0);
                                    break;
                            }
                            break;
                    }
                }

                for (int i = 0; i < stick.ButtonCaps.Count; i++)
                {
                    bool is_range = stick.ButtonCaps[i].IsRange;
                    HIDPage page = stick.ButtonCaps[i].UsagePage;
                    switch (page)
                    {
                        case HIDPage.Button:
                            if (is_range)
                            {
                                for (short usage = stick.ButtonCaps[i].Range.UsageMin; usage <= stick.ButtonCaps[i].Range.UsageMax; usage++)
                                {
                                    buttons++;
                                    stick.SetButton(page, usage, false);
                                }
                            }
                            else
                            {
                                buttons++;
                                stick.SetButton(page, stick.ButtonCaps[i].NotRange.Usage, false);
                            }
                            break;

                        default:
                            Debug.Print("[WinRawJoystick] Unknown HIDPage {0} for button.", page);
                            break;
                    }
                }
            }

            stick.SetCapabilities(new JoystickCapabilities(axes, buttons, dpads, true));
        }

        static bool GetDeviceCaps(Device stick, byte[] preparsed_data, out HidProtocolCaps caps)
        {
            int axis_caps_count = 0;
            int button_caps_count = 0;

            // Query joystick capabilities
            caps = new HidProtocolCaps();
            if (HidProtocol.GetCaps(preparsed_data, ref caps) != HidProtocolStatus.Success)
            {
                Debug.Print("[WinRawJoystick] HidProtocol.GetCaps() failed with {0}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            // Make sure our caps arrays are big enough
            HidProtocolValueCaps[] axis_caps = new HidProtocolValueCaps[caps.NumberInputValueCaps];
            HidProtocolButtonCaps[] button_caps = new HidProtocolButtonCaps[caps.NumberInputButtonCaps];

            // Axis capabilities
            ushort axis_count = (ushort)axis_caps.Length;
            if (HidProtocol.GetValueCaps(HidProtocolReportType.Input,
                axis_caps, ref axis_count, preparsed_data) !=
                HidProtocolStatus.Success)
            {
                Debug.Print("[WinRawJoystick] HidProtocol.GetValueCaps() failed with {0}",
                    Marshal.GetLastWin32Error());
                return false;
            }
            axis_caps_count = (int)axis_count;

            // Button capabilities
            ushort button_count = (ushort)button_caps.Length;
            if (HidProtocol.GetButtonCaps(HidProtocolReportType.Input,
                button_caps, ref button_count, preparsed_data) !=
                HidProtocolStatus.Success)
            {
                Debug.Print("[WinRawJoystick] HidProtocol.GetButtonCaps() failed with {0}",
                    Marshal.GetLastWin32Error());
                return false;
            }
            button_caps_count = (int)button_count;

            stick.AxisCaps.Clear();
            stick.AxisCaps.AddRange(axis_caps);

            stick.ButtonCaps.Clear();
            stick.ButtonCaps.AddRange(button_caps);

            return true;
        }

        // Retrieves the GUID of a device, which is stored
        // in the last part of the DEVICENAME string
        Guid GetDeviceGuid(IntPtr handle)
        {
            Guid guid = new Guid();

            unsafe
            {
                // Find out how much memory we need to allocate
                // for the DEVICENAME string
                int size = 0;
                if (Functions.GetRawInputDeviceInfo(handle, RawInputDeviceInfoEnum.DEVICENAME, IntPtr.Zero, ref size) < 0 || size == 0)
                {
                    Debug.Print("[WinRawJoystick] Functions.GetRawInputDeviceInfo(DEVICENAME) failed with error {0}",
                        Marshal.GetLastWin32Error());
                    return guid;
                }

                // Allocate memory and retrieve the DEVICENAME string
                sbyte* pname = stackalloc sbyte[size + 1];
                if (Functions.GetRawInputDeviceInfo(handle, RawInputDeviceInfoEnum.DEVICENAME, (IntPtr)pname, ref size) < 0)
                {
                    Debug.Print("[WinRawJoystick] Functions.GetRawInputDeviceInfo(DEVICENAME) failed with error {0}",
                        Marshal.GetLastWin32Error());
                    return guid;
                }

                // Convert the buffer to a .Net string, and split it into parts
                string name = new string(pname);
                if (String.IsNullOrEmpty(name))
                {
                    Debug.Print("[WinRawJoystick] Failed to construct device name");
                    return guid;
                }

                // The GUID is stored in the last part of the string
                string[] parts = name.Split('#');
                if (parts.Length > 3)
                {
                    guid = new Guid(parts[3]);
                }
            }

            return guid;
        }

        Device GetDevice(IntPtr handle)
        {
            long hardware_id = handle.ToInt64();
            bool is_device_known = false;

            lock (UpdateLock)
            {
                is_device_known = Devices.FromHardwareId(hardware_id) != null;
            }

            if (!is_device_known)
            {
                RefreshDevices();
            }

            lock (UpdateLock)
            {
                return Devices.FromHardwareId(hardware_id);
            }
        }

        bool IsValid(int index)
        {
            return Devices.FromIndex(index) != null;
        }

        #endregion

        #region IJoystickDriver2 Members

        public JoystickState GetState(int index)
        {
            lock (UpdateLock)
            {
                if (IsValid(index))
                {
                    return Devices.FromIndex(index).GetState();
                }
                return new JoystickState();
            }
        }

        public JoystickCapabilities GetCapabilities(int index)
        {
            lock (UpdateLock)
            {
                if (IsValid(index))
                {
                    return Devices.FromIndex(index).GetCapabilities();
                }
                return new JoystickCapabilities();
            }
        }

        public Guid GetGuid(int index)
        {
            lock (UpdateLock)
            {
                if (IsValid(index))
                {
                    return Devices.FromIndex(index).GetGuid();
                }
                return new Guid();
            }
        }

        #endregion
    }
}
