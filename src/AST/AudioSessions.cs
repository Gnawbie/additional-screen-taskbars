using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AST
{
    /// <summary>A program playing sound, with every one of its streams on the default device.</summary>
    internal sealed class AudioApp
    {
        private readonly List<object> _sessions = new List<object>();

        public uint ProcessId;
        public bool IsSystemSounds;
        public string Name;
        public string ExePath;

        internal void Add(object session) => _sessions.Add(session);

        /// <summary>From 0 to 100. A program's streams normally share one level, so the first one speaks for all.</summary>
        public int Level
        {
            get
            {
                ((AudioVolume.ISimpleAudioVolume)_sessions[0]).GetMasterVolume(out float level);
                return (int)Math.Round(level * 100);
            }
            set
            {
                var context = Guid.Empty;
                float level = Math.Max(0, Math.Min(100, value)) / 100f;
                foreach (AudioVolume.ISimpleAudioVolume s in _sessions) s.SetMasterVolume(level, ref context);
            }
        }

        public bool IsMuted
        {
            get
            {
                ((AudioVolume.ISimpleAudioVolume)_sessions[0]).GetMute(out bool muted);
                return muted;
            }
            set
            {
                var context = Guid.Empty;
                foreach (AudioVolume.ISimpleAudioVolume s in _sessions) s.SetMute(value, ref context);
            }
        }

        /// <summary>How loud it is right now, from 0 to 1.</summary>
        public float Peak
        {
            get
            {
                float peak = 0;
                foreach (AudioVolume.IAudioMeterInformation s in _sessions)
                {
                    s.GetPeakValue(out float p);
                    peak = Math.Max(peak, p);
                }
                return peak;
            }
        }
    }

    public static partial class AudioVolume
    {
        private const int SessionExpired = 2;

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            void GetAudioSessionControl(IntPtr groupingId, int flags, out IntPtr control);
            void GetSimpleAudioVolume(IntPtr groupingId, int flags, out IntPtr volume);
            void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        // IAudioSessionControl's methods come first, because COM calls them by position.
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            void GetState(out int state);
            void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            void GetGroupingParam(out Guid grouping);
            void SetGroupingParam(ref Guid grouping, ref Guid context);
            void RegisterAudioSessionNotification(IntPtr notify);
            void UnregisterAudioSessionNotification(IntPtr notify);
            void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetProcessId(out uint id);
            [PreserveSig] int IsSystemSoundsSession();
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            void SetMasterVolume(float level, ref Guid context);
            void GetMasterVolume(out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
        }

        private static IMMDevice DefaultDevice()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out IMMDevice device);
            return device;
        }

        private static T Activate<T>(IMMDevice device)
        {
            var iid = typeof(T).GUID;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object result);
            return (T)result;
        }

        /// <summary>The default device's name, such as "Speakers".</summary>
        internal static string DeviceName => NameOf(DefaultDevice());

        /// <summary>How loud the default device is right now, from 0 to 1.</summary>
        internal static Func<float> DeviceMeter()
        {
            var meter = Activate<IAudioMeterInformation>(DefaultDevice());
            return () => { meter.GetPeakValue(out float p); return p; };
        }

        /// <summary>The programs with sound on the default device, System Sounds first, as Windows 10's mixer lists them.</summary>
        internal static List<AudioApp> Apps()
        {
            Activate<IAudioSessionManager2>(DefaultDevice()).GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            sessions.GetCount(out int count);
            var byProgram = new Dictionary<string, AudioApp>();
            var apps = new List<AudioApp>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessions.GetSession(i, out object session);
                    var control = (IAudioSessionControl2)session;
                    control.GetState(out int state);
                    if (state == SessionExpired) continue;
                    bool system = control.IsSystemSoundsSession() == 0;
                    control.GetProcessId(out uint pid);
                    // Programs like Discord and browsers play sound from several processes, which should share one column.
                    string exePath = system ? null : Native.ExePathOf(pid);
                    string key = system ? "system" : exePath != null ? exePath.ToLowerInvariant() : "pid " + pid;
                    if (!byProgram.TryGetValue(key, out AudioApp app))
                    {
                        app = new AudioApp { ProcessId = pid, IsSystemSounds = system };
                        if (system) app.Name = "System Sounds";
                        else
                        {
                            app.ExePath = exePath;
                            // Programs can name their own sessions. Windows' own names are resource references starting with @.
                            control.GetDisplayName(out string display);
                            app.Name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith("@") ? display
                                : app.ExePath != null ? Path.GetFileNameWithoutExtension(app.ExePath) : "Program " + pid;
                        }
                        byProgram[key] = app;
                        apps.Add(app);
                    }
                    app.Add(session);
                }
                // A stream that ends while being read just drops out of the list.
                catch (COMException) { }
                catch (InvalidCastException) { }
            }
            return apps.OrderByDescending(a => a.IsSystemSounds).ToList();
        }
    }
}
