namespace Loupedeck.AudioSwitcherPlugin;

using System;

internal static class BadgeGeometry
{
    private const Double DiameterRatio = 0.20;
    private const Double MarginRatio = 0.125;

    public static Int32 GetDiameter(Int32 width, Int32 height) =>
        Math.Max(16, (Int32)Math.Round(
            Math.Min(width, height) * DiameterRatio,
            MidpointRounding.AwayFromZero));

    public static Single GetMargin(Int32 width, Int32 height) =>
        Math.Max(10f, (Single)Math.Round(
            Math.Min(width, height) * MarginRatio,
            MidpointRounding.AwayFromZero));

    public static Single GetBorderWidth(Int32 diameter) => Math.Max(2f, (Single)Math.Round(diameter / 14f, 1));

    public static Single GetCheckStroke(Int32 diameter) => Math.Max(2.3f, (Single)Math.Round(diameter / 7f, 1));
}
