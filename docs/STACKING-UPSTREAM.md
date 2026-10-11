# Shared-reference stacking integration (not released)

This Windows work prepares the stacker UI and driver for upstream
[Seiza #245](https://github.com/theatrus/seiza/pull/245),
[#246](https://github.com/theatrus/seiza/pull/246), and
[#247](https://github.com/theatrus/seiza/pull/247).
The first two add a common alignment image; the third adds native-ranked
fallback when a channel's best light cannot align to that image.
[#248](https://github.com/theatrus/seiza/pull/248) adds the same selection to
Python and does not require another Windows API.

## Release hold

**Do not release this integration until the required Seiza crates are published.**
The production Cargo manifests and lockfile deliberately remain on published
dependencies. Seiza 0.26.0 predates the shared-reference C API changes, even
though the unreleased source still reports that version. Windows probes the
actual DLL exports, rather than assuming a version implies support. The new
controls are unavailable with the current published DLL; ordinary stacking
continues to work.

As of October 11, 2026, all three core changes are merged. The latest published
`seiza-cabi` 0.26.0 and `seiza-stacking` 0.28.0 still predate those changes.

Before making a Windows release:

- Confirm the new published crates include #245, #246, and #247, and reconcile
  any further API changes.
- Update the exact `seiza-cabi` pin and compatible stacking dependencies to
  their **published registry versions**, and regenerate `Cargo.lock`.
- Build through `scripts/build-rust.ps1`; keep its registry-only dependency
  checks. Do not vendor or ship an unreleased Git/path override.
- Run the complete managed/native suites against the newly published DLL.
  Shared-reference and candidate-fallback tests must run, not skip.
- Exercise both Windows stack dialogs, shared-grid filter outputs, fallback
  warnings, live pause/resume, and source/output protection.
- Only then choose the Windows release version and follow `RELEASING.md`.

## Windows behavior

In **Stack Images**, enable **Use a shared alignment reference**. By default,
the first stack's channel reference supplies the common grid. Optionally
choose a FITS/XISF image of the same field instead. Separate filter outputs
then share that image's dimensions and WCS. Each filter still selects its own
channel reference for brightness normalization, noise weighting, and
calibration matching.

In **Live Stack**, an optional alignment image supplies geometry while a
captured light supplies the channel reference. Restored sessions retain their
saved grid; an explicitly different grid cannot silently replace it.

The alignment image is not calibrated or counted as a light unless it is
also explicitly selected as an input. A shared-reference stack starts empty,
so the driver pushes the chosen channel light first. Automatic selection
uses up to five candidates in the core's ranking order, without reimplementing
the scorer in C#. Skipped alignment candidates and their reasons are reported;
those lights can still be offered once as ordinary stack inputs. Manual
reference selection does not silently fall back.

Source lights, calibration masters, and the shared reference are protected
from output replacement, including when writing through an atomic staging
file. Live folder monitoring excludes the alignment image so it cannot be
accidentally ingested as a capture.

## Prerelease validation

For development only, build `seiza-cabi` from a separate, revision-pinned
upstream checkout and supply the resulting DLL to the managed tests via
`-p:SeizaNativeDll=<absolute path>` (use a separate output directory for
parallel validation runs). The test project always copies the explicitly
selected DLL, even when its timestamp is older than the previous run's DLL.
Keep that checkout and target directory
outside the production dependency graph. The capability-gated tests visibly
skip against the existing published DLL and execute against the prerelease
one. This is validation, not a shipping dependency.
