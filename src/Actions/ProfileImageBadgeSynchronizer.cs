namespace Loupedeck.AudioSwitcherPlugin;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

internal static class ProfileImageBadgeSynchronizer
{
    private const String OutputActionName = "$AudioSwitcher___Loupedeck.AudioSwitcherPlugin.SwitchAudioOutputCommand";
    private const String InputActionName = "$AudioSwitcher___Loupedeck.AudioSwitcherPlugin.SwitchAudioInputCommand";

    private static readonly Object ActionImagesLock = new();
    private static readonly Dictionary<String, CachedActionImages> ActionImagesByParameters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<String, String> ActionImageSignatures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<String, DateTime> IctTimestampCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedActionImages(Byte[] Inactive, Byte[] Active);

    public static Boolean Synchronize(
        EDataFlow dataFlow,
        String currentDeviceKey,
        Action<ActionEditorActionParameters, Boolean> onDeviceMatched = null,
        Action onActionImagesChanged = null)
    {
        var profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Logi",
            "LogiPluginService",
            "Applications");

        if (!Directory.Exists(profilesRoot))
        {
            return false;
        }

        var actionName = dataFlow == EDataFlow.Render ? OutputActionName : InputActionName;
        var foundMatchingActions = false;
        var actionImagesChanged = false;
        var imageCache = new Dictionary<String, CachedActionImages>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var profilePath in EnumerateProfileInfoFiles(profilesRoot))
            {
                foundMatchingActions |= SynchronizeProfile(
                    profilePath,
                    dataFlow,
                    actionName,
                    currentDeviceKey,
                    onDeviceMatched,
                    imageCache,
                    ref actionImagesChanged);
            }

            lock (ActionImagesLock)
            {
                var flowPrefix = $"{dataFlow}:";
                var staleKeys = new List<String>();
                foreach (var key in ActionImagesByParameters.Keys)
                {
                    if (key.StartsWith(flowPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        staleKeys.Add(key);
                    }
                }

                foreach (var key in staleKeys)
                {
                    ActionImagesByParameters.Remove(key);
                }

                foreach (var pair in imageCache)
                {
                    ActionImagesByParameters[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Could not synchronize saved audio action states.");
        }

        if (actionImagesChanged)
        {
            try
            {
                onActionImagesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not refresh button images after profile artwork changed.");
            }
        }

        return foundMatchingActions;
    }

    /// <summary>
    /// Returns cached button artwork bytes for in-memory rendering via GetCommandImage().
    /// </summary>
    internal static Byte[] GetCommandImageBytes(
        EDataFlow dataFlow,
        String deviceKey,
        BadgePosition badgePosition,
        Int32 stateIndex)
    {
        if (String.IsNullOrWhiteSpace(deviceKey))
        {
            return null;
        }

        var cacheKey = CreateImageLookupKey(dataFlow, deviceKey, badgePosition);
        CachedActionImages images;
        lock (ActionImagesLock)
        {
            if (!ActionImagesByParameters.TryGetValue(cacheKey, out images))
            {
                return null;
            }
        }

        return stateIndex == 1 ? images.Active ?? images.Inactive : images.Inactive ?? images.Active;
    }

    private static IEnumerable<String> EnumerateProfileInfoFiles(String profilesRoot)
    {
        var pending = new Stack<String>();
        var visited = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
        pending.Push(profilesRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            String normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(directory);
            }
            catch
            {
                continue;
            }

            if (!visited.Add(normalizedPath))
            {
                continue;
            }

            String[] profileFiles;
            try
            {
                profileFiles = Directory.GetFiles(directory, "ProfileInfo.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                profileFiles = Array.Empty<String>();
            }

            foreach (var profileFile in profileFiles)
            {
                yield return profileFile;
            }

            String[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch
                {
                    // A locked or disappearing profile folder must not block other profiles.
                }
            }
        }
    }

    private static Boolean SynchronizeProfile(
        String profilePath,
        EDataFlow dataFlow,
        String actionName,
        String currentDeviceKey,
        Action<ActionEditorActionParameters, Boolean> onDeviceMatched,
        Dictionary<String, CachedActionImages> imageCache,
        ref Boolean actionImagesChanged)
    {
        var foundMatchingActions = false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("profileActions", out var profileActions) ||
                profileActions.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var profileDirectory = Path.GetDirectoryName(profilePath);
            if (String.IsNullOrEmpty(profileDirectory))
            {
                return false;
            }

            var imagesDirectory = Path.Combine(profileDirectory, "ActionImages");
            var iconsDirectory = Path.Combine(profileDirectory, "ActionIcons");

            if (!Directory.Exists(imagesDirectory))
            {
                return false;
            }

            if (!Directory.Exists(iconsDirectory))
            {
                try
                {
                    Directory.CreateDirectory(iconsDirectory);
                }
                catch { }
            }

            foreach (var action in profileActions.EnumerateArray())
            {
                if (!TryGetString(action, "templateActionName", out var templateActionName) ||
                    !String.Equals(templateActionName, actionName, StringComparison.Ordinal) ||
                    !TryGetString(action, "name", out var profileActionName) ||
                    !TryGetParameters(action, out var parameters))
                {
                    continue;
                }

                try
                {
                    TryGetString(action, "displayName", out var actionDisplayName);

                    var deviceKey = GetParameter(parameters, "device");
                    var badgePosition = AudioDeviceSwitchImage.ParseBadgePosition(GetParameter(parameters, "badgePosition"));
                    var isCurrentDevice = !String.IsNullOrWhiteSpace(currentDeviceKey) &&
                                          String.Equals(deviceKey, currentDeviceKey, StringComparison.OrdinalIgnoreCase);

                    // ── 1. Active State Notification (Startup Query & Device Matching) ──
                    // Notifies Loupedeck of the state (1 = Active, 0 = Inactive) so the physical LCD
                    // and software display the correct state upon startup without requiring manual button clicks.
                    if (onDeviceMatched != null)
                    {
                        try
                        {
                            var actionParams = new ActionEditorActionParameters();
                            foreach (var prop in parameters.EnumerateObject())
                            {
                                // Skip JSON-serialization metadata keys (e.g. "$type").
                                // Loupedeck's internal ActionEditorActionParameters does NOT include
                                // these in its Parameters dictionary, so including them here produces
                                // a different GetHashCode64() and causes SetCurrentState() to write
                                // into the wrong hash slot — silently doing nothing.
                                if (prop.Value.ValueKind == JsonValueKind.String &&
                                    !prop.Name.StartsWith("$", StringComparison.Ordinal))
                                {
                                    actionParams.Parameters[prop.Name] = prop.Value.GetString();
                                }
                            }
                            onDeviceMatched(actionParams, isCurrentDevice);
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Warning(ex, $"Failed to invoke onDeviceMatched for '{profileActionName}'.");
                        }
                    }

                    // ── 2. Non-Destructive Canvas Persistence & Editor State Hydration ──
                    // Ensures ActionIcons/{actionId}___Inactive.ict exists with discrete Text and Image layers,
                    // so opening the Icon Editor presents editable layers and populated inspector fields.
                    PreserveAndHydrateLayerTemplate(iconsDirectory, profileActionName, actionDisplayName);

                    // ── 3. Clean 3-Tier Image Asset Separation (Hardware Badging & Popups) ─
                    var inactiveImagePath = Path.Combine(imagesDirectory, $"{profileActionName}___Inactive.png");
                    var activeImagePath   = Path.Combine(imagesDirectory, $"{profileActionName}___Active.png");
                    var defaultImagePath  = Path.Combine(imagesDirectory, $"{profileActionName}.png");
                    var state0Path        = Path.Combine(imagesDirectory, $"{profileActionName}___state0000.png");
                    var state1Path        = Path.Combine(imagesDirectory, $"{profileActionName}___state0001.png");

                    // Source artwork is owned by the user via Icon Editor.
                    // Inactive artwork is ALWAYS clean (unbadged).
                    var sourceBytes = ReadCustomImage(inactiveImagePath)
                                   ?? ReadCustomImage(state0Path)
                                   ?? ReadCustomImage(defaultImagePath);

                    if (sourceBytes == null)
                    {
                        continue;
                    }

                    // Tier 1: Base icon asset ({profileActionName}.png) is fetched by Loupedeck for
                    // on-screen notification popups. MUST ALWAYS BE CLEAN (unbadged) so popups never
                    // display a green checkmark badge.
                    WriteImage(sourceBytes, defaultImagePath, BadgePosition.None);

                    // Tier 2: State 0 (Inactive) must always be clean unbadged artwork.
                    WriteImage(sourceBytes, state0Path, BadgePosition.None);

                    // Tier 3: State 1 (Active) receives the dynamic checkmark badge when this device is
                    // the active default. When inactive, it receives clean artwork.
                    var activeBadge = isCurrentDevice ? badgePosition : BadgePosition.None;
                    var badgedActiveBytes = WriteImage(sourceBytes, activeImagePath, activeBadge);
                    WriteImage(sourceBytes, state1Path, activeBadge);

                    // Update in-memory cache for GetCommandImage
                    var cacheKey = CreateImageLookupKey(dataFlow, deviceKey, badgePosition);
                    if (!imageCache.ContainsKey(cacheKey))
                    {
                        imageCache[cacheKey] = new CachedActionImages(sourceBytes, badgedActiveBytes);
                    }

                    // Track file stamps for change detection
                    var sourceIdentity = $"{profileDirectory}|{profileActionName}";
                    var signature = String.Join("|",
                        GetImageFileStamp(inactiveImagePath),
                        GetImageFileStamp(activeImagePath),
                        GetImageFileStamp(defaultImagePath));
                    actionImagesChanged |= UpdateActionImageSignature($"{dataFlow}:{sourceIdentity}", signature);

                    foundMatchingActions = true;
                }
                catch (Exception actionEx)
                {
                    PluginLog.Warning(actionEx, $"Failed synchronizing action '{profileActionName}' in profile '{profilePath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"Could not synchronize saved audio action states for '{profilePath}'.");
        }

        return foundMatchingActions;
    }

    private static Boolean TryGetParameters(JsonElement action, out JsonElement parameters)
    {
        parameters = default;
        return action.ValueKind == JsonValueKind.Object &&
               action.TryGetProperty("actionParameters", out var actionParameters) &&
               actionParameters.ValueKind == JsonValueKind.Object &&
               actionParameters.TryGetProperty("parameters", out parameters) &&
               parameters.ValueKind == JsonValueKind.Object;
    }

    private static Boolean TryGetString(JsonElement element, String propertyName, out String value)
    {
        value = null;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               !String.IsNullOrWhiteSpace(value = property.GetString());
    }

    private static String GetParameter(JsonElement parameters, String name)
    {
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                parameter.Value.ValueKind == JsonValueKind.String)
            {
                return parameter.Value.GetString();
            }
        }

        return null;
    }

    private static Byte[] ReadCustomImage(String imagePath)
    {
        try
        {
            return !String.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath)
                ? File.ReadAllBytes(imagePath)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Byte[] WriteImage(Byte[] sourceBytes, String destinationPath, BadgePosition badgePosition)
    {
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using var sourceImage = Image.FromStream(sourceStream);
        using var canvas = new Bitmap(sourceImage.Width, sourceImage.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);

        graphics.Clear(Color.Black);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawImage(sourceImage, new Rectangle(0, 0, canvas.Width, canvas.Height));

        if (badgePosition != BadgePosition.None)
        {
            DrawBadge(graphics, canvas.Width, canvas.Height, badgePosition);
        }

        using var memoryStream = new MemoryStream();
        canvas.Save(memoryStream, ImageFormat.Png);
        var bytes = memoryStream.ToArray();

        // Avoid touching disk if the destination file already exists and is identical
        try
        {
            if (File.Exists(destinationPath))
            {
                var existingBytes = File.ReadAllBytes(destinationPath);
                if (existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    return bytes;
                }
            }
        }
        catch { }

        // Write safely to disk with retry and in-place fallback if the file is open by Loupedeck/Electron
        var written = false;
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            written = true;
        }
        catch (Exception)
        {
            // File.Move with overwrite fails on Windows if another process (e.g. Electron UI or Loupedeck)
            // holds a read-share handle on destinationPath. Fall back to direct in-place stream write.
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }

        if (!written)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush();
                    }
                    written = true;
                    break;
                }
                catch (Exception) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(30);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed writing asset image '{destinationPath}': {ex.Message}");
                }
            }
        }

        return bytes;
    }

    private static void DrawBadge(Graphics graphics, Int32 width, Int32 height, BadgePosition badgePosition)
    {
        if (badgePosition == BadgePosition.None)
        {
            return;
        }

        var size = BadgeGeometry.GetDiameter(width, height);
        var radius = size / 2f;
        var margin = BadgeGeometry.GetMargin(width, height);
        var centerX = badgePosition switch
        {
            BadgePosition.TopLeft or BadgePosition.MiddleLeft or BadgePosition.BottomLeft => radius + margin,
            BadgePosition.TopCenter or BadgePosition.Center or BadgePosition.BottomCenter => width / 2f,
            _ => width - radius - margin,
        };
        var centerY = badgePosition switch
        {
            BadgePosition.TopLeft or BadgePosition.TopCenter or BadgePosition.TopRight => radius + margin,
            BadgePosition.MiddleLeft or BadgePosition.Center or BadgePosition.MiddleRight => height / 2f,
            _ => height - radius - margin,
        };
        var bounds = new RectangleF(centerX - radius, centerY - radius, size, size);

        using var fill = new SolidBrush(Color.FromArgb(255, 36, 190, 70));
        using var border = new Pen(Color.FromArgb(255, 12, 90, 35), BadgeGeometry.GetBorderWidth(size));
        const Single inset = 1f;
        using var check = new Pen(Color.White, BadgeGeometry.GetCheckStroke(size)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        graphics.FillEllipse(fill, bounds);
        graphics.DrawEllipse(border, bounds);
        graphics.DrawLine(check, centerX - radius * .52f + inset, centerY, centerX - radius * .10f, centerY + radius * .42f - inset);
        graphics.DrawLine(check, centerX - radius * .10f, centerY + radius * .42f - inset, centerX + radius * .58f - inset, centerY - radius * .46f + inset);
    }

    /// <summary>
    /// Preserves, restores, or hydrates the editable non-destructive layer template (.ict and .layers.ict).
    /// Ensures element metadata (text, font size, color, icon, coordinates) is available to Loupedeck's
    /// Icon Editor so that:
    ///  1. The right panel is hydrated with text, font size, color, and icon properties (fixing Image 4).
    ///  2. The canvas renders distinct layer controls that can be selected and moved without moving
    ///     a flattened background image (fixing Images 1–3).
    ///  3. Loupedeck's active icon is never hijacked by a ___Active.ict file.
    /// </summary>
    internal static void PreserveAndHydrateLayerTemplate(String iconsDirectory, String profileActionName, String displayName)
    {
        if (String.IsNullOrWhiteSpace(iconsDirectory) ||
            !Directory.Exists(iconsDirectory) ||
            String.IsNullOrWhiteSpace(profileActionName))
        {
            return;
        }

        var layersBackupPath = Path.Combine(iconsDirectory, $"{profileActionName}.layers.ict");
        var inactiveIctPath  = Path.Combine(iconsDirectory, $"{profileActionName}___Inactive.ict");
        var defaultIctPath   = Path.Combine(iconsDirectory, $"{profileActionName}.ict");
        var activeIctPath    = Path.Combine(iconsDirectory, $"{profileActionName}___Active.ict");

        try
        {
            // CRITICAL RULE: Loupedeck must NEVER have a ___Active.ict file, because its presence
            // causes Loupedeck's renderer to ignore ___Active.png and suppress our dynamic badge.
            if (File.Exists(activeIctPath))
            {
                try { File.Delete(activeIctPath); } catch { }
            }

            // Check if Loupedeck has a valid ICT file on disk
            var candidatePath = File.Exists(inactiveIctPath) && IsValidLayerTemplate(inactiveIctPath) ? inactiveIctPath
                              : File.Exists(defaultIctPath)  && IsValidLayerTemplate(defaultIctPath)  ? defaultIctPath
                              : null;

            var candidateTimestamp = candidatePath != null
                ? File.GetLastWriteTimeUtc(candidatePath)
                : DateTime.MinValue;
            var backupTimestamp = File.Exists(layersBackupPath)
                ? File.GetLastWriteTimeUtc(layersBackupPath)
                : DateTime.MinValue;

            // Debounce: skip if no changes have occurred
            var effectiveTimestamp = candidateTimestamp > backupTimestamp ? candidateTimestamp : backupTimestamp;
            IctTimestampCache.TryGetValue(profileActionName, out var lastSeen);
            if (effectiveTimestamp != DateTime.MinValue && effectiveTimestamp == lastSeen)
            {
                return;
            }

            if (candidatePath != null)
            {
                // Loupedeck has updated or saved a valid template — update our backup sidecar
                if (!File.Exists(layersBackupPath) ||
                    candidateTimestamp > backupTimestamp ||
                    new FileInfo(candidatePath).Length != new FileInfo(layersBackupPath).Length)
                {
                    File.Copy(candidatePath, layersBackupPath, overwrite: true);
                }

                CopyIfDifferent(layersBackupPath, inactiveIctPath);
                CopyIfDifferent(layersBackupPath, defaultIctPath);
                IctTimestampCache[profileActionName] = File.GetLastWriteTimeUtc(layersBackupPath);
                return;
            }

            // If backup exists, restore it to Loupedeck's template paths
            if (File.Exists(layersBackupPath) && IsValidLayerTemplate(layersBackupPath))
            {
                CopyIfDifferent(layersBackupPath, inactiveIctPath);
                CopyIfDifferent(layersBackupPath, defaultIctPath);
                IctTimestampCache[profileActionName] = File.GetLastWriteTimeUtc(layersBackupPath);
                return;
            }

            // Neither exists: recover from an existing profile template matching this device's displayName,
            // or synthesize a clean layered template so the editor opens with editable layers.
            var layerJson = TryRecoverOrSynthesizeLayers(iconsDirectory, displayName);
            if (!String.IsNullOrWhiteSpace(layerJson))
            {
                File.WriteAllText(layersBackupPath, layerJson);
                File.WriteAllText(inactiveIctPath, layerJson);
                File.WriteAllText(defaultIctPath, layerJson);
                IctTimestampCache[profileActionName] = File.GetLastWriteTimeUtc(layersBackupPath);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"Failed to preserve/hydrate layer template for '{profileActionName}'.");
        }
    }

    private static String TryRecoverOrSynthesizeLayers(String iconsDirectory, String displayName)
    {
        // 1. Search for existing .ict files in ActionIcons that match this displayName
        if (!String.IsNullOrWhiteSpace(displayName) && Directory.Exists(iconsDirectory))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(iconsDirectory, "*.ict"))
                {
                    if (file.EndsWith(".layers.ict", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith("___Active.ict", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsValidLayerTemplate(file))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    if (text.IndexOf($"\"{displayName}\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return text;
                    }
                }
            }
            catch { }
        }

        // 2. Synthesize a clean default layered template (background + text layer + image placeholder)
        return SynthesizeDefaultLayerTemplate(displayName);
    }

    private static String SynthesizeDefaultLayerTemplate(String displayName)
    {
        var text = String.IsNullOrWhiteSpace(displayName) ? String.Empty : displayName.Trim();
        return @"{
  ""backgroundColor"": 4278190080,
  ""items"": [
    {
      ""image"": """",
      ""imageFileName"": null,
      ""imageColor"": 4294967295,
      ""imageRotation"": ""None"",
      ""isVisible"": true,
      ""itemType"": ""Image"",
      ""area"": {
        ""x"": 0,
        ""y"": 0,
        ""width"": 100,
        ""height"": 78
      }
    },
    {
      ""text"": """ + EscapeJson(text) + @""",
      ""originalText"": null,
      ""textColor"": 4294967295,
      ""fontSize"": 4,
      ""fontName"": ""Arial"",
      ""isVisible"": true,
      ""itemType"": ""Text"",
      ""area"": {
        ""x"": 0,
        ""y"": 78,
        ""width"": 100,
        ""height"": 22
      }
    }
  ]
}";
    }

    private static String EscapeJson(String value)
    {
        if (String.IsNullOrEmpty(value))
        {
            return String.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static Boolean IsValidLayerTemplate(String filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var content = File.ReadAllText(filePath);
            if (String.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array &&
                   items.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyIfDifferent(String sourcePath, String destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            if (File.Exists(destinationPath))
            {
                var sourceInfo = new FileInfo(sourcePath);
                var destInfo = new FileInfo(destinationPath);
                if (sourceInfo.Length == destInfo.Length &&
                    String.Equals(File.ReadAllText(sourcePath), File.ReadAllText(destinationPath), StringComparison.Ordinal))
                {
                    return;
                }
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"Failed to copy '{sourcePath}' to '{destinationPath}'.");
        }
    }

    private static String CreateImageLookupKey(EDataFlow dataFlow, String deviceKey, BadgePosition badgePosition)
        => $"{dataFlow}:{deviceKey}:{badgePosition}";

    private static Boolean UpdateActionImageSignature(String actionIdentity, String signature)
    {
        lock (ActionImagesLock)
        {
            if (!ActionImageSignatures.TryGetValue(actionIdentity, out var existing))
            {
                ActionImageSignatures.Add(actionIdentity, signature);
                return true;
            }

            if (String.Equals(existing, signature, StringComparison.Ordinal))
            {
                return false;
            }

            ActionImageSignatures[actionIdentity] = signature;
            return true;
        }
    }

    private static String GetImageFileStamp(String imagePath)
    {
        try
        {
            var file = new FileInfo(imagePath);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-";
        }
        catch
        {
            return "?";
        }
    }
}
