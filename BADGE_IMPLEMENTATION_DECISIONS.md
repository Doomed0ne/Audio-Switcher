# Active Device Badge Decisions

## Scope

This document covers only the green selected-device badge on saved `Audio Input`
and `Audio Output` actions. It does not change or evaluate Volume Mixer, dials,
or cycle actions.

## What Loupedeck Actually Does

When an action is saved and placed on a profile, Loupedeck turns it into a
generic profile action. The custom image created in the editor is stored beside
the profile as an `ActionIcons/*.ict` definition and cached as
`ActionImages/*___Inactive.png` and `ActionImages/*___Active.png`.

`SetCurrentState` and `ActionImageChanged` correctly tell the SDK which device
is selected, but the editor can retain a previously cached generic active image.
That is why the first version could add a checkmark but could not reliably
remove it from the previous device.

The working solution updates the cache as well as the SDK state:

1. Read the Windows default input or output device.
2. Find saved Audio Switcher actions in every Loupedeck profile.
3. Read their editor artwork from the `.ict` image definition.
4. Write an unbadged inactive image for every matching action.
5. Write an active image with the badge only for the action assigned to the
   current Windows device. Every other active image is deliberately unbadged.
6. Request a normal SDK image refresh.

The last two steps make the result reliable even when Loupedeck temporarily
continues to display a stale active state.

## Keep: Required For Reliable Badges

| Code | Why it stays |
| --- | --- |
| `AudioSwitchCommandBase` inheriting `MultistateActionEditorCommand` | Defines inactive/active states and tells Loupedeck the state that matches Windows. |
| `Device` selector | Associates each saved tile with the Windows endpoint it should select and later recognize. |
| `BadgePosition` selector and enum | Controls where the automatic badge is placed; `none` intentionally leaves fully manual two-state artwork alone. |
| `RunCommand` device switch plus `SetCurrentState` | Changes the Windows default device immediately and refreshes the pressed action. |
| The one-second refresh timer | Keeps badges correct after the default is changed outside this plugin, for example in Windows settings. |
| Startup refresh retry | Waits briefly for Loupedeck's saved profile artwork to become available, then stamps the current Windows device without requiring a button press. |
| `TryNotifyImageChanged` | Requests the screen update and safely retries after the SDK's occasional profile-reload null reference. |
| `ProfileImageBadgeSynchronizer` | The essential compatibility layer for saved generic profile actions and editor-created artwork. |
| Reading `.ict` editor images | Preserves artwork made with the Loupedeck editor. |
| Writing both `Active.png` and `Inactive.png` | The essential stale-checkmark fix. Non-selected tiles must have both cache files unbadged. |
| `System.Drawing.Common` and the `Graphics` badge renderer | Needed to compose the saved profile PNGs. |

## Keep, But It Is A Fallback

| Code | Purpose | Recommendation |
| --- | --- | --- |
| `AudioDeviceSwitchImage.BuildCommandButtonImage` | Produces a readable black/text tile or SDK-provided image when no saved editor artwork is available. | Keep for now. It is small and protects unsaved or unusual actions. |
| `AudioDeviceSwitchImage.DrawCheckBadge` | Draws a badge in the SDK-generated fallback image. | Keep with the fallback renderer. It uses the same one-fifth tile size as the cache renderer. |
| `ReadCustomImage(inactiveImagePath)` fallback | Lets existing cached art remain usable when an `.ict` source cannot be read. | Keep. It prevents an unnecessary blank tile. |
| Scanning every Loupedeck profile | Makes the feature work in Basic and other profiles, not just the currently open profile. | Keep unless performance becomes a real issue. It runs on load and only after a device change, not continuously every second. |

## Removed In 1.2.20

The `BadgeImagePath` file selector and its file-reading helpers have been
removed. Tile artwork is now intentionally created in the Loupedeck editor and
read from the saved `.ict` profile definition.

The plugin-start reflection dump and per-image verbose logging have also been
removed. They were diagnostic-only and could create hundreds of log entries
during profile startup.

## Optional: Candidate Removal After More Editor Testing

| Code | Why it is optional |
| --- | --- |
| `base.GetCommandImage(...)` lookup in `GetCommandImage` | It is a harmless fallback, but generic saved editor actions return `null` here. The synchronizer, not this lookup, is what preserves custom artwork. |
| Verbose `GetCommandImage` logging | Useful while diagnosing image/state behavior; noisy once the feature has proven stable. |
| Compatibility overloads `DrawCheckBadge()` and `DrawCheckBadgeBottomRight()` | They are only useful if other code still calls them. Verify with a search before deleting. |

## Remove: Debug-Only Work

`AudioSwitcherPlugin` currently reflects every property and method on
`ActionEditorCommand` when the plugin starts and writes the result to the log.
That was useful for investigating the SDK, but is not needed by any runtime
feature. It can be removed now without affecting badges, switching, or Volume
Mixer.

Keep normal error and switch logs. Remove only the constructor reflection block
and its associated `try`/`catch`.

## Recommended Cleanup Order

1. Keep the working badge path unchanged through normal use, including a
   Windows-settings device change and both input/output switches.
2. Remove the reflection dump in `AudioSwitcherPlugin` first. It is pure debug
   noise and has no behavior value.
3. Leave the synchronizer, timer, multistate logic, and cached active/inactive
   image generation in place. Removing any of these reintroduces the old stale
   checkmark problem.
4. Only then consider trimming fallback rendering or compatibility overloads.

## Verification Checklist

- Switching from Speakers to BlackShark leaves one small green badge on
  BlackShark and none on Speakers.
- Switching back moves the badge to Speakers.
- Changing the default output in Windows moves the badge within about one
  second.
- The same behavior applies independently to saved input actions.
- Volume Mixer actions remain unchanged.

## Current Status

Version `1.2.20` uses a 35 px badge with a 12 px edge inset on a 192 px tile.
During startup it retries until it has refreshed saved profile artwork, rather
than relying on the first plugin-load timing. The current artwork is always
read from the editor definition before any cached backup, so later editor image
changes remain visible.
