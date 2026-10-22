# Badge and Icon Editor Decisions

## Icon Editor source

The user-level `ActionIcons/<profile-action>.ict` template is the editable Icon Editor source. Keep it authoritative and preserve it byte-for-byte. Do not create `.layers.ict` sidecars inside `ActionIcons`; they are not an SDK template level and may interfere with the editor's template discovery.

The plugin does not edit `ActionImages` files. In particular, `___Inactive.png` remains owned by Logi's Icon Editor. Automatic badge pixels are composed into the `BitmapImage` returned by `GetCommandImage`; they are not saved into the user's artwork or state cache files.

When `BadgePosition` is `None`, use the authored active-state image for manual two-state artwork. With an automatic badge position, use the clean inactive image as the base and draw the indicator at render time.

## Startup and state

1. `AudioDeviceManager` reads the current Windows endpoint.
2. Each `AudioSwitchCommandBase` instance synchronizes saved action states when it loads and retries during a 250 ms warm-up window.
3. The profile synchronizer calls `SetCurrentState` for saved actions and requests a redraw through `ActionImageChanged`.
4. The command image callback returns the authored image with a transient badge only for the current device.

The refresh callback is guarded against overlap. The warm-up runs for its full window because discovering an action in one profile does not prove that every profile has loaded.

## Supported boundaries

- The Logi SDK exposes button rendering through `GetCommandImage` and redraw requests through `ActionImageChanged`.
- Icon templates are `.ict` files. The SDK documents user-level templates in `ActionIcons`, plugin action templates in `icontemplates`, and the plugin-level `metadata/DefaultIconTemplate.ict` fallback.
- The SDK documentation does not describe plugin callbacks for moving Icon Editor selection handles, hydrating its inspector fields, or rendering a separate notification popup. Those behaviors cannot be fixed through the command image callback alone.
- If a user's original `.ict` has already lost its editable layers, recover it from a genuine exported Icon Template. Do not synthesize user text from the profile action's display name or copy another action's template.

## Verification checklist

- Reopen the Icon Editor and confirm the original text, font, color, icon tint, and item bounds remain editable.
- Move and resize a text layer; confirm its pixels move with its bounds after reopening the editor.
- Restart Logi Plugin Service with a known default input and output. Confirm each matching tile is active without pressing it.
- Switch devices in the plugin and in Windows Settings. Confirm the badge moves and other actions redraw without changing their source PNG or `.ict` files.
- Set `BadgePosition` to `None` and confirm the authored two-state images remain intact.
- Check notification artwork separately in Logi's UI; the plugin contains no separate popup renderer.
