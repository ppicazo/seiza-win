# Seiza 0.44.2 Windows adoption validation

Validated on Windows x64, 2026-10-25, with Rust 1.99.1 and .NET 10. This is a
follow-up to merged Windows PR #70. Dependencies remain published registry
packages: Seiza/C ABI 0.44.2, stacking 0.51.2, parallax 0.19.2 and satellites
0.22.2. The native release source is
`d955f83055432e21a435960dbf802d0e6d940b48`.

## GPU policy and memory budget

The actual production Settings window was exercised in an isolated diagnostic
build with a separate settings file. The planning budget was changed from
automatic to 4096 MiB, then 128 MiB, and survived reopening Settings. Turning
GPU off disabled the budget control while retaining its value. A full process
restart retained CPU-only / 128 MiB. Production batch and live dialog option
creation agreed with the persisted values.

| Production batch runs, with final reintegration | Accepted fixed / mosaic | Elapsed total |
| --- | --- | ---: |
| GPU enabled, 128 MiB | 3/3 and 8/8 | 5.795 s |
| CPU-only, 128 MiB retained | 3/3 and 8/8 | 1.822 s |

These are small M31 diagnostic crops/repeats, not independent telescope
exposures. Times include startup, and the GPU run overlapped compilation.
They are smoke-test timings, not a controlled speed comparison.

The enabled run selected Intel Arc A770M / Vulkan and logged actual device
computation, not only adapter detection. Its 1312 MiB accumulator requirement
exceeded three quarters of the 128 MiB planning budget, selecting a CPU
accumulator and small reintegration bands. The trace's device reservation
exceeded 128 MiB, consistent with the documented soft planning budget.
The disabled run logged no adapter/device-compute/GPU-error entries. All
diagnostic source SHA-256 hashes were unchanged in both runs.

The earlier dedicated Arc / Iris Xe iGPU, D3D12 / Vulkan memory-planner matrix
belongs to [core PR #363](https://github.com/theatrus/seiza/pull/363). That full
matrix was not repeated for this app update; the current app smoke test above
uses the published default adapter selection.

A native checkpoint saved under 0.42.0 reopened under 0.44.2 with a CPU-only /
4096 MiB override and identical dimensions, frame counts, source ledger and
configuration fingerprint. Managed tests cover changing GPU policy in both
directions and changing the budget after the original reference file is gone.
Expanded canvases with Local normalization fail validation with alternatives
before a stack can start.

## Color contracts

Native and managed tests cover signed/NaN-preserving continuum subtraction,
exact-source saved-plan replay, stale-source rejection, missing-saturation
skips without manual fallback, clipping-mask validation and output aliases.
A synthetic 720 × 720 Gaussian star field recovers its known 0.7 continuum
coefficient within 0.001, passes validation with and without Gaussian PSF
matching, and its reviewed plan produces residuals below 0.01 in original
sample units.

Analytical pixel checks cover linear RGB star extraction on a common scale,
prepared-layer screening without another stretch, HOO/SHO display composition,
linked linear-to-display preparation and conflicting transfer declarations.
FITS and XISF display outputs preserve the expected colors through the app's
8-bit preview, 16-bit export, processing clipboard and Explorer thumbnail /
preview renderer. Failure and pre-cancellation preserve existing outputs;
reports cannot replace a source, plan or result.

The production native color window launches and exposes the new controls.
Interactive picker/processing UI validation remains limited: Windows computer
use twice returned `failed to activate captured window`. The color service
and native scientific paths were tested directly; a complete interactive
color workflow is not claimed.

## Checks

- Release solution build: zero warnings/errors.
- Unsigned MSI build and static Finish/Shell sequencing check pass; no install.
- Managed suite: 788 passed.
- Windows suite: 78 passed, 4 optional real-source/encoder tests skipped.
- Windows platform adapter: 22 passed; Explorer provider: 30 passed.
- Published native suites: C ABI 145; stacking unit 503 (5 ignored), caller-pool
  1, integrations 20 + 12, output-version 55, doc 1; parallax 141 passed.
- Rustfmt, workspace Clippy with warnings denied, and Git whitespace checks pass.

Raw local evidence is under ignored `artifacts/seiza-0442/`, including native
traces, source hashes, checkpoint comparison, test logs and diagnostic app
results. User settings and original images were unchanged. No installation,
merge or release was performed.
