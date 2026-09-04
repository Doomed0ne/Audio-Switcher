namespace Loupedeck.AudioSwitcherPlugin;

using System;

/// <summary>
/// 3x3 square matrix positions for stamping the active-device check badge.
/// </summary>
public enum BadgePosition
{
    TopRight,      // Default
    TopCenter,
    TopLeft,
    MiddleRight,
    Center,
    MiddleLeft,
    BottomRight,   // Recommended for text-on-top layout
    BottomCenter,
    BottomLeft,
    None,          // Full manual 2-state icon (no automatic stamp)
}

internal static class AudioDeviceSwitchImage
{
    // ── Badge Drawer ─────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a solid green circle checkmark badge at the specified 3x3 matrix position.
    /// </summary>
    public static void DrawCheckBadge(BitmapBuilder builder, BadgePosition position)
    {
        if (position == BadgePosition.None)
        {
            return;
        }

        var badgeSize = BadgeGeometry.GetDiameter(builder.Width, builder.Height);
        var radius = badgeSize / 2.0f;
        var margin = BadgeGeometry.GetMargin(builder.Width, builder.Height);

        // Calculate X coordinate
        float centerX = position switch
        {
            BadgePosition.TopLeft or BadgePosition.MiddleLeft or BadgePosition.BottomLeft
                => radius + margin,
            BadgePosition.TopCenter or BadgePosition.Center or BadgePosition.BottomCenter
                => builder.Width / 2.0f,
            _ // TopRight, MiddleRight, BottomRight (default)
                => builder.Width - radius - margin
        };

        // Calculate Y coordinate
        float centerY = position switch
        {
            BadgePosition.TopLeft or BadgePosition.TopCenter or BadgePosition.TopRight
                => radius + margin,
            BadgePosition.MiddleLeft or BadgePosition.Center or BadgePosition.MiddleRight
                => builder.Height / 2.0f,
            _ // BottomLeft, BottomCenter, BottomRight
                => builder.Height - radius - margin
        };

        // Draw badge circle
        builder.FillCircle(centerX, centerY, radius, new BitmapColor(36, 190, 70));
        builder.DrawCircle(centerX, centerY, radius, new BitmapColor(12, 90, 35));

        // Draw a slightly inset tick so it has clear space inside the badge.
        var stroke = BadgeGeometry.GetCheckStroke(badgeSize);
        const Single inset = 1f;

        // Left leg
        builder.DrawLine(
            centerX - radius * 0.52f + inset,
            centerY,
            centerX - radius * 0.10f,
            centerY + radius * 0.42f - inset,
            BitmapColor.White,
            stroke);

        // Right leg
        builder.DrawLine(
            centerX - radius * 0.10f,
            centerY + radius * 0.42f - inset,
            centerX + radius * 0.58f - inset,
            centerY - radius * 0.46f + inset,
            BitmapColor.White,
            stroke);
    }

    /// <summary>
    /// Backward-compatible overload for Top-Right placement.
    /// </summary>
    public static void DrawCheckBadge(BitmapBuilder builder)
        => DrawCheckBadge(builder, BadgePosition.TopRight);

    /// <summary>
    /// Backward-compatible helper for Bottom-Right placement.
    /// </summary>
    public static void DrawCheckBadgeBottomRight(BitmapBuilder builder)
        => DrawCheckBadge(builder, BadgePosition.BottomRight);

    // ── Command Button Image Builder ─────────────────────────────────────────

    /// <summary>
    /// Builds the composite button image for a given state (0 = Inactive, 1 = Active).
    /// </summary>
    public static BitmapImage BuildCommandButtonImage(
        String displayName,
        Int32 stateIndex,
        BitmapImage baseImage,
        Byte[] baseImageBytes,
        Int32 width,
        Int32 height,
        BadgePosition badgePosition = BadgePosition.TopRight)
    {
        if (baseImageBytes == null && baseImage == null)
        {
            return null;
        }

        using var builder = new BitmapBuilder(width, height);
        builder.Clear(BitmapColor.Black);

        if (baseImageBytes?.Length > 0)
        {
            builder.DrawImage(baseImageBytes, 0, 0, width, height, BitmapRotation.None);
        }
        else if (baseImage != null)
        {
            builder.DrawImage(baseImage, 0, 0, width, height, BitmapRotation.None);
        }

        // State 1 is Active — stamp checkmark badge if position is not None
        if (stateIndex == 1 && badgePosition != BadgePosition.None)
        {
            DrawCheckBadge(builder, badgePosition);
        }

        return builder.ToImage();
    }
}
