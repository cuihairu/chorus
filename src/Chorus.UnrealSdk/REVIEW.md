# Chorus Unreal SDK — Pre-implementation Review (EK-3, stage 1)

> The task book (EK-3) splits the UE5 plugin into two steps: **first** this review
> (module form / CI compile cost / reuse boundary, with a 做-or-缓做 conclusion and
> an effort estimate), **then**, only if the review passes, a minimal UE module
> (transport + Envelope codec + session, not full feature). Landing this review
> document is the EK-3 one-stage deliverable. It is written before any UE code so
> the conclusion is not shaped by work already done.

## 1. Scope of the decision

What must be true for the UE plugin to be worth building now, and what the
review must fix. The plugin is the third engine surface after the Unity SDK
(`com.chorus.sdk`, 21 FIX-U3D items) and the Godot SDK (EK-1, C# Godot 4 .NET).
It must reuse the existing **protocol and session semantics** — the same wire and
the same decision ladder — not a parallel variant.

## 2. UE module form

The plugin would be a standard UE5 Runtime module:

```
src/Chorus.UnrealSdk/
  ChorusUnrealSdk.uplugin          # plugin descriptor: "Runtime" module, no editor module
  Source/
    ChorusUnrealSdk/
      ChorusUnrealSdk.Build.cs     # UBT module rules
      ChorusUnrealSdkModule.cpp    # IMPLEMENT_MODULE (empty for a logic-only module)
      Envelope/...                 # wire codec (Envelope framing)
      Session/...                  # session lifecycle + applier
      Transport/...                # the transport seam (C++ interface)
      Tests/...                    # AutomationTest pure-logic tests (no Editor dependency)
```

Facts that shape the form:

- **No-Editor pure-logic module is achievable.** A `Runtime` module that depends
  only on `Core` (and pulls nothing from `Engine`, `RHI`, `Slate`, `Editor`) can
  hold the codec and the applier. The UE Automation framework
  (`IMPLEMENT_SIMPLE_AUTOMATION_TEST`, `EAutomationTestFlags::ApplicationContextMask`)
  runs these as `-ExecCmds="Automation RunTests ..."` and, for a Runtime module
  with no renderer dependency, under `-nullrhi -nosplash` headless — no Editor
  binary, no render device.
- **The module is logic-only in the honest boundary sense**: the parts that touch
  UE objects (the `UObject`/`UWorld` bridge that maps the transport's frames onto
  game state) are *not* part of this minimal module. The transport seam is a plain
  C++ interface; a UE-side adapter (an `AActor` or a subsystem) is a later,
  separate concern.
- **UE has no cross-platform .NET hosting story** (see §4), so the module is
  **C++**, not C#. The `Build.cs` therefore declares the `Chorus.Messaging`-derived
  contract as the thing it implements, not as a referenced assembly.

## 3. CI compile cost

The load-bearing question. UE is **not** on NuGet and **not** in any official
GitHub Actions image. Compiling one UE module means:

| Item | Cost | Notes |
|---|---|---|
| Engine on the runner | 30–60 GB source, or a prebuilt binary | GitHub `windows-latest` has ~30 GB usable; a full UE install does not fit with a checkout. A self-hosted runner is the realistic host. |
| UBT first cold build | 10–30+ min | `RunUAT BuildPlugin` compiles the module against the engine; no way around the first build. |
| Engine-source setup (git clone + setup script) | 30–90 min, one-time | Cacheable (`actions/cache` keyed on engine version), but the *cold* leg is prohibitive. |
| Incremental rebuild after cached engine | ~2–5 min | Feasible **if** a runner already carries a cached engine install; this is the only viable CI shape. |
| Cross-platform matrix | x2 | UE module compiles per platform (Win64/Linux/…); each adds a build leg. |

The DDC (DerivedDataCache) that UE caches is for shaders/assets — irrelevant to a
logic-only module, so the usual UE cache strategy buys nothing here; the cache
that matters is the **engine install itself**.

**Conclusion on cost:** as a *must-run* CI leg, the cost is **high** — a cold UE
toolchain on a hosted runner is not reliable (disk, setup time, flakiness), and
there is no UE runner provisioned for this repo. The honest CI placement, if the
module is ever built, is a **manual-dispatch** leg on a self-hosted runner with a
cached engine (the task book already allows this: "UE 侧按 REVIEW 结论决定是否进
CI（编译成本过高则挂手动 dispatch）"). It is **not** a candidate for the must-run
set that the cross-engine golden tests occupy.

## 4. Reuse boundary with the Unity and Godot SDKs

What can actually be reused:

- **The wire and semantics contract — fully reusable, and already pinned.** The
  Envelope framing (`JsonEnvelopeCodec`), the applier decision ladder
  (`SyncApplier`), the session lifecycle and the transport seam are
  **language-neutral contracts**, and the cross-engine golden tests (EK-2) pin
  them byte-for-byte between the reference C# codec and the Godot port. A UE
  module implements the *same contract*; the golden set is the oracle that keeps
  it honest.
- **The Godot SDK's *code* — not directly reusable.** `Chorus.GodotSdk` is a pure
  net8.0 class library with zero `GodotSharp` and zero Chorus references, and the
  Unity leaf is the same shape (engine-agnostic C#). That is exactly why they drop
  into Godot/Unity as normal assemblies. But **UE5 cannot host a .NET assembly**:
  there is no sanctioned CLR host; community `UnrealDotNet`-style plugins are
  unmaintained; C++/CLI (`/clr`) is Windows-only and collides with UBT's flags.
  So the C# leaves are **not** a shared binary for UE.

**Therefore the reuse boundary is the *contract*, not the *code*.** The realistic
UE path is a C++ re-implementation of the codec + applier + session from the
contract, verified against the same golden Envelope bytes. That means a
**cross-language golden leg** (C++ serialize == C# serialize) is the true
anti-drift guard for the UE module — a C++ unit test that pins the UE side to the
same byte-level oracle the Godot side already answers.

## 5. Conclusion

**缓做 (defer) the UE module implementation.** The review's own criteria are met
by a logic-only Runtime module, but the binding constraint is the toolchain, not
the design:

1. There is **no UE toolchain on this machine** (`UE_ROOT` unset, no
   `UnrealBuildTool`, no engine source) and **no UE CI runner** provisioned for
   this repo. The module cannot be compiled or tested here.
2. The reuse boundary requires a **C++ re-implementation** (UE has no .NET host),
   which is new surface, not a port — it carries its own drift risk and needs a
   cross-language golden leg before it can be trusted.
3. UE is **P1**, after the P0 Godot SDK and the P0 cross-engine consistency core
   (EK-1/EK-2). Nothing in the P0 deliverable depends on UE.

Deferring does not mean the design is rejected: the module form (§2), the CI
placement (§3) and the reuse boundary (§4) are all recorded here as the shape to
build against, and EK-4's UE leg is wired to this conclusion (manual dispatch, not
must-run). The deferred step-2 work is scoped below.

**Estimated effort when the toolchain is provisioned:**

| Item | Effort |
|---|---|
| C++ Envelope codec + wire types (byte-identical to `JsonEnvelopeCodec`) | 1–2 focused days |
| C++ `SyncApplier` (same decision ladder) + session state machine | 1–2 focused days |
| Cross-language golden leg (C++ vs C# bytes) | ~0.5 day |
| Minimal UE module (`.uplugin` + `Build.cs` + Automation tests) | ~1 day |
| **Total** | **~4–5 focused days**, plus the toolchain provisioning (engine install on a self-hosted runner) which is the real gate. |

## 6. What "review passed" would require

If a UE toolchain is provisioned later, the module proceeds in step 2 under these
conditions — recorded here so the review is not re-litigated from scratch:

- The module is **Runtime, logic-only**, `Core`-only dependencies, no Editor
  module.
- The wire must be **byte-identical** to `JsonEnvelopeCodec`, asserted by a
  cross-language golden test against the EK-2 golden set.
- The applier must answer the **same decision and baseline** as the Unity/Godot
  `SyncApplier` on the same frames (the parity scene set).
- No UE design assets (logo/icon) are created — the no-assets rule applies.
- Tests run headless (`-nullrhi`) with **no Editor dependency**.
