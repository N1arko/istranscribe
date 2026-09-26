# Design QA — FEAT-013.A Floating Recording Widget

## Evidence

- Source visual truth: local generated image used during design review; it is not stored in this repository.
- Rendered implementation: local `artifacts/recording-overlay-live.png` (ignored build evidence).
- Collapsed implementation: local `artifacts/recording-overlay-collapsed-live.png` (ignored build evidence).
- Combined comparison: local `artifacts/design-qa/recording-overlay-comparison.png` (ignored build evidence).
- Viewport: Windows desktop, 1920 × 1080, 100% scaling, dark system theme.
- State: active manual recording; paused and collapsed states were also exercised in the native app.

## Comparison scope

The selected image is a conceptual full-app mock. FEAT-013.A owns the floating right-edge widget, so the comparison is scoped to that component. The combined comparison normalizes the source and implementation into adjacent 760 × 280 regions. It is the full-view evidence for the component and already enlarges the complete timer and all controls enough for focused inspection; a second focused crop would repeat the same pixels.

## Findings

No actionable P0, P1, or P2 differences remain.

### Fonts and typography

- The implementation uses the existing Inter family and Calm Instrument text hierarchy.
- The timer remains legible at native size, uses a semibold optical weight, and preserves the source hierarchy.
- Dynamic timer content fits through the hour format without wrapping or truncation.

### Spacing and layout rhythm

- Control order, grouping, edge anchoring, compact height, rounded surface, and right-edge placement match the selected direction.
- Forty-pixel action targets provide stable pointer interaction within the 48-pixel surface.
- The component is slightly denser than the generated mock. This is an intentional desktop constraint that keeps the widget unobtrusive and does not change hierarchy or usability.

### Colors and visual tokens

- Surface, border, text, recording, paused, pointer-over, and pressed roles come from the existing Calm Instrument resources.
- Recording and paused states remain distinguishable through both color and icon state.
- Contrast is sufficient in the inspected dark state; light, system, and high-contrast behavior inherits the same semantic resource contract.

### Image quality and asset fidelity

- The target contains no photographic or branded image assets inside the widget.
- Icons render sharply as vector controls at native DPI, stay optically centered, and keep a consistent geometric family.

### Copy and content

- The visible widget contains only the live duration. Tooltips and automation names use the existing RU/EN localization resources.
- No design-prompt text or placeholder content appears in the component.

### States, interactions, and accessibility

- Native Windows smoke covered active recording, pause with a frozen timer, resume, collapse, expand, free drag by the status/timer area, cross-session position restore, finish, and automatic hide during processing.
- Icon-only controls expose localized tooltips and automation names.
- Keyboard focus remains available for controls after the user activates the surface; showing the topmost window does not activate it automatically.

## Follow-up polish

- P3: the implementation indicator and timer are visually lighter than the generated mock.
- P3: the finish icon uses a filled recording-color square while the mock uses a light outline.
- P3: the implementation adds a subtle divider after the timer and uses a flatter surface treatment.

These differences preserve the chosen hierarchy and use the product's existing semantic controls, so they do not block acceptance.

## Comparison history

- Pass 1: compared the selected source and live active-recording capture in one combined image. No P0/P1/P2 finding was identified, so no visual fix iteration was required.
- Interaction verification: active, paused, resumed, dragged, restored in a new recording session, collapsed, expanded, finished, and hidden states behaved as specified in the native Windows surface.

## Implementation checklist

- [x] Right-edge topmost surface appears for an active recording.
- [x] Pause-aware duration and pause/resume action share the primary runtime state.
- [x] Finish action ends the recording and the surface hides on the runtime transition.
- [x] Collapse/expand works within the current session and resets for a new session.
- [x] Dragging by the status/timer area persists a center anchor for later recording sessions.
- [x] Localized tooltips and automation names are present.
- [x] Source and rendered implementation were compared in one image.

final result: passed
