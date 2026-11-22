# Windows 11 Accessibility Verification Runbook & Architecture Contract

**Product:** Boundless Study  
**Document ID:** DOC-A11Y-RUNBOOK-001  
**Target Platform:** Windows 11 (Supported builds, x64)  
**Standard Compliance:** WCAG 2.2 Level AA, Section 508, Windows UI Automation (UIA) Architecture  
**Associated Requirements:** `BS-NFR-A11Y-001` through `BS-NFR-A11Y-012`, `BS-TEST-NFR-031` through `BS-TEST-NFR-037`, `BS-TEST-FUNC-006`, `BS-TEST-FUNC-109`  
**Associated Release Conditions:** Decision A3 / D-014 (Retain 24 protected executable targets grow-only; verify Windows 11 accessibility coverage before release or enabling blocking)  
**Source Code Contract:** `tests/BoundlessStudy.Architecture.Tests/AccessibilityContractTests.cs`  

---

## 1. Purpose and Quality Gate Demarcation

This document establishes the official quality gate and repeatable verification procedure for accessibility across the Boundless Study desktop application on Windows 11. 

A central architecture principle of Boundless Study is that **static code checks and live manual verification serve distinct, non-fungible roles**. Passing an automated test suite does **not** constitute accessibility verification; it only verifies that the static XAML markup conforms to structural contracts. Conversely, manual testing without automated contracts allows accessibility regressions to silently re-enter the codebase during feature development.

### 1.1 Demarcation of Automated vs. Manual Checks

| Dimension | Automated Checks (CI / Test Runner) | Manual Release Checks (Live Windows 11) |
|---|---|---|
| **Execution Context** | Headless CI server or developer machine; runs inside `dotnet test`. | Dedicated Windows 11 reference environment (physical or VM) with active desktop session. |
| **Input / Sensors** | Static XML/XAML parsing via `XDocument`. No UI rendered, no hardware needed. | Human tester using physical keyboard, mouse, touch, high-DPI displays, and speakers/headphones. |
| **Assistive Tech** | Static inspection of attributes (`AutomationProperties.Name`, `TabIndex`, etc.). | Live Windows 11 assistive tools: Narrator (`narrator.exe`), Magnifier (`magnify.exe`), On-Screen Keyboard (`osk.exe`). |
| **Visual Themes** | Enforces prohibition of literal hex/named colors; mandates `DynamicResource` and `SystemColors`. | Live inspection across all four Windows 11 Contrast Themes (Aquatic, Desert, Dusk, Night Sky). |
| **Display Scaling** | None (cannot measure rasterization or monitor scaling statically). | Physical multi-monitor test matrix at 100%, 125%, 150%, 175%, and 200% display scaling. |
| **Audio & Speech** | None (cannot verify synthesized speech cadence or pronunciation). | Auditory verification of Narrator speech rate, pitch, verbosity, and announcement silence during timer ticks. |
| **Verdict Authority** | PASS / FAIL in build log. Passing only proves markup contract conformance. | Official release signoff. Every check remains **NOT EXECUTED** until human execution with evidence. |

> [!IMPORTANT]
> Passing automated tests in `AccessibilityContractTests.cs` is a **prerequisite** for manual testing, not a substitute. Release Gate A3 remains **BLOCKED** until all manual procedures in this runbook are executed on a certified Windows 11 build and signed off with attached evidence.

---

## 2. Automated Architecture Contracts (CI Scope)

The automated test suite in `tests/BoundlessStudy.Architecture.Tests/AccessibilityContractTests.cs` automatically discovers all `.xaml` files under `src/BoundlessStudy.Desktop` and asserts the following architectural contracts:

1. **UIA Accessible Names Contract (`BS-NFR-A11Y-002`):**
   - Every `Window` and `UserControl` root element must expose an accessible name via `AutomationProperties.Name` or a non-empty `Title` (for `Window`).
   - Every interactive control (`Button`, `TextBox`, `ComboBox`, `CheckBox`, `RadioButton`, `ListBox`, `ListView`, `MenuItem`, `Hyperlink`, `RepeatButton`, `ToggleButton`, `PasswordBox`, `RichTextBox`, `Slider`, `ProgressBar`, `TabItem`, `DatePicker`, `TreeViewItem`, `DataGrid`) must expose an accessible name.
   - Text content controls may derive their accessible name from visible text (`Content`, `Header`, `Text`). Data controls (`TextBox`, `ComboBox`, `PasswordBox`, `Slider`, etc.) have no text fallback and **must** explicitly define `AutomationProperties.Name` or `AutomationProperties.LabeledBy`.
   - Accessible names must not contain placeholder, trivial, or meaningless values (e.g., `""`, `"-"`, `">"`, `"Button"`, `"TextBox"`, `"click here"`, `"todo"`).
   - When `AutomationProperties.LabeledBy` is bound via `ElementName`, the referenced element **must** exist within the same XAML document.
   - Non-interactive images must either provide an accessible name or be marked explicitly decorative (`Focusable="False"` or `AutomationProperties.AccessibilityView="Raw"`).

2. **Keyboard Navigability & Tab-Index Contract (`BS-NFR-A11Y-001`):**
   - Tab navigation must be deterministic: either every tabbable control in a view defines an explicit, non-negative `TabIndex`, or none does (relying entirely on logical document order). Mixing explicit and implicit tab indices is forbidden because default-0 controls preempt explicit indices.
   - Primary recovery actions (e.g., `Resume`, `Discard`) must never be excluded from keyboard navigation (`IsTabStop="False"` or `Focusable="False"`).
   - Visible focus indicators must not be suppressed. Setting `FocusVisualStyle="{x:Null}"` or empty styles is forbidden.
   - Interactive controls must not have `Focusable="False"` unless disabled or paired with an accessible alternative.
   - Containers must not set `KeyboardNavigation.TabNavigation="Cycle"` or `"Contained"` without documented modal boundary semantics to prevent keyboard traps.
   - At most one button per view may be designated `IsDefault="True"` (Enter key) and at most one `IsCancel="True"` (Escape key).

3. **High Contrast Color Resource Contract (`BS-NFR-A11Y-004`):**
   - Color-bearing properties (`Background`, `Foreground`, `BorderBrush`, `Color`, `Fill`, `Stroke`, `CaretBrush`, `SelectionBrush`, etc.) must not contain hard-coded literal hex colors, with the strict exception of opaque black (`#000000`) and opaque white (`#FFFFFF`).
   - Hardcoded named color literals (e.g., `Red`, `Green`, `Blue`, `LightGray`, `DarkGray`, `Yellow`, `Orange`) are forbidden in UI markup.
   - System colors and theme brushes must be referenced via `{DynamicResource {x:Static SystemColors.*BrushKey}}`, **never** via `{StaticResource ...}`, ensuring dynamic adaptation to runtime Windows theme switches.

4. **Access Key & Mnemonic Contract (`BS-NFR-A11Y-001`, `BS-NFR-A11Y-009`):**
   - Primary recovery actions (`Resume`, `Discard`) must define unique WPF access-key markers (e.g., `_Resume`, `_Discard`).
   - Access keys within a single view must not collide (no duplicate mnemonics).

---

## 3. Manual Verification Reference Environment

All manual verification procedures documented below must be conducted on the certified reference environment:

- **Operating System:** Windows 11 Pro or Enterprise, Version 23H2 (OS Build 22631+) or 24H2 (OS Build 26100+), x64 architecture.
- **Hardware:**
  - 4 physical CPU cores (or 8 logical cores), 8 GB RAM, SSD.
  - Physical standard US English QWERTY keyboard.
  - Precision touchpad and physical two-button mouse.
  - Audio output device (speakers or headphones) with system volume set to 50%.
- **Display Configurations:**
  - **Single Monitor:** 1920x1080 resolution at 100%, 125%, 150%, and 200% scaling.
  - **Dual Monitor Mixed-DPI:** Monitor 1 at 2560x1440 @ 150%; Monitor 2 at 1920x1080 @ 100%.
- **Pre-Test Data Baseline:**
  - Clean local user profile with initialized `boundless.db` containing seeded study tasks, subjects, focus profiles, and historical sessions.
  - Windows Ease of Access / Accessibility settings set to default before starting each test cycle.

---

## 4. Manual Screen Reader (Narrator) Verification Procedure

**Scope:** Validates `BS-NFR-A11Y-002` (UIA Names/Roles/States), `BS-NFR-A11Y-003` (Narrator Workflow Completion), and `BS-NFR-A11Y-008` (Timer & State Announcements).

### 4.1 Setup & Configuration
1. Start Windows Narrator: Press `Win + Ctrl + Enter` (or open Windows Settings > Accessibility > Narrator > Toggle On).
2. Ensure Narrator Voice is set to a natural voice (e.g., "Microsoft Jenny" or "Microsoft Guy") at 50% speed.
3. In Narrator Settings, set **Verbosity Level** to **3 - Immediate context** (reads control name, type, and state).
4. Verify "Hear advanced details like help text on buttons and controls" is checked.
5. Launch the Boundless Study desktop application.

### 4.2 Verification Steps

| Step | Action | Expected Spoken Output | Status |
|---|---|---|---|
| **NAR-01** | Focus application window (`Alt + Tab`). | Narrator announces window title: *"Boundless Study, window"*. Focus is placed on initial dashboard element. | [ ] NOT EXECUTED |
| **NAR-02** | Press `Tab` through all interactive controls on the primary dashboard. | For every control, Narrator reads: **[Name]**, **[Role]** (e.g., *button*, *edit*, *combo box*), and **[State]** (e.g., *disabled*, *expanded*). No control speaks as *"unnamed"* or *"pane"*. | [ ] NOT EXECUTED |
| **NAR-03** | Navigate to Task Creation form and enter task details using keyboard only. | Form fields announce labels: *"Task Title, edit"*, *"Subject, combo box, collapsed"*, *"Estimated Duration, edit"*. Invalid inputs trigger validation error announcement. | [ ] NOT EXECUTED |
| **NAR-04** | Toggle Narrator Scan Mode (`Caps Lock + Space`). Use `H` to navigate headings, `B` for buttons, `F` for form fields. | Scan mode moves logically through document hierarchy. Headings announce their level (e.g., *"Focus Session, Heading 1"*). | [ ] NOT EXECUTED |
| **NAR-05** | Start a 25-minute Focus Session. | Narrator announces: *"Focus session started, 25 minutes remaining"*. | [ ] NOT EXECUTED |
| **NAR-06** | Observe audio output during active session countdown (minutes 1 through 3). | **Anti-flooding check:** Narrator remains **silent** during normal second-by-second countdown ticks. It must **not** announce every tick (`BS-NFR-A11Y-008`). | [ ] NOT EXECUTED |
| **NAR-07** | Trigger session pause / interruption. | Narrator immediately announces state change: *"Session paused. Active time: 4 minutes. Press Alt+R to resume or Alt+D to discard."* | [ ] NOT EXECUTED |
| **NAR-08** | Complete session and observe summary screen. | Narrator announces session completion dialog: *"Session complete. 25 minutes studied, 50 XP awarded. Focus summary, table, 4 rows, 2 columns."* | [ ] NOT EXECUTED |
| **NAR-09** | Navigate to Backup & Restore screen and initiate backup. | Narrator announces backup progress and completion: *"Creating database backup... Backup complete: boundless-backup-20261122.bak, verified."* | [ ] NOT EXECUTED |

### 4.3 Narrator Failure Criteria
- Any control announced as *"unnamed"*, *"blank"*, or with its raw class name (`"Button1"`, `"TextBlock"`).
- Countdown timer ticks flooding the audio channel continuously.
- Failure to announce session state transitions (Pause, Resume, Break, Stop).
- Dialogs that fail to announce their title or trapping Narrator focus.

---

## 5. Manual Magnifier Verification Procedure

**Scope:** Validates `BS-NFR-A11Y-005` (Display Scaling & Zooming) and `BS-NFR-A11Y-012` (No precision pointer dependence).

### 5.1 Setup & Configuration
1. Open Windows Magnifier: Press `Win + Plus` (`Win + +`).
2. Open Magnifier Settings: Set **Zoom Increment** to **100%**.
3. Enable tracking options:
   - Check **Have Magnifier follow: Mouse pointer**.
   - Check **Have Magnifier follow: Keyboard focus**.
   - Check **Have Magnifier follow: Text insertion point**.
4. Test in three view modes: Full Screen (`Ctrl + Alt + F`), Lens (`Ctrl + Alt + L`), and Docked (`Ctrl + Alt + D`).

### 5.2 Verification Steps

| Step | Action | Expected Visual Behavior | Status |
|---|---|---|---|
| **MAG-01** | In Full Screen mode, zoom to **200%** (`Win + +`). Navigate the dashboard using `Tab` and arrow keys. | The magnified viewport automatically pans to keep the currently focused control centered and fully visible. | [ ] NOT EXECUTED |
| **MAG-02** | Increase zoom to **400%**. Focus on text input fields and type 50 characters. | The magnified viewport tracks the text cursor (caret). Characters do not clip or extend beyond visible screen boundaries without smooth panning. | [ ] NOT EXECUTED |
| **MAG-03** | Inspect typography and icons at 400% zoom. | Fonts render cleanly with ClearType/DirectWrite smoothing; vector icons (`Path`/vector resources) scale smoothly without pixelation; no text overlaps adjacent containers. | [ ] NOT EXECUTED |
| **MAG-04** | Switch to Lens Mode (`Ctrl + Alt + L`). Move the lens over status indicators, charts, and buttons. | The lens displays high-resolution content without rendering glitches, clipping, or flickering. | [ ] NOT EXECUTED |
| **MAG-05** | Switch to Docked Mode (`Ctrl + Alt + D`) anchored at the top of the screen. Resize application window. | The application window adjusts layout cleanly without controls sliding under the docked magnifier bar. | [ ] NOT EXECUTED |
| **MAG-06** | Test button and interactive click targets at 200% zoom. | Interactive target bounds meet or exceed 32x32 effective pixels; clicking or tapping does not require sub-pixel precision (`BS-NFR-A11Y-012`). | [ ] NOT EXECUTED |

### 5.3 Magnifier Failure Criteria
- Focus jumps outside the magnified viewport without automatic scrolling.
- Text or controls clip horizontally, preventing reading of error or recovery messages.
- Fixed-size dialogs become unscrollable when the viewport is magnified.

---

## 6. Manual On-Screen Keyboard (OSK) Verification Procedure

**Scope:** Validates `BS-NFR-A11Y-001` (Alternative Input Operation) and `BS-NFR-A11Y-012` (No hardware keyboard dependence).

### 6.1 Setup & Configuration
1. Disconnect physical keyboard or push it aside.
2. Launch On-Screen Keyboard: Press `Win + Ctrl + O` or run `osk.exe` from Run dialog / Command prompt.
3. Position the OSK window at the lower half of the primary display.
4. Set OSK Options: Enable "Use click sound" (optional) and "Turn on numeric key pad".

### 6.2 Verification Steps

| Step | Action | Expected Operational Behavior | Status |
|---|---|---|---|
| **OSK-01** | Click OSK `Tab` key repeatedly to navigate dashboard controls. | Focus advances deterministically through all interactive controls. Focus visual rectangle is clearly visible on every selected control. | [ ] NOT EXECUTED |
| **OSK-02** | Click OSK `Shift` then `Tab` to navigate backwards. | Focus moves in exact reverse order without skipping controls or getting trapped. | [ ] NOT EXECUTED |
| **OSK-03** | Click OSK `Space` or `Enter` to activate buttons, checkboxes, and expand combo boxes. | Actions execute immediately, identical to physical keyboard input. | [ ] NOT EXECUTED |
| **OSK-04** | Click into Task Title `TextBox` and type using virtual OSK alphanumeric keys. | Text is received smoothly into the control without lost keystrokes or focus loss. | [ ] NOT EXECUTED |
| **OSK-05** | Activate access keys: Click OSK `Alt`, then observe access key underlines in Boundless Study UI. Click the mnemonic key (e.g. `R` for `_Resume`). | The associated command executes immediately without requiring mouse dragging or hardware keyboard input. | [ ] NOT EXECUTED |
| **OSK-06** | Position OSK directly over the lower portion of the application window. | The application window's scrolling containers adjust or remain operable; critical action buttons (e.g., Save, Start, Cancel) can be scrolled into view above the OSK. | [ ] NOT EXECUTED |
| **OSK-07** | In an open modal dialog or overlay, click OSK `Esc` key. | Modal or flyout closes cleanly, restoring focus to the invoking element. | [ ] NOT EXECUTED |

### 6.3 OSK Failure Criteria
- Controls lose focus when clicking an OSK key.
- Mnemonic / access-key sequences fail to activate when clicking OSK `Alt` followed by a letter.
- Input fields fail to accept OSK virtual keystrokes.

---

## 7. Manual High-Contrast Checklist (Contrast Themes Scope)

**Scope:** Validates `BS-NFR-A11Y-004` (High Contrast Mode Visibility), `BS-NFR-A11Y-006` (No Color-Alone Meaning), and WCAG 2.2 AA Contrast Criteria.

### 7.1 Setup & Configuration
1. Open Windows Settings > Accessibility > **Contrast themes** (Shortcut: `Left Alt + Left Shift + PrintScreen`).
2. Test **all four** standard Windows 11 Contrast Themes sequentially:
   - **Theme 1: Aquatic** (Teal text on dark navy background, yellow links, light blue selection).
   - **Theme 2: Desert** (Burgundy/dark brown text on light sand background, turquoise highlights).
   - **Theme 3: Dusk** (Cyan text on dark violet background, green highlights).
   - **Theme 4: Night Sky** (Light yellow text on pure black background, magenta highlights).
3. For each theme, inspect all primary views: Dashboard, Session Runner, Task Management, Insights/Charts, Settings, and the Blocking Overlay.

### 7.2 Detailed Verification Checklist per Theme

| Check ID | Verification Item | Acceptance Criteria | Aquatic | Desert | Dusk | Night Sky |
|---|---|---|---|---|---|---|
| **HC-01** | Window & Card Backgrounds | System window brush replaces standard application background; no glaring white or dark patches from hard-coded colors. | [ ] | [ ] | [ ] | [ ] |
| **HC-02** | Text Readability | All labels, headers, notes, and values render in high-contrast text brush; contrast ratio meets or exceeds **7:1** for normal text, **4.5:1** for large text. | [ ] | [ ] | [ ] | [ ] |
| **HC-03** | Container & Card Borders | Every card, list item, text box, and panel has a distinct, visible high-contrast border. No transparent borders that disappear. | [ ] | [ ] | [ ] | [ ] |
| **HC-04** | Keyboard Focus Rectangle | When an element receives focus, a high-contrast focus rectangle (at least 2px thick, distinct color) surrounds the element clearly. | [ ] | [ ] | [ ] | [ ] |
| **HC-05** | Selection & Active States | Selected list items, tabs, and radio buttons use high-contrast highlight brush; selected text uses highlight text brush. | [ ] | [ ] | [ ] | [ ] |
| **HC-06** | Status & Badge Information | Status (Active, Paused, Completed, Blocked) is conveyed with text/symbols alongside color; never by color alone (`BS-NFR-A11Y-006`). | [ ] | [ ] | [ ] | [ ] |
| **HC-07** | Error & Warning Messages | Error banners and inline field validations display visible border, warning icon glyph, and legible high-contrast text (`BS-NFR-A11Y-009`). | [ ] | [ ] | [ ] | [ ] |
| **HC-08** | Charts & Insights Data | Chart bars, markers, or lines use distinct hatching/patterns/symbols; data values are legible; accessible table alternative is available. | [ ] | [ ] | [ ] | [ ] |
| **HC-09** | Blocking Overlay Screen | Full-screen blocking notice preserves text legibility, visible warning border, and clearly distinguished exit button (`BS-NFR-A11Y-011`). | [ ] | [ ] | [ ] | [ ] |
| **HC-10** | Live Theme Switching | Switching themes while the application is actively running updates colors immediately via `DynamicResource` without requiring application restart. | [ ] | [ ] | [ ] | [ ] |

### 7.3 High-Contrast Failure Criteria
- Inverted or unreadable text (e.g., black text on black background, white on white).
- Borders or separation lines disappearing entirely, causing controls to merge visually.
- Focus indicator invisible against high-contrast background.
- Application requires restart to reflect high-contrast theme change.

---

## 8. Protected Targets Verification (A3 / D-014 Release Condition)

**Scope:** Validates that accessibility tools are permanently excluded from blocking and enforcement mechanisms under approved decision A3 (D-014).

Under decision A3 (approved 2026-11-19), the protected executable baseline includes three critical accessibility executables:
1. `narrator.exe` (Windows Narrator)
2. `magnify.exe` (Windows Magnifier)
3. `osk.exe` (Windows On-Screen Keyboard)

### 8.1 Verification Protocol

| Step | Action | Expected System Behavior | Status |
|---|---|---|---|
| **A3-01** | Inspect `BlockedApp.ExcludedExecutableNames` in `src/BoundlessStudy.Domain/Entities/BlockingRules.cs`. | `narrator.exe`, `magnify.exe`, and `osk.exe` are present in the excluded list. | [ ] NOT EXECUTED |
| **A3-02** | Start an active Focus Session with strict application blocking enabled. | Focus session enters `Active` state with background monitoring active. | [ ] NOT EXECUTED |
| **A3-03** | Launch Windows Narrator (`Win + Ctrl + Enter`). | Narrator launches immediately; the blocking overlay is **not** displayed; session remains in `Active` state; no violation event logged. | [ ] NOT EXECUTED |
| **A3-04** | Launch Windows Magnifier (`Win + +`). Zoom in and out over application. | Magnifier functions without hindrance; no blocking overlay; no violation event logged. | [ ] NOT EXECUTED |
| **A3-05** | Launch Windows On-Screen Keyboard (`osk.exe`). Interact with application. | OSK runs freely; virtual typing accepted; no blocking overlay; no violation event logged. | [ ] NOT EXECUTED |
| **A3-06** | Attempt to configure a blocking rule targeting `narrator.exe`, `magnify.exe`, or `osk.exe` in Settings. | Application rejects the rule with an explicit validation error stating that system accessibility tools cannot be blocked. | [ ] NOT EXECUTED |

---

## 9. Display Scaling & Mixed-DPI Verification Matrix

**Scope:** Validates `BS-NFR-A11Y-005` (100% to 200% display scaling and mixed-DPI dual-monitor operation).

### 9.1 Verification Matrix

| Scale Factor | Test Configuration | Layout Integrity | Text Readability | Focus Visibility | Target Sizing (≥32px) | Status |
|---|---|---|---|---|---|---|
| **100% (96 DPI)** | Single Monitor 1920x1080 | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |
| **125% (120 DPI)** | Single Monitor 1920x1080 | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |
| **150% (144 DPI)** | Single Monitor 2560x1440 | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |
| **175% (168 DPI)** | Single Monitor 2560x1440 | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |
| **200% (192 DPI)** | Single Monitor 3840x2160 (or 1920x1080) | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |
| **Mixed-DPI** | Drag window from 150% Monitor to 100% Monitor | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] Pass [ ] Fail | [ ] NOT EXECUTED |

### 9.2 Mixed-DPI Drag Procedure
1. Configure Monitor 1 at 150% scaling and Monitor 2 at 100% scaling.
2. Launch Boundless Study on Monitor 1. Verify layout is proportional and crisp.
3. Slowly drag the window across the monitor boundary so it straddles both screens.
4. Release the window onto Monitor 2.
5. **Verify:** Per-Monitor DPI v2 awareness triggers; window re-renders crisply at 100% without blurred text, clipping, or truncated dialog buttons.
6. Drag window back to Monitor 1. Verify clean scaling back to 150%.

---

## 10. Manual Release Execution Log & Evidence Record

This section serves as the permanent evidence record for release approval. Every test execution must record the operator, date, build hash, environment details, and pass/fail verdict.

### 10.1 Execution Header
- **Release Version:** `0.1.0-alpha`
- **Build Hash / Commit:** ________________________________________
- **Test Operator Name:** ________________________________________
- **Execution Date:** ________________________________________
- **Windows 11 OS Build:** ________________________________________
- **Display Hardware:** ________________________________________

### 10.2 Summary Scorecard

| Area | Requirement References | CI Automated Status | Manual Windows 11 Status | Verified By | Date |
|---|---|---|---|---|---|
| **Keyboard Navigability** | `BS-NFR-A11Y-001`, `BS-TEST-NFR-031` | **PASS (CI Contract)** | [ ] NOT EXECUTED | | |
| **UIA Names & Roles** | `BS-NFR-A11Y-002`, `BS-TEST-NFR-031` | **PASS (CI Contract)** | [ ] NOT EXECUTED | | |
| **Narrator Acceptance** | `BS-NFR-A11Y-003`, `BS-TEST-NFR-032` | N/A (Manual only) | [ ] NOT EXECUTED | | |
| **High Contrast Themes** | `BS-NFR-A11Y-004`, `BS-TEST-NFR-033` | **PASS (CI Contract)** | [ ] NOT EXECUTED | | |
| **Display Scaling & DPI**| `BS-NFR-A11Y-005`, `BS-TEST-NFR-034` | N/A (Manual only) | [ ] NOT EXECUTED | | |
| **No Color-Only Meaning**| `BS-NFR-A11Y-006`, `BS-TEST-NFR-033` | N/A (Manual only) | [ ] NOT EXECUTED | | |
| **Reduced Motion** | `BS-NFR-A11Y-007`, `BS-TEST-NFR-035` | N/A (Manual only) | [ ] NOT EXECUTED | | |
| **Timer Anti-Flooding** | `BS-NFR-A11Y-008`, `BS-TEST-NFR-035` | N/A (Manual only) | [ ] NOT EXECUTED | | |
| **Error Recovery Content**| `BS-NFR-A11Y-009`, `BS-TEST-NFR-036` | **PASS (CI Contract)** | [ ] NOT EXECUTED | | |
| **Protected A11y Targets**| `A3 / D-014`, `BS-APP-003` | **PASS (Domain Unit)** | [ ] NOT EXECUTED | | |

### 10.3 Post-Test Environment Restoration Procedure
1. Toggle off Narrator (`Win + Ctrl + Enter` or `Caps Lock + Esc`).
2. Close Magnifier (`Win + Esc`).
3. Close On-Screen Keyboard (`Alt + F4` on OSK).
4. Revert High Contrast theme to "None" in Windows Settings > Accessibility > Contrast themes.
5. Revert display scaling to recommended default (100% or 150%).
6. Reset audio volume to standard level.
