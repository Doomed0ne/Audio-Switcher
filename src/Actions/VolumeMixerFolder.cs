using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Loupedeck.AudioSwitcherPlugin.Helpers;

namespace Loupedeck.AudioSwitcherPlugin.Actions
{
    /// <summary>
    /// Volume Mixer dynamic folder.
    ///   Encoder dials  → rotate = volume up/down, press = mute toggle
    ///   Middle buttons → ◀ Back (close folder) | ◀ Prev page | Next page ▶ | (info slots)
    /// </summary>
    public class VolumeMixerFolder : PluginDynamicFolder
    {
        // ── Special button action parameter keys ─────────────────────────────
        private const string BtnBack = "__btn_back__";
        private const string BtnPrev = "__btn_prev__";
        private const string BtnNext = "__btn_next__";
        private const string BtnEmpty = "__btn_empty__";

        // ── Audio ─────────────────────────────────────────────────────────────
        private const string SystemKey = "__system__";
        private List<AudioSessionWrapper> _sessions = new();
        private Timer _refreshTimer;

        // ── Paging ────────────────────────────────────────────────────────────
        // The encoder area shows up to SLOTS_PER_PAGE items at a time.
        // Page 0 always starts at index 0 (System + apps).
        private const int SLOTS_PER_PAGE = 6;
        private int _page = 0;

        private int TotalSlots => 1 + _sessions.Count; // System + app sessions
        private int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalSlots / (double)SLOTS_PER_PAGE));

        public VolumeMixerFolder()
        {
            this.DisplayName = "Volume Mixer";
            this.GroupName   = "Audio";
            this.Description = "Per-app volume control. Rotate = volume. Press dial = mute. Middle buttons = navigate.";
            PluginLog.Info("VolumeMixerFolder ctor");
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override bool Activate()
        {
            PluginLog.Info("VolumeMixerFolder.Activate");
            _page = 0;
            RefreshSessions();
            this.EncoderActionNamesChanged();
            this.ButtonActionNamesChanged();

            _refreshTimer?.Dispose();
            _refreshTimer = new Timer(_ => OnTimerTick(), null, 2000, 2000);
            return true;
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType)
            => PluginDynamicFolderNavigation.EncoderArea;

        public override bool Deactivate()
        {
            PluginLog.Info("VolumeMixerFolder.Deactivate");
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            return true;
        }

        private void OnTimerTick()
        {
            try
            {
                var oldKeys = string.Join(",", _sessions.Select(s => s.Key));
                var newSessions = VolumeMixerManager.GetActiveSessions();
                var newKeys = string.Join(",", newSessions.Select(s => s.Key));

                if (oldKeys != newKeys)
                {
                    PluginLog.Info($"Sessions changed [{oldKeys}] → [{newKeys}]");
                    _sessions = newSessions;
                    // Clamp page
                    if (_page >= TotalPages) _page = Math.Max(0, TotalPages - 1);
                    this.EncoderActionNamesChanged();
                    this.ButtonActionNamesChanged();
                }
            }
            catch (Exception ex) { PluginLog.Error(ex, "TimerTick error"); }
        }

        // ── Encoder slot lists (dials) ───────────────────────────────────────
        // Returns up to SLOTS_PER_PAGE items for the current page.

        private List<string> CurrentPageKeys()
        {
            var allKeys = new List<string> { SystemKey };
            allKeys.AddRange(_sessions.Select(s => s.Key));

            int start = _page * SLOTS_PER_PAGE;
            int count = Math.Min(SLOTS_PER_PAGE, allKeys.Count - start);
            return count > 0 ? allKeys.GetRange(start, count) : new List<string>();
        }

        public override IEnumerable<string> GetEncoderRotateActionNames(DeviceType deviceType)
        {
            if (_sessions.Count == 0) RefreshSessions();

            var keys = CurrentPageKeys();
            var names = keys.Select(k => this.CreateAdjustmentName(k)).ToList();
            PluginLog.Info($"GetEncoderRotateActionNames page={_page}/{TotalPages} slots={names.Count}");
            return names;
        }

        public override IEnumerable<string> GetEncoderPressActionNames(DeviceType deviceType)
        {
            if (_sessions.Count == 0) RefreshSessions();

            var keys = CurrentPageKeys();
            var names = keys.Select(k => this.CreateCommandName(k)).ToList();
            PluginLog.Info($"GetEncoderPressActionNames page={_page}/{TotalPages} slots={names.Count}");
            return names;
        }

        // ── Middle button slot list ──────────────────────────────────────────
        // Fixed layout: [Back] [Prev] [Next] [info] [info] [info]

        public override IEnumerable<string> GetButtonPressActionNames(DeviceType deviceType)
        {
            var names = new List<string>
            {
                this.CreateCommandName(BtnBack),
                this.CreateCommandName(BtnPrev),
                this.CreateCommandName(BtnNext)
            };
            PluginLog.Info("GetButtonPressActionNames returning 3 nav buttons");
            return names;
        }

        // ── Encoder events (rotate) ──────────────────────────────────────────

        public override void ApplyAdjustment(string actionParameter, int diff)
        {
            string key = ResolveKey(actionParameter);
            PluginLog.Info($"ApplyAdjustment key={key} diff={diff}");
            try
            {
                if (key == SystemKey)
                {
                    float v = VolumeMixerManager.GetMasterVolume() + diff;
                    VolumeMixerManager.SetMasterVolume(v);
                }
                else
                {
                    var s = SessionByKey(key);
                    if (s != null) s.SetVolume(s.GetVolume() + diff);
                    else PluginLog.Warning($"No session for key={key}");
                }
            }
            catch (Exception ex) { PluginLog.Error(ex, "ApplyAdjustment error"); }

            this.AdjustmentValueChanged(actionParameter);
            this.AdjustmentImageChanged(actionParameter);
        }

        // ── Encoder press (dial press) = MUTE ────────────────────────────────





        // ── Middle button press = navigation ─────────────────────────────────

        public override void RunCommand(string actionParameter)
        {
            string key = ResolveKey(actionParameter);
            PluginLog.Info($"RunCommand key={key}");

            switch (key)
            {
                case BtnBack:
                    PluginLog.Info("Back button pressed — closing folder");
                    this.Close();
                    break;

                case BtnPrev:
                    if (_page > 0)
                    {
                        _page--;
                        PluginLog.Info($"Prev page → {_page}");
                        this.EncoderActionNamesChanged();
                        this.ButtonActionNamesChanged();
                    }
                    break;

                case BtnNext:
                    if (_page < TotalPages - 1)
                    {
                        _page++;
                        PluginLog.Info($"Next page → {_page}");
                        this.EncoderActionNamesChanged();
                        this.ButtonActionNamesChanged();
                    }
                    break;

                default:
                    // Could be a session key if routed here — try mute
                    ToggleMute(actionParameter);
                    break;
            }
        }

        private void ToggleMute(string actionParameter)
        {
            string key = ResolveKey(actionParameter);
            PluginLog.Info($"ToggleMute key={key}");
            try
            {
                if (key == SystemKey)
                    VolumeMixerManager.SetMasterMute(!VolumeMixerManager.IsMasterMuted());
                else
                {
                    var s = SessionByKey(key);
                    if (s != null) s.SetMute(!s.IsMuted());
                }
            }
            catch (Exception ex) { PluginLog.Error(ex, "ToggleMute error"); }

            this.AdjustmentValueChanged(actionParameter);
            this.AdjustmentImageChanged(actionParameter);
            this.CommandImageChanged(actionParameter);
        }

        // ── Display names ────────────────────────────────────────────────────

        public override string GetAdjustmentDisplayName(string actionParameter, PluginImageSize imageSize)
        {
            string key = ResolveKey(actionParameter);
            if (key == SystemKey) return "System";
            return SessionByKey(key)?.DisplayName ?? actionParameter;
        }

        public override string GetAdjustmentValue(string actionParameter)
        {
            string key = ResolveKey(actionParameter);
            try
            {
                if (key == SystemKey)
                    return VolumeMixerManager.IsMasterMuted() ? "Muted" : $"{Math.Round(VolumeMixerManager.GetMasterVolume())}%";
                var s = SessionByKey(key);
                if (s != null)
                    return s.IsMuted() ? "Muted" : $"{Math.Round(s.GetVolume())}%";
            }
            catch (Exception ex) { PluginLog.Error(ex, "GetAdjustmentValue error"); }
            return "—";
        }

        // ── Images ───────────────────────────────────────────────────────────

        public override BitmapImage GetAdjustmentImage(string actionParameter, PluginImageSize imageSize)
            => BuildAudioImage(actionParameter, imageSize);

        public override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
            => BuildNavButtonImage(actionParameter, imageSize);

        // Build image for encoder (audio session) slot
        private BitmapImage BuildAudioImage(string actionParameter, PluginImageSize imageSize)
        {
            string key = ResolveKey(actionParameter);
            PluginLog.Info($"BuildAudioImage key='{key}'");
            try
            {
                using var builder = new BitmapBuilder(imageSize);
                builder.Clear(new BitmapColor(20, 20, 25));

                bool muted = false;

                if (key == SystemKey)
                {
                    muted = VolumeMixerManager.IsMasterMuted();
                    builder.DrawText("SYS", 0, 6, builder.Width, 22, BitmapColor.White, 15, 17, 0);
                }
                else
                {
                    var session = SessionByKey(key);
                    if (session == null)
                    {
                        PluginLog.Warning($"BuildAudioImage: no session for key='{key}'");
                        builder.DrawText(key.Length > 8 ? key.Substring(0, 8) : key,
                            0, 10, builder.Width, 30, BitmapColor.White, 12, 14, 0);
                        return builder.ToImage();
                    }

                    muted = session.IsMuted();

                    // Try to draw app icon
                    byte[] iconBytes = session.GetIconBytes();
                    bool drewIcon = false;
                    if (iconBytes != null && iconBytes.Length > 0)
                    {
                        try
                        {
                            using var img = BitmapImage.FromArray(iconBytes);
                            if (img != null && img.Width > 0 && img.Height > 0)
                            {
                                int x = Math.Max(0, (builder.Width - img.Width) / 2);
                                int y = Math.Max(2, (builder.Height - img.Height) / 2 - 6);
                                builder.DrawImage(img, x, y);
                                drewIcon = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Error(ex, $"Icon draw failed for key='{key}'");
                        }
                    }

                    if (!drewIcon)
                    {
                        string name = session.DisplayName;
                        string shortName = name.Length > 8 ? name.Substring(0, 8) : name;
                        builder.DrawText(shortName, 0, 6, builder.Width, 22, BitmapColor.White, 13, 15, 0);
                    }
                }

                if (muted)
                    builder.FillRectangle(0, 0, builder.Width, builder.Height, new BitmapColor(180, 0, 0, 100));

                return builder.ToImage();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"BuildAudioImage error for '{actionParameter}'");
                using var b = new BitmapBuilder(imageSize);
                b.Clear(BitmapColor.Black);
                b.DrawText("VOL", 0, 10, b.Width, 20, BitmapColor.White, 14, 16, 0);
                return b.ToImage();
            }
        }

        // Build image for navigation middle buttons
        private BitmapImage BuildNavButtonImage(string actionParameter, PluginImageSize imageSize)
        {
            string key = ResolveKey(actionParameter);
            PluginLog.Info($"BuildNavButtonImage key='{key}'");
            try
            {
                using var builder = new BitmapBuilder(imageSize);

                switch (key)
                {
                    case BtnBack:
                        builder.Clear(new BitmapColor(40, 20, 20));
                        builder.DrawText("◀ Back", 0, 0, builder.Width, builder.Height, BitmapColor.White, 14, 16, 0);
                        break;

                    case BtnPrev:
                        bool hasPrev = _page > 0;
                        builder.Clear(hasPrev ? new BitmapColor(20, 30, 50) : new BitmapColor(25, 25, 25));
                        builder.DrawText("◀ Prev", 0, 0, builder.Width, builder.Height,
                            hasPrev ? new BitmapColor(140, 200, 255) : new BitmapColor(60, 60, 60), 13, 15, 0);
                        if (hasPrev)
                            builder.DrawText($"pg {_page}/{TotalPages}", 0, builder.Height - 18, builder.Width, 16,
                                new BitmapColor(100, 140, 200), 10, 12, 0);
                        break;

                    case BtnNext:
                        bool hasNext = _page < TotalPages - 1;
                        builder.Clear(hasNext ? new BitmapColor(20, 30, 50) : new BitmapColor(25, 25, 25));
                        builder.DrawText("Next ▶", 0, 0, builder.Width, builder.Height,
                            hasNext ? new BitmapColor(140, 200, 255) : new BitmapColor(60, 60, 60), 13, 15, 0);
                        if (hasNext)
                            builder.DrawText($"pg {_page + 2}/{TotalPages}", 0, builder.Height - 18, builder.Width, 16,
                                new BitmapColor(100, 140, 200), 10, 12, 0);
                        break;

                    default:
                        // Empty slot — show page indicator in first empty
                        builder.Clear(new BitmapColor(18, 18, 22));
                        if (key == BtnEmpty)
                        {
                            string pageInfo = TotalPages > 1 ? $"{_page + 1}/{TotalPages}" : "";
                            if (!string.IsNullOrEmpty(pageInfo))
                                builder.DrawText(pageInfo, 0, 0, builder.Width, builder.Height,
                                    new BitmapColor(80, 80, 80), 11, 13, 0);
                        }
                        break;
                }

                return builder.ToImage();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"BuildNavButtonImage error for '{actionParameter}'");
                using var b = new BitmapBuilder(imageSize);
                b.Clear(BitmapColor.Black);
                return b.ToImage();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the compound SDK action parameter back to our simple key.
        /// SDK wraps keys like: "DynamicFolderAdjustment#Namespace.Class_____myKey__"
        /// </summary>
        private string ResolveKey(string actionParameter)
        {
            if (string.IsNullOrEmpty(actionParameter)) return SystemKey;

            if (_sessions.Count == 0) RefreshSessions();

            // Check nav button keys
            foreach (var nav in new[] { BtnBack, BtnPrev, BtnNext, BtnEmpty })
                if (actionParameter.IndexOf(nav, StringComparison.OrdinalIgnoreCase) >= 0)
                    return nav == BtnEmpty ? BtnEmpty : nav;

            if (actionParameter.IndexOf(BtnEmpty, StringComparison.OrdinalIgnoreCase) >= 0)
                return BtnEmpty;

            if (actionParameter == SystemKey || actionParameter.Contains(SystemKey))
                return SystemKey;

            // Exact match in sessions
            if (_sessions.Any(s => s.Key.Equals(actionParameter, StringComparison.OrdinalIgnoreCase)))
                return actionParameter;

            // Substring match (handles SDK compound format)
            foreach (var session in _sessions)
            {
                if (actionParameter.IndexOf(session.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return session.Key;
            }

            return actionParameter;
        }

        private void RefreshSessions()
        {
            _sessions = VolumeMixerManager.GetActiveSessions();
            PluginLog.Info($"RefreshSessions: {_sessions.Count} sessions");
            foreach (var s in _sessions)
                PluginLog.Info($"  {s.ProcessName} pids=[{string.Join(",", s.ProcessIds)}] exe='{s.ExePath}'");
        }

        private AudioSessionWrapper SessionByKey(string key)
            => _sessions.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
