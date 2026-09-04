namespace Loupedeck.AudioSwitcherPlugin;

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

internal static class ProfileImageBadgeSynchronizer
{
    private const String OutputActionName = "$AudioSwitcher___Loupedeck.AudioSwitcherPlugin.SwitchAudioOutputCommand";
    private const String InputActionName = "$AudioSwitcher___Loupedeck.AudioSwitcherPlugin.SwitchAudioInputCommand";

    public static Boolean Synchronize(EDataFlow dataFlow, String currentDeviceKey)
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
        var synchronized = false;

        try
        {
            foreach (var profilePath in Directory.EnumerateFiles(profilesRoot, "ProfileInfo.json", SearchOption.AllDirectories))
            {
                synchronized |= SynchronizeProfile(profilePath, actionName, currentDeviceKey);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Could not synchronize editor badge images.");
        }

        return synchronized;
    }

    private static Boolean SynchronizeProfile(String profilePath, String actionName, String currentDeviceKey)
    {
        var synchronized = false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (!document.RootElement.TryGetProperty("profileActions", out var profileActions) ||
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
            if (!Directory.Exists(imagesDirectory))
            {
                return false;
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

                var badgePosition = ParseBadgePosition(GetParameter(parameters, "badgePosition"));
                if (badgePosition == BadgePosition.None)
                {
                    continue;
                }

                var deviceKey = GetParameter(parameters, "device");
                var isCurrentDevice = !String.IsNullOrWhiteSpace(currentDeviceKey) &&
                                      String.Equals(deviceKey, currentDeviceKey, StringComparison.OrdinalIgnoreCase);

                var inactiveImagePath = Path.Combine(imagesDirectory, $"{profileActionName}___Inactive.png");
                var activeImagePath = Path.Combine(imagesDirectory, $"{profileActionName}___Active.png");
                var defaultImagePath = Path.Combine(imagesDirectory, $"{profileActionName}.png");
                var state0Path = Path.Combine(imagesDirectory, $"{profileActionName}___state0000.png");
                var state1Path = Path.Combine(imagesDirectory, $"{profileActionName}___state0001.png");

                // Read the exact composite image created by Loupedeck's Icon Editor (with user's custom icon, text, colors)
                var sourceBytes = ReadCustomImage(inactiveImagePath)
                               ?? ReadCustomImage(defaultImagePath)
                               ?? ReadCustomImage(state0Path);

                if (sourceBytes == null)
                {
                    continue;
                }

                // Loupedeck's Image Editor owns inactiveImagePath and state0Path.
                // We NEVER touch or rewrite them, preserving the user's saved state artwork,
                // custom wording, font, and colors permanently on disk.

                // State 1 (Active) receives the badge only when this device is the active default.
                // Inactive devices receive the clean unbadged artwork.
                var activeBadge = isCurrentDevice ? badgePosition : BadgePosition.None;
                WriteImage(sourceBytes, activeImagePath, activeBadge);
                WriteImage(sourceBytes, state1Path, activeBadge);
                WriteImage(sourceBytes, defaultImagePath, activeBadge);

                synchronized = true;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"Could not synchronize editor badge images for '{profilePath}'.");
        }

        return synchronized;
    }

    private static Boolean TryGetParameters(JsonElement action, out JsonElement parameters)
    {
        parameters = default;
        return action.TryGetProperty("actionParameters", out var actionParameters) &&
               actionParameters.TryGetProperty("parameters", out parameters) &&
               parameters.ValueKind == JsonValueKind.Object;
    }

    private static Boolean TryGetString(JsonElement element, String propertyName, out String value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out var property) &&
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

    private static void WriteImage(Byte[] sourceBytes, String destinationPath, BadgePosition badgePosition)
    {
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using var sourceImage = Image.FromStream(sourceStream);
        using var canvas = new Bitmap(sourceImage.Width, sourceImage.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);

        graphics.Clear(Color.Black);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawImage(sourceImage, new Rectangle(0, 0, canvas.Width, canvas.Height));

        DrawBadge(graphics, canvas.Width, canvas.Height, badgePosition);

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            canvas.Save(temporaryPath, ImageFormat.Png);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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

    private static BadgePosition ParseBadgePosition(String value) => value?.ToLowerInvariant() switch
    {
        "top-center" => BadgePosition.TopCenter,
        "top-left" => BadgePosition.TopLeft,
        "middle-right" => BadgePosition.MiddleRight,
        "center" => BadgePosition.Center,
        "middle-left" => BadgePosition.MiddleLeft,
        "bottom-right" => BadgePosition.BottomRight,
        "bottom-center" => BadgePosition.BottomCenter,
        "bottom-left" => BadgePosition.BottomLeft,
        "none" => BadgePosition.None,
        _ => BadgePosition.TopRight,
    };
}
