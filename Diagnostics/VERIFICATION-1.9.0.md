# UsageNotch 1.9.0 verification

9 September 2026. Scope: slimmer proportions and predictive drag-shape previews.

## Implemented

- Sleek side width: 88 → 76 DIPs at 100% scale; ring 50 → 44; logo 21 → 19. Compact width: 76 → 68, ring 40 → 38. Shorter shoulders and lighter outline. Existing saved scale/spacing are preserved; new configurations use 18-DIP spacing.
- Bounded 160-ms velocity look-ahead, filtered direction, corner hysteresis, gradual reversal and relaxation on stopping. The actual drop zone remains distance-based and independent of prediction.
- Drag updates coalesced on WPF render frames. Morph/reach changes share one geometry rebuild. Surface brushes are cached across frames. Frame listener is removed on release or window close.
- Cross-axis directional reach keeps icons upright. Horizontal/vertical layout still changes on release, not halfway through a drag.
- Independent top/side magnetic attraction removes the positional discontinuity at their boundary.
- New Appearance toggle: Anticipate drag direction. Reduced motion disables prediction and directional reach.
- Shape dimensions now use the same UI scale as the content. A new perimeter check discovered top-layout clipping at 75%/tight spacing; this is fixed.

## Passed

- Release compilation, zero errors. NU1900 warnings: NuGet vulnerability metadata could not be retrieved; dependency vulnerability audit is not verified.
- 1,728 WPF-rendered layout combinations: remaining/used, sleek/classic, left/right/top, floating/attached, compact/full, 75/100/150% scale, tight/default/wide spacing, session/dual, labels shown/hidden.
- Ring-centre alignment, equal spacing, outline clipping, sampled full ring perimeters, colour contrast. Remaining layouts include the wider 100% value.
- 300 bounded predictive-shape combinations; approach, stop, turn, corner jitter, negative monitor coordinates, gesture reset, snap off, prediction off and no expanded snap zone.
- Analytic settling checks at 30/60/90/120/144/240 Hz, reversal without overshoot. These are timing-model tests, not measured display frame rates.
- Existing Claude cooldown/cache/auth recovery tests, account isolation, alerts, provider parsing, settings live editing/cancel, hotkeys and unknown readings.
- Final full suite exited 0 when run with access to Windows DPAPI. A sandbox-only attempt could not create the encrypted fake-credential cache; the outside-sandbox retry passed. No AI models were called by these tests.
- Rendered sample-data previews inspected: side dual usage, remaining mode, top layout. Previews are not screenshots of a live mouse interaction.

## Limits and handoff

- Live native dragging, monitor transitions and on-device frame pacing have not been hands-on verified in this turn. No promise of flawless performance on all hardware.
- Portable EXE remains unsigned. Microsoft Store enrollment, package-identity configuration, packaged install/update testing and Store certification remain outstanding.
- Release/Latest is the canonical portable build; Release/Archive/v1.8.0-win-x64 preserves the previous EXE and instructions. No user settings or credentials were deleted.
