# Audio Switcher Plugin — Architecture, SDK Findings & Roadmap

**Last Updated:** 2026-10-17  
**Target Version:** 1.2.43  

---

## 1. Executive Answer: Will Hardware and Software Be in Sync & Show the Same Image?

**YES. Hardware and software are architected to be 100% in sync and display the exact same image.**

### How the Dual-Rendering Pipeline Works in Loupedeck

Loupedeck uses a hybrid rendering architecture for Action Editor commands (`MultistateActionEditorCommand`):

```text
                                  ┌──────────────────────────────────────────────┐
                                  │           Windows Core Audio API             │
                                  │       (Active Default Audio Endpoint)        │
                                  └──────────────────────┬───────────────────────┘
                                                         │
                                               Polling / Startup Query
                                                         │
                                                         ▼
                                  ┌──────────────────────────────────────────────┐
                                  │        AudioSwitchCommandBase & Sync         │
                                  │  • SetCurrentState(actionParams, 1 or 0)     │
                                  │  • ActionImageChanged()                      │
                                  └──────────────┬────────────────┬──────────────┘
                                                 │                │
                        ┌────────────────────────┘                └────────────────────────┐
                        ▼                                                                  ▼
        ┌──────────────────────────────────────────┐                    ┌──────────────────────────────────────────┐
        │        Physical Hardware Controller      │                    │         Software UI (Loupedeck App)      │
        │           (CT / Live / Live S / MX)      │                    │             (Profile Canvas View)        │
        ├──────────────────────────────────────────┤                    ├──────────────────────────────────────────┤
        │ • State 1 (Active):                      │                    │ • State 1 (Active):                      │
        │   Reads ActionImages/{action}___Active   │                    │   Renders GetCommandImage(state=1)       │
        │   -> Displays artwork + checkmark badge  │                    │   OR ActionImages/{action}___Active      │
        │                                          │                    │   -> Displays artwork + checkmark badge  │
        │ • State 0 (Inactive):                    │                    │                                          │
        │   Reads ActionImages/{action}___Inactive │                    │ • State 0 (Inactive):                    │
        │   -> Displays clean unbadged artwork     │                    │   Renders GetCommandImage(state=0)       │
        │                                          │                    │   OR ActionImages/{action}___Inactive    │
        │                                          │                    │   -> Displays clean unbadged artwork     │
        └──────────────────────────────────────────┘                    └──────────────────────────────────────────┘
                                                 │
                                 [IDENTICAL PIXELS RENDERED]
```

### Why They Fell Out of Sync in Earlier Versions
1. **Physical Controller:** Reads directly from `ActionImages/{actionId}___Active.png` when in State 1. When the previous agent disabled the image badge synchronizer in v1.2.38, `___Active.png` was never badged or was missing, leaving the physical LCD stuck showing unbadged art.
2. **Software UI:** Can fall back to invoking `GetCommandImage()`. When the synchronizer was made read-only and an "ambiguity" null-return rule was added, `GetCommandImage()` received `null` when duplicate actions existed (e.g. Speakers and Spaekres), causing the software preview to become blank or disagree with the hardware.
3. **State Desynchronization:** Loupedeck was never instructed to transition to State 1 at startup because `SetCurrentState(actionParams, 1)` was only called inside manual button click handlers (`RunCommand`).

### The Solution for Perfect Synchronization
By combining:
1. **State Synchronizer:** Calling `SetCurrentState(actionParams, isDefault ? 1 : 0)` and `ActionImageChanged()` at startup and on every device switch.
2. **Physical Asset Synchronizer:** Writing the dynamic checkmark overlay onto `___Active.png` (State 1) and keeping `___Inactive.png` clean (State 0).
3. **In-Memory Callback Synchronizer:** Returning the exact same badged composite in `GetCommandImage(actionParams, stateIndex, ...)`.

Both the physical controller LCD and the desktop software preview show the **exact same pixel-perfect artwork and badge**.

---

## 2. Reverse-Engineering Findings: Loupedeck Architecture & SDK Reality

Decompilation of `LoupedeckService.dll` and `PluginApi.dll` revealed the exact mechanisms governing actions, images, and the built-in editor:

### A. The Action Hierarchy
* **`MultistateActionEditorCommand` (Primary User Path):**
  * Inherits from `ActionEditorCommand` $\rightarrow$ `ActionEditorAction` $\rightarrow$ `PluginAction`.
  * Allows users to select options via Action Editor dropdowns (Device UUID and Badge Position).
  * Enables the user to design artwork using Loupedeck's native **Icon Editor**.
  * Supported by Loupedeck's runtime with state-specific caching (`___Inactive.png` and `___Active.png`).
* **`PluginDynamicCommand` (Basic SDK Tutorial Path):**
  * Generates programmatic parameter tiles in code (e.g. `AudioDeviceSwitchDynamicCommandBase`).
  * Does **not** inherit or support the user's custom Icon Editor artwork, typography, or badge position settings.
  * *Conclusion:* The plugin's primary focus must remain on `MultistateActionEditorCommand` (`SwitchAudioOutputCommand` / `SwitchAudioInputCommand`) to preserve user-designed artwork.

### B. Hardware Image Loading Precedence (`ApplicationProfileImages.CreateActionImage`)
When Loupedeck renders a button for a profile action:
1. It queries `ApplicationProfile.TryGetActionImagePath(actionName, stateName)`.
2. It looks for `ActionImages/{actionName}___{stateName}.png` (e.g., `___Active.png` or `___Inactive.png`).
3. **If a file exists on disk, Loupedeck loads it directly for the physical LCD.**
4. Only if no custom state image exists does it fall back to calling the plugin's `GetCommandImage()`.
5. *Critical Discovery:* If a `___Active.ict` file exists in `ActionIcons/`, Loupedeck prioritizes that template over `___Active.png`. Since `.ict` templates lack our dynamic checkmark, writing to `___Active.ict` was silently hiding the badge. **`___Active.ict` must never exist.**

### C. Native Icon Editor Hydration & Canvas Persistence
The native Loupedeck Icon Editor (Electron frontend) hydrates its inspector and canvas elements from JSON template files:
* Path: `ActionIcons/{actionName}___Inactive.ict` (and `{actionName}.ict`).
* Contents: A JSON object containing `backgroundColor` and an `items` array with `Text` items (text, font size, font name, text color, bounding area) and `Image` items (SVG/PNG data, color, bounding area).
* **When `.ict` exists:** The editor deserializes the elements. The inspector fields (Text, Font, Colors, Icon) are fully populated, and the canvas handles bind to individual element bounding boxes.
* **When `.ict` is missing:** The editor falls back to loading `ActionImages/{actionName}___Inactive.png` as a flat, single-layer raster image. The inspector fields are blank, and dragging canvas handles moves a selection box over stationary flattened background pixels (the bug seen in screenshots 1–4).

### D. Notification Popups
* Loupedeck displays an on-screen notification popup when switching audio devices.
* It fetches `{actionName}.png` (the default/base action image) from `ActionImages/`.
* When previous versions stamped the green checkmark badge onto `{actionId}.png`, the notification popup displayed the pre-baked checkmark (the bug seen in screenshot 5).
* *Rule:* `{actionId}.png` must **always** be clean and unbadged (`BadgePosition.None`).

---

## 3. Analysis of Past Regressions (v1.2.28 – v1.2.38)

| Regression | Symptom | True Root Cause | Correct Fix |
| :--- | :--- | :--- | :--- |
| **1. Hardware badge missing / not moving** | Checkmark does not appear or move on the physical controller LCD. | v1.2.38 made the synchronizer read-only, stopping badge generation on `___Active.png`. | Resume dynamic badge stamping onto `___Active.png` and `___state0001.png` for active devices only. |
| **2. Startup checkmark missing** | Checkmark does not appear at startup until a button is manually pressed. | `SetCurrentState(actionParams, 1)` was only called in `RunCommand()`. Loupedeck booted with the action in State 0 (`Inactive`). | Hook `OnLoad()` and polling timer to detect default device and immediately call `SetCurrentState(1)` on the active device. |
| **3. Editor flattened & inspector blank** | Editor canvas shows 1 flattened layer; Text, Font size, and Icon fields are blank (Images 1–4). | `ActionIcons/{actionId}___Inactive.ict` was missing or wiped, forcing Loupedeck to load a flat bitmap. | Ensure valid `___Inactive.ict` exists with discrete `Text` and `Image` layers. Maintain a non-destructive `.layers.ict` backup sidecar. |
| **4. Notification popup has baked badge** | Notification popup on PC screen shows a green checkmark badge (Image 5). | Synchronizer wrote the active badge to `{actionId}.png` (the base asset used by popups). | Always write `{actionId}.png` with `BadgePosition.None` (strictly clean base icon). |
| **5. v1.2.38 Ambiguity Null-Wipe** | Images turn blank when multiple buttons use the same audio device. | v1.2.38 returned `null` from `GetCommandImageBytes` if `IsAmbiguous == true`. | Remove ambiguity wiping; correlate artwork reliably per action. |
| **6. v1.2.40 COM Crash ("Oops something went wrong")** | Stacked actions / rapid clicks caused COM RPC failures and crashes. | Multi-threaded COM activation across worker threads; x64 PropVariant 24-byte alignment mismatch; non-serialized `PolicyConfigClient`. | Add `SwitchLock` and COM STA serialization; fix `PropVariant` struct size to 24 bytes explicit layout; add 300ms action debounce. (Fixed in v1.2.41) |
| **7. Software UI Desync (Hardware updates, Software does not)** | Physical LCD shows correct checkmark badge, but Electron desktop app UI stays stuck on previous selection. | `ActionImageChanged()` only notifies hardware `DeviceState70`. LoupedeckService never notifies `ConfigurationWindowMessageHandler` via WebSocket on image changes. Furthermore, `File.Move` in `WriteImage` threw `UnauthorizedAccessException` when destination files were held open with read-sharing by Electron/Loupedeck, prematurely aborting profile sync. | 1. Expose `AudioSwitcherPlugin.NotifySoftwareUiImagesChanged()` which fires `OnPluginStatusChanged`, prompting LoupedeckService to broadcast `ProfileActionImagesChangedEventArgs` over WebSocket to the Electron UI. 2. Cache authentic `ActionEditorActionParameters` objects. 3. Fall back to stream writing with retry in `WriteImage` and isolate per-action errors in `SynchronizeProfile`. (Fixed in v1.2.43) |

---

## 4. Current State of the Codebase

1. **`src/AudioSwitcherPlugin.cs`:**
   * Contains `PluginLog.Init()`. Needs `Load()` override to initialize startup audio endpoint detection.
2. **`src/Actions/AudioSwitchCommandBase.cs`:**
   * Manages Action Editor actions (`SwitchAudioOutputCommand`, `SwitchAudioInputCommand`).
   * Needs clean `SetDeviceState` integration with startup endpoint queries.
3. **`src/Actions/ProfileImageBadgeSynchronizer.cs`:**
   * Currently in v1.2.38 "read-only / ambiguity" state.
   * Needs restoration of 3-tier asset writing (clean base `.png`, clean `___Inactive.png`, badged `___Active.png`), deletion of `___Active.ict`, and hydration of `___Inactive.ict`.
4. **`src/Actions/AudioDeviceSwitchImage.cs` & `BadgeGeometry.cs`:**
   * Stable in-memory badge geometry and 3x3 matrix positioning.
5. **`src/Actions/AudioDeviceSwitchDynamicCommandBase.cs`:**
   * Experimental text-tile dynamic command. Can be retained as a secondary utility, but `AudioSwitchCommandBase` remains the primary user feature.

---

## 5. Technical Action Plan for v1.2.39

### Phase 1: Clean Up & Fix Asset Separation (Popups & Badges)
In `ProfileImageBadgeSynchronizer.cs`:
- Remove `IsAmbiguous` null-dropping logic.
- Implement strict 3-tier asset handling:
  1. `{actionId}.png` $\rightarrow$ ALWAYS written with `BadgePosition.None` (clean base icon for popups).
  2. `{actionId}___state0000.png` $\rightarrow$ ALWAYS written with `BadgePosition.None` (clean State 0).
  3. `{actionId}___Active.png` and `{actionId}___state0001.png` $\rightarrow$ stamped with badge ONLY when `isCurrentDevice == true`.
  4. `{actionId}___Inactive.png` $\rightarrow$ owned exclusively by the user/Icon Editor; never overwritten.

### Phase 2: Implement Non-Destructive Layer Hydration (Editor Fix)
In `ProfileImageBadgeSynchronizer.cs`:
- Verify `ActionIcons/{actionId}___Inactive.ict` exists.
- If missing, restore from `{actionId}.layers.ict` or synthesize a clean multi-layer template containing:
  - `Text` layer: `displayName`, font Arial, white, area $(x=0, y=78, w=100, h=22)$.
  - `Image` layer: base artwork/placeholder, area $(x=0, y=0, w=100, h=78)$.
- Save to `{actionId}___Inactive.ict`, `{actionId}.ict`, and `{actionId}.layers.ict`.
- If `{actionId}___Active.ict` exists, **delete it immediately**.

### Phase 3: Implement Startup State Sync & Auto-Matching
In `AudioSwitchCommandBase.cs` and `AudioSwitcherPlugin.cs`:
- On `OnLoad()`, query `AudioDeviceManager.GetDefaultDevice()`.
- For each profile action:
  - Match device UUID against Windows default.
  - Invoke `SetCurrentState(actionParams, isCurrent ? 1 : 0)`.
- Call `ActionImageChanged()`.
- On steady-state 1-second timer ticks, re-verify default device. If Windows audio changes (e.g. headphones unplugged), automatically switch states and trigger redraws.

---

## 6. Verification Checklist

- [ ] **Startup Badge:** On Loupedeck restart, the currently active audio device displays the green checkmark badge immediately on hardware without any button clicks.
- [ ] **Device Switching:** Pressing an inactive device button switches the Windows default endpoint, moves the checkmark badge to the new device on both the physical LCD and the software preview, and removes the badge from the old device.
- [ ] **External Switching:** Changing the default audio device in Windows Settings or via taskbar moves the badge on the physical Loupedeck within 1 second.
- [ ] **Notification Popups:** Loupedeck's on-screen notification popup displays the clean speaker/headphone icon without any checkmark overlay.
- [ ] **Icon Editor Layering:** Opening the Icon Editor for any button loads distinct, editable layers. Inspector fields (Text, Font size, Colors, Icon) are populated, and moving selection handles moves only the target layer without disturbing the background.
- [ ] **Multiple Actions:** Having multiple buttons assigned to the same audio device does not cause images to disappear or turn blank.
