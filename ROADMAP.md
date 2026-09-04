# Audio Switcher Plugin — Roadmap & Architecture Plan

---

## 1. Executive Summary & Vision

The Audio Switcher plugin allows users to configure audio output and input device buttons on Loupedeck devices with:
1. **100% Native Image Editor Art & Wording**: The icon, text wording, font size, layout, and colors come directly from Loupedeck's built-in **Image Editor** ("Edit icon" window). The plugin never synthesizes or alters the artwork, and never auto-injects text from the `Name *` field.
2. **Clean Icon Editor State (Zero Embedded Badges)**: Base artwork (`___Inactive.png`) is owned exclusively by Loupedeck and is **never modified or stamped** by the plugin. Opening the Loupedeck Image Editor will always display pristine, unbadged artwork.
3. **Automatic Startup Checkmark Detection**: On Windows / Loupedeck boot, the plugin immediately detects the live default audio devices and displays the checkmark badge on the active buttons without requiring any button press.
4. **Dynamic Device Switching**: When switching devices, the checkmark badge moves instantaneously to the new device and is completely removed from all other devices of that dataflow.
5. **Exact 1.2.16 Badge Metrics**: Green circular badge with 16px diameter, 10px breathing room margin, and 2.3px check stroke on the 80x80 canvas, placed according to the 3x3 selector.

---

## 2. Lessons Learned & Root Cause Analysis

### Lesson A: Why Version .26 Went the Wrong Direction
- In .26, the plugin attempted to manually parse `.ict` JSON files and re-render text using GDI+ `DrawString` with a hardcoded Arial font and white color.
- **Why this failed**: Loupedeck's built-in Image Editor already renders the user's chosen icon, text wording, font, size, and custom color into `___Inactive.png`. Trying to reconstruct and draw text in C# stripped out the user's custom color choices and font styling.
- **Rule**: The plugin must **never** draw text or parse `.ict` to generate button images. The rendered image from Loupedeck's Image Editor (`___Inactive.png`) is the sole authoritative visual source.

### Lesson B: Why Badges Were Not Disappearing (v1.2.24)
- In .24, `ProfileImageBadgeSynchronizer` wrote badged bitmaps directly into `___Inactive.png` and `___Source.png`. When switching devices, the synchronizer read `___Inactive.png` as its source—reading pixels that already had the checkmark baked in.
- **Rule**: `___Inactive.png` must **NEVER** receive a checkmark badge. In fact, `___Inactive.png` should never be written to or modified by the plugin at all.

### Lesson C: Why the `Name` Field Was Clashing with Editor Text
- In Loupedeck SDK, `GetCommandDisplayName` defaults to returning the action's `Name *` from the bottom-right Action Editor panel.
- Loupedeck draws this display name over the button, creating duplicate or misplaced text that clashes with the wording already designed in the Image Editor.
- **Rule**: Override `GetCommandDisplayName` to return `String.Empty` so Loupedeck displays only the Image Editor artwork.

---

## 3. Architecture & Data Flow

```
+-------------------------------------------------------------+
|               Loupedeck Image Editor (User UI)              |
|  - User picks icon & types custom wording with custom color  |
|  - Loupedeck renders and saves to:                          |
|      ActionImages/{actionName}___Inactive.png (Clean Master)|
+-------------------------------------------------------------+
                              │
                              │ (Read-Only by Plugin)
                              ▼
+-------------------------------------------------------------+
|            Audio Switcher Plugin Synchronization             |
|                                                             |
|  1. Inactive State (___Inactive.png, state 0):               |
|     - Left 100% UNTOUCHED. Pristine artwork for Editor.     |
|                                                             |
|  2. Active State (___Active.png, state 1):                   |
|     - If Live Default Device == True:                       |
|         Overlays 1.2.16 Checkmark Badge at BadgePosition    |
|     - If Live Default Device == False:                      |
|         Writes Clean Copy (BadgePosition.None, no badge)    |
|                                                             |
|  3. Display Name:                                           |
|     - GetCommandDisplayName => String.Empty                 |
|       (Suppresses auto-generated text from Name field)      |
+-------------------------------------------------------------+
                              │
                              │ ActionImageChanged() & SetCurrentState()
                              ▼
+-------------------------------------------------------------+
|             Loupedeck Hardware & Software Canvas            |
|  - Active device: Displays State 1 (Editor Art + Checkmark)  |
|  - Inactive device: Displays State 0 (Editor Art Clean)     |
+-------------------------------------------------------------+
```

---

## 4. Deliverables & Specifications

### 1. Icon & Wording from Image Editor
- Authoritative source: `ActionImages/{actionName}___Inactive.png`.
- Contains the user's custom icon, custom wording, custom font size, and custom color exactly as seen in the Image Editor.
- The plugin reads this image and does not modify its content.

### 2. No Embedded Badge in Image Editor
- `___Inactive.png` is never stamped by the plugin.
- Opening the Loupedeck Image Editor shows the "Unselected" state completely clean without any checkmark badge.

### 3. Startup Checkmark Detection
- On `OnLoad`, an active 250ms warmup timer polls Windows CoreAudio for up to 10 seconds.
- As soon as the default device endpoint is acquired:
  - `ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, defaultKey)` writes `___Active.png` with the badge for the default device, and clean for inactive devices.
  - Loupedeck's state is synchronized via `SetCurrentState(actionParameters, isActive ? 1 : 0)`.
  - Loupedeck is notified via `ActionImageChanged()`.
  - The checkmark badge immediately appears on the active default devices on launch.

### 4. Device Switching
- When the user presses a device button:
  - Default endpoint switches via CoreAudio.
  - Newly active device gets `___Active.png` with badge (`SetCurrentState(new, 1)`).
  - Previous device gets `___Active.png` clean (`SetCurrentState(old, 0)`).
  - `ActionImageChanged()` fires.
  - The checkmark badge moves instantaneously.

### 5. Proven Badge Geometry (1.2.16)
- Diameter: `16px` on 80x80 canvas (20% ratio).
- Margin: `10px` breathing room from edges (12.5% ratio).
- Check Stroke: `2.3px` with 1px inset.
- Position: Configurable 3x3 matrix (Top-Right default).

---

## 5. File Changes & Responsibilities

| File | Responsibility |
| :--- | :--- |
| `src/Actions/ProfileImageBadgeSynchronizer.cs` | Reads `___Inactive.png` as read-only source. **Never writes to `___Inactive.png`**. Writes `___Active.png` and `___state0001.png` badged if active default, or clean if inactive. Completely removes `.ict` parsing. |
| `src/Actions/AudioSwitchCommandBase.cs` | Overrides `GetCommandDisplayName => String.Empty` to suppress `Name` auto-text. Runs startup warmup timer (250ms x 40). Synchronizes `SetCurrentState` during `GetCommandImage` and on device switch. |
| `src/Actions/AudioDeviceSwitchImage.cs` | Composites `baseImage` with 1.2.16 badge geometry for State 1. Does not draw any text over `baseImage`. |
| `src/Actions/BadgeGeometry.cs` | Maintains exact 1.2.16 metrics (16px diameter, 10px margin on 80x80 canvas). |

---

## 6. Acceptance Criteria

- [ ] When an icon is created/edited in Loupedeck's Image Editor, its icon and custom wording (in whatever color selected) appear on the button.
- [ ] No unwanted auto-text from the `Name *` field appears on the button image.
- [ ] In the Image Editor, the "Unselected" state is completely clean (no embedded checkmark).
- [ ] On app / Windows startup, the currently active audio output and input devices display the checkmark badge automatically.
- [ ] Clicking to switch devices moves the checkmark badge to the new device and removes it from the old device.
- [ ] Badge diameter is 16px with 10px breathing room from the border.

---

*Target Version: 1.2.27*
*Last updated: 2026-09-03*
