using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;

namespace Loupedeck.AudioSwitcherPlugin.Helpers
{
    /// <summary>Represents a single app audio session exposed to the Volume Mixer.</summary>
    public class AudioSessionWrapper
    {
        public string Key          { get; }   // unique stable ID = ProcessName
        public string ProcessName  { get; }
        public string DisplayName  { get; }
        public string ExePath      { get; }
        public List<int> ProcessIds { get; }

        private byte[] _cachedIconBytes;
        private bool _iconExtracted;
        private readonly object _iconLock = new object();

        public AudioSessionWrapper(string processName, string displayName, string exePath, List<int> processIds)
        {
            ProcessName = processName;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? processName : displayName;
            ExePath     = exePath ?? string.Empty;
            Key         = processName;
            ProcessIds  = processIds ?? new List<int>();
        }

        // Volume helpers
        public float GetVolume()  => VolumeMixerManager.GetSessionVolume(ProcessIds);
        public void  SetVolume(float v) => VolumeMixerManager.SetSessionVolume(ProcessIds, v);
        public bool  IsMuted()    => VolumeMixerManager.IsSessionMuted(ProcessIds);
        public void  SetMute(bool m) => VolumeMixerManager.SetSessionMute(ProcessIds, m);

        /// <summary>Returns PNG bytes of the process icon, or null on failure.</summary>
        public byte[] GetIconBytes()
        {
            if (_iconExtracted) return _cachedIconBytes;
            lock (_iconLock)
            {
                if (_iconExtracted) return _cachedIconBytes;
                _cachedIconBytes = ExtractIcon();
                _iconExtracted = true;
                return _cachedIconBytes;
            }
        }

        private byte[] ExtractIcon()
        {
            if (string.IsNullOrEmpty(ExePath) || !File.Exists(ExePath))
                return null;
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(ExePath);
                if (icon == null) return null;
                using var bmp = icon.ToBitmap();
                using var ms  = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Icon extract failed for {ProcessName}: {ex.Message}");
                return null;
            }
        }
    }

    public static class VolumeMixerManager
    {
        // ─── STA helper ─────────────────────────────────────────────────────────
        // NAudio COM objects (MMDeviceEnumerator etc.) must be created on STA threads.
        // The Loupedeck timer and plugin threads are MTA, so we marshal COM work to STA.

        private static T RunOnSta<T>(Func<T> work)
        {
            T result = default;
            Exception caught = null;

            var thread = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { caught = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (caught != null) throw caught;
            return result;
        }

        private static void RunOnSta(Action work)
        {
            Exception caught = null;

            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { caught = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (caught != null) throw caught;
        }

        // ─── Session enumeration ────────────────────────────────────────────────

        /// <summary>Returns one wrapper per unique app name that has active/inactive audio sessions.</summary>
        public static List<AudioSessionWrapper> GetActiveSessions()
        {
            return RunOnSta(GetActiveSessionsCore);
        }

        private static List<AudioSessionWrapper> GetActiveSessionsCore()
        {
            var sessionDataList = new List<Tuple<int, string, string, string>>(); // PID, ProcName, DisplayName, ExePath

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    try
                    {
                        var mgr = device.AudioSessionManager;
                        var sessions = mgr.Sessions;

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            var pid = (int)session.GetProcessID;

                            // Skip system (pid 0)
                            if (pid == 0)
                                continue;

                            // Resolve process details
                            string procName  = string.Empty;
                            string exePath   = string.Empty;
                            string displayName = session.DisplayName;

                            try
                            {
                                using var proc = Process.GetProcessById(pid);
                                procName = proc.ProcessName;
                                exePath = GetProcessExePath(pid);
                            }
                            catch
                            {
                                // Process already exited
                                continue;
                            }

                            if (string.IsNullOrEmpty(procName))
                                continue;

                            sessionDataList.Add(Tuple.Create(pid, procName, displayName, exePath));
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error(ex, $"Failed enumerating sessions on device {device.FriendlyName}");
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "GetActiveSessions failed: " + ex.Message);
            }

            // Group by ProcessName to combine duplicate apps (like Discord)
            var grouped = sessionDataList
                .GroupBy(s => s.Item2, StringComparer.OrdinalIgnoreCase)
                .Select(g => {
                    var first = g.First();
                    // Accumulate all PIDs for this process name
                    var pids = g.Select(x => x.Item1).Distinct().ToList();
                    // Prefer non-empty ExePath
                    var exePath = g.Select(x => x.Item4).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? string.Empty;
                    var displayName = g.Select(x => x.Item3).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? first.Item2;
                    return new AudioSessionWrapper(first.Item2, displayName, exePath, pids);
                })
                .OrderBy(s => s.ProcessName)
                .ToList();

            return grouped;
        }

        // ─── Per-app volume/mute ────────────────────────────────────────────────

        public static float GetSessionVolume(List<int> pids)
        {
            return WithSession(pids, s => s.SimpleAudioVolume.Volume * 100f, 0f);
        }

        public static void SetSessionVolume(List<int> pids, float pct)
        {
            float clamped = Math.Max(0f, Math.Min(100f, pct));
            WithSession(pids, s => { s.SimpleAudioVolume.Volume = clamped / 100f; return true; }, false);
        }

        public static bool IsSessionMuted(List<int> pids)
        {
            return WithSession(pids, s => s.SimpleAudioVolume.Mute, false);
        }

        public static void SetSessionMute(List<int> pids, bool mute)
        {
            WithSession(pids, s => { s.SimpleAudioVolume.Mute = mute; return true; }, false);
        }

        // ─── Master volume ──────────────────────────────────────────────────────

        public static float GetMasterVolume()
        {
            return WithDefaultDevice(d => d.AudioEndpointVolume.MasterVolumeLevelScalar * 100f, 0f);
        }

        public static void SetMasterVolume(float pct)
        {
            float clamped = Math.Max(0f, Math.Min(100f, pct));
            WithDefaultDevice(d => { d.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100f; return true; }, false);
        }

        public static bool IsMasterMuted()
        {
            return WithDefaultDevice(d => d.AudioEndpointVolume.Mute, false);
        }

        public static void SetMasterMute(bool mute)
        {
            WithDefaultDevice(d => { d.AudioEndpointVolume.Mute = mute; return true; }, false);
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, [Out] StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static string GetProcessExePath(int pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                int capacity = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                {
                    return sb.ToString();
                }
            }
            catch { }
            finally
            {
                CloseHandle(hProcess);
            }
            return string.Empty;
        }

        private static T WithDefaultDevice<T>(Func<MMDevice, T> action, T fallback)
        {
            try
            {
                return RunOnSta(() =>
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device     = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    return action(device);
                });
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Master volume operation failed: " + ex.Message);
                return fallback;
            }
        }

        private static T WithSession<T>(List<int> pids, Func<AudioSessionControl, T> action, T fallback)
        {
            if (pids == null || pids.Count == 0)
                return fallback;

            bool found = false;
            T result = fallback;

            try
            {
                result = RunOnSta(() =>
                {
                    bool foundInner = false;
                    T resultInner = fallback;

                    using var enumerator = new MMDeviceEnumerator();
                    var devices    = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                    foreach (var device in devices)
                    {
                        try
                        {
                            var sessions = device.AudioSessionManager.Sessions;
                            for (int i = 0; i < sessions.Count; i++)
                            {
                                using var s = sessions[i];
                                if (pids.Contains((int)s.GetProcessID))
                                {
                                    if (!foundInner)
                                    {
                                        resultInner = action(s);
                                        foundInner = true;
                                    }
                                    else
                                    {
                                        action(s);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            device.Dispose();
                        }
                    }

                    found = foundInner;
                    return resultInner;
                });
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Session operation failed: {ex.Message}");
            }
            return found ? result : fallback;
        }
    }
}

