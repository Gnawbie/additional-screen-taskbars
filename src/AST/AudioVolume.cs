using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AST
{
    /// <summary>A playback device, such as speakers or a headset.</summary>
    internal sealed class AudioOutput
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    /// <summary>The default playback device's volume, through Windows' documented Core Audio interfaces.</summary>
    public static partial class AudioVolume
    {
        private const int Render = 0, Multimedia = 1, ClsctxAll = 23;
        private const int RoleConsole = 0, RoleCommunications = 2, StateActive = 1, StorageRead = 0;
        private const ushort VtLpwstr = 31;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            void GetCount(out uint count);
            void Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            void OpenPropertyStore(int access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant { public ushort Type; public ushort Reserved1, Reserved2, Reserved3; public IntPtr Pointer; public IntPtr Extra; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        // Windows has no documented way to change the default device. This is the interface its own sound settings
        // use, and programs that switch devices all rely on it. Only the method used here needs its real signature.
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            void GetMixFormat();
            void GetDeviceFormat();
            void ResetDeviceFormat();
            void SetDeviceFormat();
            void GetProcessingPeriod();
            void SetProcessingPeriod();
            void GetShareMode();
            void SetShareMode();
            void GetPropertyValue();
            void SetPropertyValue();
            void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        /// <summary>The playback devices that are connected and turned on.</summary>
        internal static List<AudioOutput> Outputs()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out IMMDevice current);
            current.GetId(out string currentId);

            var outputs = new List<AudioOutput>();
            enumerator.EnumAudioEndpoints(Render, StateActive, out IMMDeviceCollection devices);
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out IMMDevice device);
                device.GetId(out string id);
                outputs.Add(new AudioOutput { Id = id, Name = NameOf(device), IsDefault = id == currentId });
            }
            return outputs;
        }

        private static string NameOf(IMMDevice device)
        {
            device.OpenPropertyStore(StorageRead, out IPropertyStore store);
            var friendlyName = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            store.GetValue(ref friendlyName, out PropVariant value);
            try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : "Unknown device"; }
            finally { PropVariantClear(ref value); }
        }

        /// <summary>Sets every role, as Windows' Sound settings do, so calls move to the new device as well.</summary>
        internal static void SetDefaultOutput(string id)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = RoleConsole; role <= RoleCommunications; role++) policy.SetDefaultEndpoint(id, role);
        }

        // Every method has to be listed, in order, because COM calls them by position.
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IntPtr notify);
            void UnregisterControlChangeNotify(IntPtr notify);
            void GetChannelCount(out uint count);
            void SetMasterVolumeLevel(float decibels, ref Guid context);
            void SetMasterVolumeLevelScalar(float level, ref Guid context);
            void GetMasterVolumeLevel(out float decibels);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
            void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            void GetChannelVolumeLevel(uint channel, out float decibels);
            void GetChannelVolumeLevelScalar(uint channel, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        private static IAudioEndpointVolume Open()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out IMMDevice device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object result);
            return (IAudioEndpointVolume)result;
        }

        /// <summary>From 0 to 100.</summary>
        public static int Level
        {
            get
            {
                Open().GetMasterVolumeLevelScalar(out float level);
                return (int)Math.Round(level * 100);
            }
            set
            {
                var context = Guid.Empty;
                Open().SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, value)) / 100f, ref context);
            }
        }

        public static bool IsMuted
        {
            get
            {
                Open().GetMute(out bool muted);
                return muted;
            }
            set
            {
                var context = Guid.Empty;
                Open().SetMute(value, ref context);
            }
        }
    }
}
