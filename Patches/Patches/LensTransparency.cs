using System;
using System.Collections.Generic;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// Makes scope lens surfaces invisible by replacing their live mesh with an empty mesh.
    ///
    /// Why previous approaches failed:
    ///   - renderer.enabled = false:      EFT re-enables it, or CommandBuffer ignores it
    ///   - forceRenderingOff = true:       CommandBuffer/Graphics.DrawMesh bypasses this
    ///   - gameObject.SetActive(false):    EFT re-activates, or object was already inactive
    ///   - gameObject.layer = 31:          Graphics.DrawMesh ignores layers
    ///   - material swap to transparent:   CommandBuffer uses cached material reference
    ///
    /// What works: set MeshFilter.mesh to an EMPTY mesh.
    ///   No vertices = no triangles = nothing to draw.
    ///   CommandBuffer, Graphics.DrawMesh, DrawRenderer ALL need geometry.
    ///   Zero geometry = zero rendering. Period.
    ///
    /// Cached original lens meshes are reused by the reticle stencil pass while ADS.
    ///
    /// A hybrid/composite sight keeps its non-selected mode inactive, so the scope-enter scan cannot
    /// see that mode's lens; such surfaces are registered and adopted per frame by EnsureHidden()
    /// once they start drawing (see the LensCandidate comment for the measured evidence).
    /// </summary>
    internal static class LensTransparency
    {
        internal struct LensMaskEntry
        {
            public Renderer Renderer;
            public Mesh Mesh;
        }

        private struct HiddenEntry
        {
            public MeshFilter Filter;
            public SkinnedMeshRenderer Skinned;
            public Mesh OriginalMesh;
            public Renderer Renderer;
            public bool WasForceOff;
        }

        private static readonly List<HiddenEntry> _hidden = new List<HiddenEntry>(16);
        private static Mesh _emptyMesh;

        /// <summary>Renderers KillMesh() refused to empty (mixed-submesh meshes); used for one-shot logs.</summary>
        private static readonly HashSet<Renderer> _refusedMixedMesh = new HashSet<Renderer>();

        /// <summary>Collimator dot surfaces KillMesh() refused to empty; used for one-shot logs.</summary>
        private static readonly HashSet<Renderer> _refusedDotSurface = new HashSet<Renderer>();

        // ── The collimator dot surface: geometry stays, its dead thermal path is disabled ──────
        //
        // MEASURED (EOTech HHS-1, SPT 4.1.5, session 20260915_232202): emptying
        // `mode_001/linza_mode_001` (9 verts, shader `CW FX/Collimator`, material
        // `scope_all_eotech_exps3-4_LOD0_linza`) turned the previously black 1x collimator into a
        // clear-but-DOTLESS sight picture (user snapshots 1 vs 3 of that session; the F9 export
        // `20260915_232202_optic_232215_2.json` has that renderer as the only drawing lens surface
        // of that mode with 9 verts and `sharedThermalVisionOn=1`).
        //
        // The dot is drawn BY THAT RENDERER, from the game's own code:
        //   * `CollimatorSight.Awake()` binds `CollimatorMeshRenderer = GetComponent<MeshRenderer>()`
        //     and `CollimatorMaterial = CollimatorMeshRenderer.sharedMaterial`
        //     (Assembly-CSharp.decompiled.cs 272309-272313), and `CollimatorSight.LookAt()` rotates
        //     that node to face the camera (272325-272330, called from ScopePrefabCache at
        //     218258/218274) — the holographic parallax behaviour.
        //   * `ScopeMaskRenderer.DrawActiveCollimator()` reproduces the collimator's on-screen
        //     footprint by drawing exactly that renderer with a material built from the SAME shader
        //     (`ShadersFinder.Find("CW FX/Collimator")`) after `CopyPropertiesFromMaterial(
        //     CollimatorMaterial)` (275977-276148) — i.e. shader + mesh + material ARE the collimator
        //     image.
        //
        // And the shader's uniform set proves empting it does NOT remove anything that samples the
        // picture source this mod kills:
        //   `CW FX/Collimator` (game shader bundle
        //   `EscapeFromTarkov_Data\StreamingAssets\Windows\shaders`, properties @1345756-1346188,
        //   $Globals @1346280-1346508, tags @1348448-1348492, name @1349580) declares/exposes
        //   `_Color, _NoiseTex, _MarkTex ("Mark Texture"), _FadeTex, _MarkShift, _MarkScale, _HDR`
        //   and its const buffer holds `_MarkTex, _MarkShift, _MarkScale, _FadeTex, _NoiseTex, _HDR,
        //   _ThermalVisionOn, _WorldSpaceCameraPos, unity_ObjectToWorld/WorldToObject`.
        //   **No `_CamTex`** — unlike `CW FX/OpticSight` (the PiP screen shader), whose const buffer
        //   holds `_CamTex, _MaskTex2/_MaskTex, _Scales, _Shifts, _ShiftDirection, _SwitchToSight`
        //   (@359839732-359839908, @359845244-359845472).
        //   (Method check: the Collimator shader's tag `QUEUE Overlay+11` ⇒ material renderQueue
        //   4011, which is exactly what PiPDiag reports for that material; `Custom/OpticGlass`'s
        //   `QUEUE Overlay` ⇒ 3000, likewise. So the property/const-buffer attribution is sound.)
        //
        // ⇒ For the optic this mod has taken over, the collimator dot surface is the ONE lens
        //   surface that must keep drawing: it cannot sample the killed PiP texture, and it is the
        //   dot. The one way it can still go black is its `_ThermalVisionOn` branch (the thermal
        //   image for this path does not exist here), so that is neutralised per material instead of
        //   by deleting geometry — see ForceThermalOffForOwnedOptic().
        private static readonly List<Material> _forcedThermalMats = new List<Material>(8);
        private static readonly List<float> _forcedThermalPrev = new List<float>(8);

        // ── Image lens surfaces of the CURRENT optic ─────────────────────────────
        //
        // Every lens surface this class classified for the active optic, together with the
        // material instances it draws with. Two reasons to keep this list:
        //
        //   1. Collimator lenses are deliberately NOT emptied (the reticle dot is painted on
        //      them — see ShouldSkipForCollimator), so they stay part of the sight picture.
        //   2. `_ThermalVisionOn` is a shader GLOBAL for the game (BSG.CameraEffects.ThermalVision
        //      writes 1 in OnPreCull while thermal renders, 0 in OnPostRender/OnDisable), but a
        //      per-material value takes precedence over it. Third-party mods (e.g.
        //      BetterThermalNightVision's T7ScopeHook) write `Material.SetFloat("_ThermalVisionOn", 0)`
        //      on every MeshRenderer of the optic subtree, once per weapon initialisation
        //      (ProceduralWeaponAnimation.InitTransforms). That is a per-material latch: it is never
        //      re-evaluated for a new or changed optic instance, so an optic created before the write
        //      can keep a value that contradicts the game's actual thermal state for the rest of its life.
        //
        // A stale 0 on a lens that still draws makes that lens sample its own picture source. While
        // PiP-Disabler manages the scope that source is the vanilla optic camera — which this mod
        // suppresses — so there is nothing to sample and the surface renders black.
        //
        // RefreshImageLensThermalState() therefore mirrors the game's authoritative thermal state onto
        // the lens surfaces that are actually drawing, which is exactly the value the shader would have
        // received from the global. It is a no-op once the value agrees, and a no-op for every lens
        // surface this class emptied (those draw nothing).
        //
        // The correction is gated on the writing mod actually being loaded, so without it this class
        // never touches a shader value at all (same compat-shim convention as FikaCompat/FOVFixCompat).
        private static readonly List<Renderer> _imageLensRenderers = new List<Renderer>(8);
        private static readonly List<Material[]> _imageLensMaterials = new List<Material[]>(8);
        private static readonly int ThermalVisionOnId = Shader.PropertyToID("_ThermalVisionOn");
        private static float _thermalVisionOnApplied = float.NaN;
        private static bool? _perMaterialLensWriterPresent;

        // ── Lens surfaces of the managed optic, INCLUDING its inactive sibling modes ──────────
        //
        // Registration must not depend on activeInHierarchy.
        //
        // A hybrid/composite sight keeps its non-selected mode's GameObject inactive, so a scan that
        // runs while mode A is selected never sees mode B's lens at all — and because EnsureHidden()
        // only ever replays what was registered, that lens stays intact for the rest of the optic
        // instance's life. Measured on the EOTech HHS-1 (PiPDiag F9 export
        // `20260915_104724_optic_104737_2.json`, anchorPath …/mod_scope/scope_all_eotech_hhs_1_tan(Clone)):
        //   mode_000 (selected)  -> backLens / linza_mode_000 / scope_all_eotech_g33_glass_LOD0 all
        //                           EmptyLensMesh 0 verts, meshCleared=true, forceRenderingOff=true
        //   mode_001 (inactive)  -> linza_mode_001: 9 verts, meshCleared=false, forceRenderingOff=false,
        //                           shader CW FX/Collimator, hasSwitchToSight=FALSE (its material has no
        //                           `_SwitchToSight`, so the lens-fade path cannot hide it either), and its
        //                           node carries a CollimatorSight component while the mode node itself
        //                           carries only a Transform — no OpticSight, so no OnEnable event exists
        //                           for PiP-Disabler's scope-enter path to react to. In the whole game log
        //                           `OnEnable/ENTER: 'mode_001'` occurs for other hybrids but never for the
        //                           HHS-1 (`5c0a2cec0db834001b7ce47d`).
        // ⇒ an event-driven reclaim is impossible for this case; only the per-frame path can do it.
        //
        // So `activeInHierarchy` is only ever an "is it drawing right now?" test. Registration must not
        // depend on it, on any OpticSight event, or on the collimator classification, and hiding is
        // maintained every scoped frame by EnsureHidden() for as long as this mod owns the optic.
        //
        // Ownership: only surfaces belonging to the sight we manage are touched, so a second optic
        // mounted on the same `mod_scope` rail (its own scope_* branch) is left alone — it is handled by
        // its own pass when it becomes the managed optic.
        private struct LensCandidate
        {
            public Renderer Renderer;
            public Transform SightRoot;      // scope_* node of this surface's mode branch; null = sight-level
        }

        private static readonly List<LensCandidate> _lensCandidates = new List<LensCandidate>(16);
        private static readonly List<Renderer> _surfaceBuffer = new List<Renderer>(32);
        private static Transform _managedScopeRoot;
        private static Transform _managedSightRoot;
        private static int _nextRescanFrame;

        // Bumped every time the scan state is dropped (scope enter / scope exit / bypass /
        // shutdown). CollimatorLensGuard uses it to know that its own registration is stale and
        // has to be replayed — see RegisterSurfaceForOwnedSight().
        private static int _scanGeneration;

        /// <summary>
        /// How often the taken-over scope root is re-walked while scoped, so a surface that did not
        /// exist when the scope was entered is still covered. ~2 s at 60 fps; the walk reuses a
        /// cached List, and the per-frame path stays allocation-free.
        /// </summary>
        private const int RescanIntervalFrames = 120;

        private static Mesh GetEmptyMesh()
        {
            if (_emptyMesh != null) return _emptyMesh;
            _emptyMesh = new Mesh();
            _emptyMesh.name = "EmptyLensMesh";
            // Zero vertices, zero triangles. Nothing to render.
            return _emptyMesh;
        }

        /// <summary>
        /// Full cleanup for shutdown or mod disable.
        /// </summary>
        public static void FullRestoreAll()
        {
            CollimatorLensGuard.Reset();
            RestoreAll();
        }

        /// <summary>
        /// Called on scope enter. Finds and empties ALL lens surfaces.
        /// Searches from an expanded root to catch glass/lens meshes above scopeRoot.
        /// </summary>
        public static void HideAllLensSurfaces(OpticSight os)
        {
            if (os == null) return;

            Transform searchRoot = FindScopeSearchRoot(os.transform);

            // Always dump hierarchy on first enter
            DumpHierarchy(searchRoot);

            // Re-collect the scan state for the optic taking over now, so a newly instantiated
            // optic instance is never left with another instance's stale state.
            ForgetScopeScanState();

            _managedScopeRoot = searchRoot;
            _managedSightRoot = ResolveSightRoot(FindModeNode(os.transform, searchRoot), searchRoot);

            // Register EVERY lens surface under the scope root (inactive ones included) and empty the
            // ones that are drawing right now. Surfaces that are not drawing yet are adopted later by
            // EnsureHidden() the moment they start — see the LensCandidate comment above.
            var allRenderers = searchRoot.GetComponentsInChildren<Renderer>(true);
            int killed = 0;
            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                if (!IsLensSurfaceRenderer(r)) continue;

                Transform sightRoot = RememberLensCandidate(r);
                RememberImageLensSurface(r);

                if (!IsEligibleNow(r, sightRoot)) continue;

                // The collimator dot surface keeps its geometry on EVERY path (scan, adoption,
                // owner-guard registration): it draws the reticle dot and does not sample the killed
                // picture source. Its thermal branch is neutralised by
                // ForceThermalOffForOwnedOptic() instead — see the _refusedDotSurface comment.
                if (IsCollimatorDotSurface(r) && !IsTracked(r))
                {
                    if (_refusedDotSurface.Add(r))
                    {
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Keeping the collimator dot surface '{r.gameObject.name}'" +
                            " (CW FX/Collimator draws the reticle dot; no _CamTex) — its dead thermal" +
                            " path is disabled instead of deleting the dot");
                    }
                    continue;
                }

                if (!IsTracked(r)) killed++;
                KillMesh(r);
            }

            // Also handle the specific LensRenderer (belt-and-suspenders). KillMesh itself refuses a
            // collimator dot surface, so this path cannot delete the dot either.
            try
            {
                var lens = os.LensRenderer;
                if (lens != null)
                {
                    Transform sightRoot = RememberLensCandidate(lens);
                    RememberImageLensSurface(lens);

                    if (IsEligibleNow(lens, sightRoot) && !IsCollimatorDotSurface(lens))
                    {
                        if (!IsTracked(lens)) killed++;
                        KillMesh(lens);
                    }
                }
            }
            catch { }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] Destroyed geometry on {killed} lens surfaces (searchRoot='{searchRoot.name}')" +
                $" registered={_lensCandidates.Count} managedSightRoot='{(_managedSightRoot != null ? _managedSightRoot.name : "null")}'");
        }

        /// <summary>Drops everything recorded about the optic being managed (forces a re-scan/re-assert).</summary>
        private static void ForgetScopeScanState()
        {
            _imageLensRenderers.Clear();
            _imageLensMaterials.Clear();
            _thermalVisionOnApplied = float.NaN;

            _lensCandidates.Clear();
            _managedScopeRoot = null;
            _managedSightRoot = null;
            _nextRescanFrame = 0;
            _scanGeneration++;
            _refusedMixedMesh.Clear();
            _refusedDotSurface.Clear();
        }

        /// <summary>Nearest ancestor-or-self named "mode*", stopped at the scope search root.</summary>
        private static Transform FindModeNode(Transform t, Transform searchRoot)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                string n = cur.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("mode", StringComparison.OrdinalIgnoreCase))
                    return cur;

                if (cur == searchRoot) break;
            }

            return null;
        }

        /// <summary>
        /// The scope_* node a mode branch belongs to: the highest "scope*"-named ancestor below the
        /// scope search root, falling back to the mode node's parent. This is what separates
        /// "a sibling mode of the sight we manage" (ours to empty) from "a different optic mounted on
        /// the same rail" (not ours — that optic gets its own pass when it becomes the managed one,
        /// and its lens is untouched while it is inactive).
        /// </summary>
        internal static Transform ResolveSightRoot(Transform modeNode, Transform searchRoot)
        {
            if (modeNode == null) return null;

            Transform best = modeNode.parent;

            for (var cur = modeNode.parent; cur != null && cur != searchRoot; cur = cur.parent)
            {
                string n = cur.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("scope", StringComparison.OrdinalIgnoreCase))
                    best = cur;
            }

            return best != null ? best : modeNode;
        }

        /// <summary>
        /// Records one lens surface of the managed scope as a candidate for later adoption.
        /// Returns the scope_* node it belongs to (null when it sits at sight level, e.g. the
        /// Elcan's backLens / *_glass_LOD0 which are siblings of its mode nodes).
        /// </summary>
        private static Transform RememberLensCandidate(Renderer r)
        {
            if (r == null) return null;

            for (int i = 0; i < _lensCandidates.Count; i++)
                if (_lensCandidates[i].Renderer == r) return _lensCandidates[i].SightRoot;

            Transform sightRoot = ResolveSightRoot(FindModeNode(r.transform, _managedScopeRoot), _managedScopeRoot);

            _lensCandidates.Add(new LensCandidate
            {
                Renderer = r,
                SightRoot = sightRoot,
            });

            return sightRoot;
        }

        /// <summary>
        /// True when this surface belongs to the sight we manage and is part of the image right now.
        /// A surface inside another optic's branch is never ours while we manage this one.
        /// </summary>
        private static bool IsEligibleNow(Renderer r, Transform sightRoot)
        {
            if (r == null) return false;

            bool active;
            try { active = r.gameObject.activeInHierarchy; }
            catch { return false; }

            if (!active) return false;

            if (sightRoot != null && _managedSightRoot != null && sightRoot != _managedSightRoot)
                return false;

            return true;
        }

        /// <summary>
        /// Maintains the invariant for the optic this mod has taken over: every surface of THAT optic
        /// that is drawing right now must be hidden — whichever mode it belongs to, whether any
        /// OpticSight event ever fired for it, and whether the scope-enter scan saw it. Called every
        /// scoped frame from EnsureHidden(); allocation-free on the steady path.
        ///
        /// The collimator classification is deliberately not consulted here: this mod suppressed the
        /// optic camera a collimator lens would sample its picture from, so for the optic we own,
        /// leaving such a surface drawing means a black sight.
        /// </summary>
        private static void AdoptNewlyEligibleLensSurfaces()
        {
            // Re-walk the taken-over root occasionally so a surface that did not exist yet at
            // scope-enter is still covered — the invariant must not depend on an event having fired.
            if (_managedScopeRoot != null && Time.frameCount >= _nextRescanFrame)
            {
                _nextRescanFrame = Time.frameCount + RescanIntervalFrames;

                int added = RegisterSurfacesUnder(_managedScopeRoot);
                if (added > 0)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Re-scan registered {added} new surface(s) under" +
                        $" '{_managedScopeRoot.name}'");
                }
            }

            for (int i = 0; i < _lensCandidates.Count; i++)
            {
                var c = _lensCandidates[i];
                if (c.Renderer == null) continue;
                if (!IsEligibleNow(c.Renderer, c.SightRoot)) continue;

                if (!IsTracked(c.Renderer))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Adopting lens surface '{c.Renderer.gameObject.name}'" +
                        " (started drawing while this mod owns the scope)");
                }

                KillMesh(c.Renderer);   // no-op when already tracked
            }
        }

        /// <summary>
        /// Registers every lens surface found under the taken-over scope root (inactive ones included)
        /// and returns how many were newly registered. Uses a reused List, so no per-call array
        /// allocation on the steady path.
        /// </summary>
        private static int RegisterSurfacesUnder(Transform root)
        {
            if (root == null) return 0;

            int before = _lensCandidates.Count;

            try
            {
                _surfaceBuffer.Clear();
                root.GetComponentsInChildren(true, _surfaceBuffer);
            }
            catch
            {
                return 0;
            }

            for (int i = 0; i < _surfaceBuffer.Count; i++)
            {
                var r = _surfaceBuffer[i];
                if (r == null) continue;
                if (!IsLensSurfaceRenderer(r)) continue;

                RememberImageLensSurface(r);
                RememberLensCandidate(r);
            }

            return _lensCandidates.Count - before;
        }

        // ===== Entry points for the event-free owner path (CollimatorLensGuard) =====
        //
        // The scope-enter scan is OpticSight-driven, so it never runs for the failing case this
        // class was extended for: a 1x collimator mode whose ScopeModeInfo has no OpticSight at all
        // (measured: Walther MRS on mod_scope/scope_all_eotech_hhs_1(Clone)/mode_000,
        // currentModHasOptics=false, hasCollimators=true, and zero OnEnable/OpticSight events for
        // the whole raid). CollimatorLensGuard drives these three methods instead.

        /// <summary>
        /// True when the scan state was dropped since the caller last registered (its own
        /// registration is therefore stale and must be replayed). Allocation-free.
        /// </summary>
        internal static bool NeedsOwnerRegistration(int registeredForGeneration)
            => _scanGeneration != registeredForGeneration || _lensCandidates.Count == 0;

        internal static int ScanGeneration => _scanGeneration;

        /// <summary>
        /// Registers every lens surface under <paramref name="root"/> for the sight owned by
        /// <paramref name="sightRoot"/> and empties the ones drawing right now. Records the scope
        /// root / sight root as the managed ones so the per-frame adoption path covers surfaces that
        /// only start drawing later (a hybrid's other mode).
        /// </summary>
        internal static int RegisterOwnedSightSurfaces(Transform root, Transform sightRoot)
        {
            if (root == null || sightRoot == null) return 0;

            _managedScopeRoot = root;
            _managedSightRoot = sightRoot;

            // Seed the managed roots before the walk so RememberLensCandidate() attributes each
            // surface to the right sight branch (this is the existing ownership rule: a surface
            // whose scope_* node is not the managed one is never ours while we manage this sight).
            int registered = RegisterSurfacesUnder(root);

            AdoptNewlyEligibleLensSurfaces();

            return registered;
        }

        /// <summary>Forces the periodic re-walk on the next EnsureHidden() (anchor/sight changed).</summary>
        internal static void ForceNextRescan()
        {
            _nextRescanFrame = 0;
        }

        // ===== Forced thermal-off for the collimator dot surface =====

        /// <summary>
        /// While this mod owns the vanilla optic camera, the collimator dot surface of that optic must
        /// not take the game's thermal branch: the thermal image for that path is exactly what the
        /// take-over removed, and taking the branch renders the dot — and the whole lens it is drawn
        /// on — black. The dot surface cannot be emptied instead (emptying it deletes the reticle dot,
        /// CW FX/Collimator has no `_CamTex`, see the _refusedDotSurface comment above), so this method
        /// claims it per material.
        ///
        /// `_ThermalVisionOn` is written by the game as a shader GLOBAL (ThermalVision.OnPreCull →
        /// 1, OnPostRender/OnDisable/OnDestroy → 0 — decompiled 278920/278955), and a per-material
        /// value takes precedence for a surface whose material exposes the property (measured: the
        /// CW FX/Collimator and CW FX/OpticLens materials report `HasProperty(_ThermalVisionOn)=true`,
        /// the CW FX/OpticSight one does not — hence emptying is the only remedy for the latter).
        /// Outside thermal the global is 0, so writing 0 changes nothing except in exactly the state
        /// this exists for; the previous value is saved and put back by RestoreForcedThermalOff().
        ///
        /// Only the MANAGED sight is touched (a second optic on the same rail — a canted sight — is a
        /// different scope_* branch and stays untouched), and only while the dot surface draws.
        /// Allocation-free on the steady path: the material list is built once and afterwards it is
        /// one GetFloat compare per material, plus one ownership comparison per candidate.
        /// </summary>
        internal static void ForceThermalOffForOwnedOptic()
        {
            ReassertForcedThermalOff();

            if (_managedSightRoot == null) return;

            // 2) Claim the dot surface of the managed sight.
            for (int i = 0; i < _lensCandidates.Count; i++)
            {
                var c = _lensCandidates[i];
                var r = c.Renderer;
                if (!(r == null) && !(c.SightRoot != _managedSightRoot) && !IsTracked(r) &&
                    IsCollimatorDotSurface(r) && IsEligibleNow(r, c.SightRoot))
                {
                    ForceThermalOffOnRenderer(r);
                }
            }
        }

        /// <summary>
        /// Per-frame-safe half of <see cref="ForceThermalOffForOwnedOptic"/>: only the materials that
        /// were already claimed are re-checked (one `GetFloat` compare each, no Unity component
        /// searches), so another writer cannot put the dot surface back on the thermal branch between
        /// the (throttled) full passes.
        /// </summary>
        internal static void ReassertForcedThermalOff()
        {
            for (int i = 0; i < _forcedThermalMats.Count; i++)
            {
                var m = _forcedThermalMats[i];
                if (m == null) continue;

                try
                {
                    if (m.GetFloat(ThermalVisionOnId) != 0f)
                        m.SetFloat(ThermalVisionOnId, 0f);
                }
                catch { }
            }
        }

        private static void ForceThermalOffOnRenderer(Renderer r)
        {
            Material[] mats;
            try { mats = r.sharedMaterials; }
            catch { return; }

            if (mats == null) return;

            for (int j = 0; j < mats.Length; j++)
            {
                var m = mats[j];
                if (m == null) continue;

                bool has;
                try { has = m.HasProperty(ThermalVisionOnId); }
                catch { continue; }

                if (!has) continue;                 // cannot be overridden per material → leave as is
                if (IsThermalForced(m)) continue;    // already claimed

                float prev;
                try { prev = m.GetFloat(ThermalVisionOnId); }
                catch { continue; }

                try { m.SetFloat(ThermalVisionOnId, 0f); }
                catch { continue; }

                _forcedThermalMats.Add(m);
                _forcedThermalPrev.Add(prev);

                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency] Thermal branch forced OFF on the collimator dot surface" +
                    $" '{r.gameObject.name}' (material '{m.name}', _ThermalVisionOn {prev} → 0)" +
                    " — geometry kept, so the reticle dot stays drawn");
            }
        }

        private static bool IsThermalForced(Material m)
        {
            if (m == null) return false;

            for (int i = 0; i < _forcedThermalMats.Count; i++)
                if (_forcedThermalMats[i] == m) return true;

            return false;
        }

        /// <summary>True while any collimator dot material is being held at `_ThermalVisionOn = 0`.</summary>
        internal static bool HasForcedThermalOff => _forcedThermalMats.Count > 0;

        /// <summary>
        /// Puts every `_ThermalVisionOn` this class forced back to the value it had when the
        /// take-over began. Idempotent, and called from every release path (guard release,
        /// RestoreAll / scope exit / bypass, FullRestoreAll on shutdown or mod disable), so the
        /// material state can never outlive the take-over it belongs to.
        /// </summary>
        internal static void RestoreForcedThermalOff()
        {
            if (_forcedThermalMats.Count == 0) return;

            for (int i = 0; i < _forcedThermalMats.Count; i++)
            {
                var m = _forcedThermalMats[i];
                if (m == null) continue;

                try { m.SetFloat(ThermalVisionOnId, _forcedThermalPrev[i]); }
                catch { }
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] Restored _ThermalVisionOn on {_forcedThermalMats.Count}" +
                " collimator dot material(s)");

            _forcedThermalMats.Clear();
            _forcedThermalPrev.Clear();
        }

        /// <summary>True when this renderer is already registered as hidden (used for honest logging).</summary>
        private static bool IsTracked(Renderer r)
        {
            if (r == null) return false;

            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i].Renderer == r) return true;

            return false;
        }

        /// <summary>Records one lens surface of the current optic plus the materials it draws with.</summary>
        private static void RememberImageLensSurface(Renderer r)
        {
            if (r == null) return;

            for (int i = 0; i < _imageLensRenderers.Count; i++)
                if (_imageLensRenderers[i] == r) return;

            Material[] mats;
            try { mats = r.sharedMaterials; }
            catch { mats = null; }

            _imageLensRenderers.Add(r);
            _imageLensMaterials.Add(mats);
        }

        /// <summary>
        /// Mirrors the game's thermal state onto the lens surfaces of the current optic that are
        /// still drawing, so a stale per-material <c>_ThermalVisionOn</c> (left by another mod on a
        /// per-instance, once-per-weapon-init basis) cannot outlive the state it was written for.
        ///
        /// Only the surfaces that currently have geometry are touched: every lens surface this class
        /// emptied draws nothing, so writing to it could not change the image. The value written is the
        /// one the shader would otherwise receive from the global, so when no third-party writer is
        /// present this is behaviour-preserving.
        ///
        /// It is additionally gated on the writing mod actually being loaded, so on a clean install this
        /// method never touches a shader value at all.
        ///
        /// Allocation-free on the steady path (a single float compare when nothing changed).
        /// </summary>
        public static void RefreshImageLensThermalState(bool thermalRendering)
        {
            float want = thermalRendering ? 1f : 0f;
            if (want == _thermalVisionOnApplied)
                return;

            if (!PerMaterialLensWriterPresent())
            {
                _thermalVisionOnApplied = want;
                return;
            }

            _thermalVisionOnApplied = want;

            for (int i = 0; i < _imageLensRenderers.Count; i++)
            {
                var r = _imageLensRenderers[i];
                var mats = _imageLensMaterials[i];
                if (r == null || mats == null) continue;

                if (!DrawsGeometry(r)) continue;

                for (int j = 0; j < mats.Length; j++)
                {
                    var m = mats[j];
                    if (m == null) continue;

                    // A collimator dot surface held at "thermal off" by the owner guard must not be
                    // re-latched to 1 here: that write is exactly what renders the dot surface black
                    // while this mod holds the picture source down.
                    if (IsThermalForced(m)) continue;

                    try { m.SetFloat(ThermalVisionOnId, want); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// True when a mod that writes a one-shot per-material <c>_ThermalVisionOn</c> latch onto optic
        /// renderers is loaded (BetterThermalNightVision's <c>BetterVision.T7ScopeHook</c>). Resolved
        /// once and cached; same compat-shim convention as FikaCompat/FOVFixCompat.
        /// </summary>
        private static bool PerMaterialLensWriterPresent()
        {
            if (_perMaterialLensWriterPresent.HasValue)
                return _perMaterialLensWriterPresent.Value;

            bool present;
            try { present = AccessTools.TypeByName("BetterVision.T7ScopeHook") != null; }
            catch { present = false; }

            _perMaterialLensWriterPresent = present;

            PiPDisablerPlugin.DebugLogInfo(
                present
                    ? "[LensTransparency] BetterThermalNightVision per-material _ThermalVisionOn writer detected" +
                      " — re-asserting the thermal state per optic instance."
                    : "[LensTransparency] No per-material _ThermalVisionOn writer loaded — lens thermal state left untouched.");

            return present;
        }

        /// <summary>True when the renderer currently has real geometry (its lens mesh was not emptied).</summary>
        private static bool DrawsGeometry(Renderer r)
        {
            try
            {
                if (r is SkinnedMeshRenderer smr)
                    return smr.sharedMesh != null && smr.sharedMesh.vertexCount > 0;

                var mf = r.GetComponent<MeshFilter>();
                return mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Per-frame: re-apply the empty mesh if EFT restores geometry.
        /// Accepts an optional exclusion renderer so this can safely run every frame while scoped.
        /// </summary>
        public static void EnsureHidden(Renderer excludeRenderer = null)
        {
            // Adopt any registered lens surface that only started drawing after the scan
            // (a hybrid sight's other mode selected mid-raid — see _lensCandidates).
            AdoptNewlyEligibleLensSurfaces();

            var emptyMesh = GetEmptyMesh();
            for (int i = 0; i < _hidden.Count; i++)
            {
                var e = _hidden[i];
                if (excludeRenderer != null && e.Renderer == excludeRenderer)
                    continue;

                if (e.Skinned != null && e.Skinned.sharedMesh != emptyMesh)
                {
                    e.Skinned.sharedMesh = emptyMesh;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Re-emptied skinned mesh on '{e.Skinned.gameObject.name}'");
                }
                else if (e.Filter != null && e.Filter.sharedMesh != emptyMesh)
                {
                    e.Filter.sharedMesh = emptyMesh;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Re-emptied mesh on '{e.Filter.gameObject.name}'");
                }
                if (e.Renderer != null)
                {
                    // Re-enforce forceRenderingOff (lightweight bool check, no alloc)
                    if (!e.Renderer.forceRenderingOff)
                        e.Renderer.forceRenderingOff = true;
                }
            }
        }

        /// <summary>
        /// Restore all. Called on scope exit.
        ///
        /// Also drops any `_ThermalVisionOn` this class forced on the collimator dot surface: that
        /// material state belongs to the take-over and must never outlive it (the take-over is over
        /// the moment the lens geometry is handed back).
        /// </summary>
        public static void RestoreAll()
        {
            RestoreForcedThermalOff();
            ForgetScopeScanState();

            if (_hidden.Count == 0) return;

            for (int i = 0; i < _hidden.Count; i++)
            {
                var e = _hidden[i];
                try
                {
                    // Restore original mesh geometry so the lens body is visible again.
                    if (e.Skinned != null && e.OriginalMesh != null)
                    {
                        e.Skinned.sharedMesh = e.OriginalMesh;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored skinned mesh on '{e.Skinned.gameObject.name}' → {e.OriginalMesh.vertexCount} verts");
                    }
                    else if (e.Filter != null && e.OriginalMesh != null)
                    {
                        e.Filter.sharedMesh = e.OriginalMesh;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored mesh on '{e.Filter.gameObject.name}' → {e.OriginalMesh.vertexCount} verts");
                    }

                    if (e.Renderer != null)
                    {
                        e.Renderer.forceRenderingOff = e.WasForceOff;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored renderer '{e.Renderer.gameObject.name}' forceOff={e.WasForceOff}");
                    }
                }
                catch { }
            }

            PiPDisablerPlugin.DebugLogInfo($"[LensTransparency] Restored {_hidden.Count} lens meshes");
            _hidden.Clear();
        }

        // ===== Core =====

        private static void KillMesh(Renderer r)
        {
            if (r == null) return;

            // Already tracked?
            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i].Renderer == r) return;

            // Never empty the collimator dot surface, whichever path asks for it (scope-enter scan,
            // per-frame adoption, or the event-free owner guard). Belt and braces on top of the
            // call-site exemption: the dot is painted by this very renderer's mesh + material, and
            // its shader cannot sample the killed PiP texture at all. See _refusedDotSurface.
            if (IsCollimatorDotSurface(r))
            {
                if (_refusedDotSurface.Add(r))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Refused to empty '{r.gameObject.name}': it is the" +
                        " collimator dot surface (CW FX/Collimator / CollimatorSight) — emptying it" +
                        " deletes the reticle dot without removing anything that samples _CamTex");
                }
                return;
            }

            // Refuse to empty a mesh that is not a pure lens surface. `IsLensSurfaceRenderer`
            // classifies by name/shader and can, for a hypothetical prefab, match a mesh that also
            // carries the optic's visible shell on another submesh — the measured shape is the EOTech
            // EXPS3 (`scope_all_eotech_exps3_LOD0`: one mesh, submesh 0 = housing with
            // `p0/Reflective/Bumped Specular SMap`, submesh 1 = glass with `Custom/OpticGlass`), where
            // emptying the mesh destroys the housing. Belt and braces: that renderer is already
            // skipped by the name rules today, so nothing changes unless a future prefab slips
            // through, and then this fails closed (leaves the geometry alone, logs once).
            if (!IsPureLensSurface(r))
            {
                // Remember the refusal so this stays a one-line note per renderer instead of a
                // per-frame log (AdoptNewlyEligibleLensSurfaces re-offers every active candidate
                // every scoped frame).
                if (_refusedMixedMesh.Add(r))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Refused to empty '{r.gameObject.name}': the mesh carries more" +
                        " than the lens (mixed submeshes) — emptying it would delete visible housing");
                }
                return;
            }

            var mf = r.GetComponent<MeshFilter>();
            var smr = r as SkinnedMeshRenderer;
            Mesh origMesh = null;

            if (smr != null)
            {
                origMesh = smr.sharedMesh;
                smr.sharedMesh = GetEmptyMesh();
            }
            else if (mf != null)
            {
                origMesh = mf.sharedMesh;
                mf.sharedMesh = GetEmptyMesh();
            }

            var entry = new HiddenEntry
            {
                Filter = mf,
                Skinned = smr,
                OriginalMesh = origMesh,
                Renderer = r,
                WasForceOff = r.forceRenderingOff,
            };
            _hidden.Add(entry);

            // Keep renderer enabled state untouched; hybrid sights can manage this
            // dynamically between optic/collimator modes.
            r.forceRenderingOff = true;

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] MESH DESTROYED: '{r.gameObject.name}' " +
                $"(had {(origMesh != null ? origMesh.vertexCount.ToString() : "?")} verts → 0) " +
                $"MeshFilter={(mf != null ? "yes" : "NO")}, Skinned={(smr != null ? "yes" : "NO")}");
        }

        /// <summary>
        /// Determines if a Renderer is a lens/glass surface that should be hidden.
        ///
        /// Detection layers:
        ///   1. GameObject name patterns (linza, backlens, back_lens, glass, frontlens, front_linza)
        ///   2. Mesh name patterns (glass, linza)
        ///   3. Shader name patterns (CW FX/OpticSight, CW FX/BackLens)
        ///   4. Material name patterns (linza, glass, lens)
        ///
        /// Uses OrdinalIgnoreCase to avoid ToLowerInvariant() string allocations.
        /// </summary>
        internal static bool IsLensSurfaceRenderer(Renderer r)
        {
            // --- Layer 1: GameObject name ---
            var goName = r.gameObject.name;
            if (!string.IsNullOrEmpty(goName))
            {
                if (ContainsCI(goName, "linza") ||
                    ContainsCI(goName, "backlens") || ContainsCI(goName, "back_lens") ||
                    ContainsCI(goName, "frontlens") || ContainsCI(goName, "front_lens") ||
                    ContainsCI(goName, "front_linza"))
                    return true;

                // "glass" only when it looks like a scope lens (not "fiberglass" etc.)
                if (ContainsCI(goName, "glass") &&
                    (ContainsCI(goName, "lod") || ContainsCI(goName, "scope") || ContainsCI(goName, "optic")))
                    return true;
            }

            // --- Layer 2: Mesh name ---
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var meshName = mf.sharedMesh.name;
                if (!string.IsNullOrEmpty(meshName))
                {
                    if (ContainsCI(meshName, "linza") ||
                        ContainsCI(meshName, "_glass_") ||
                        ContainsCI(meshName, "_glass_lod"))
                        return true;
                }
            }

            // --- Layer 3 & 4: Shader name + Material name ---
            try
            {
                var mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;

                        var shaderName = m.shader?.name ?? "";
                        if (shaderName == "CW FX/OpticSight" ||
                            shaderName == "CW FX/BackLens" ||
                            shaderName.IndexOf("OpticSight", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            shaderName.IndexOf("BackLens", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;

                        // Material name (e.g. "_LOD0_linza", "*_lens*")
                        var matName = m.name ?? "";
                        if (!string.IsNullOrEmpty(matName))
                        {
                            if (ContainsCI(matName, "linza") || ContainsCI(matName, "_lens"))
                                return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>Zero-allocation case-insensitive Contains.</summary>
        private static bool ContainsCI(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when the WHOLE mesh of this renderer is a lens/imaging surface, so replacing its
        /// geometry with the empty mesh cannot remove anything else.
        ///
        /// This is the guard against the measured trap: `scope_all_eotech_exps3_LOD0` is ONE mesh
        /// with TWO submeshes — index 0 is the EXPS3 housing (`p0/Reflective/Bumped Specular SMap`)
        /// and index 1 is its glass (`Custom/OpticGlass`). Emptying that mesh destroys the holo's
        /// visible shell. Every renderer the scan actually selects today is single-submesh
        /// (backLens / linza_* / *_glass_*_LOD0 / *_glass_pos_*_LOD0 all report subMeshCount=1), so
        /// this check costs nothing in practice and only ever refuses a mesh that is not a pure lens.
        ///
        /// A mesh with no mesh-like material list at all (materials not readable) is treated as NOT
        /// safe, because nothing proves it is a pure lens surface.
        /// </summary>
        internal static bool IsPureLensSurface(Renderer r)
        {
            if (r == null) return false;

            Mesh mesh;
            try
            {
                if (r is SkinnedMeshRenderer smr)
                    mesh = smr.sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
            }
            catch { return false; }

            if (mesh == null)
                return false;   // nothing to empty; nothing to protect either

            int subMeshes;
            try { subMeshes = mesh.subMeshCount; }
            catch { return false; }

            if (subMeshes <= 1)
                return true;

            // More than one submesh: only safe when EVERY submesh draws with a lens shader.
            Material[] mats;
            try { mats = r.sharedMaterials; }
            catch { return false; }

            if (mats == null || mats.Length < subMeshes)
                return false;

            for (int i = 0; i < subMeshes; i++)
            {
                var m = mats[i];
                if (m == null) return false;

                string shaderName = null;
                try { shaderName = m.shader != null ? m.shader.name : null; }
                catch { }

                if (!IsLensShaderName(shaderName))
                    return false;
            }

            return true;
        }

        /// <summary>The shader families that make a surface part of the sight's optical image.</summary>
        internal static bool IsLensShaderName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
                return false;

            return ContainsCI(shaderName, "OpticSight")
                || ContainsCI(shaderName, "BackLens")
                || ContainsCI(shaderName, "OpticLens")
                || ContainsCI(shaderName, "Collimator");
        }

        private static bool LooksLikeLensName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return ContainsCI(name, "linza")
                   || ContainsCI(name, "lens")
                   || ContainsCI(name, "glass");
        }


        private static Transform FindScopeSearchRoot(Transform t)
        {
            if (t == null) return null;

            // Prefer a parent that contains the mode_* branches (common in hybrid sights)
            Transform best = t;
            Transform cur = t;

            for (int depth = 0; cur != null && depth < 10; depth++, cur = cur.parent)
            {
                string n = cur.name ?? "";

                // Typical container name
                if (ContainsCI(n, "mod_scope"))
                    best = cur;

                // Or a parent that contains mode_* children
                int modeChildren = 0;
                for (int i = 0; i < cur.childCount; i++)
                {
                    var c = cur.GetChild(i);
                    if (c != null && c.name != null &&
                        c.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase))
                        modeChildren++;
                }
                if (modeChildren >= 1)
                    best = cur;

                // Don’t climb into the whole weapon/player hierarchy
                if (ContainsCI(n, "weapon") || ContainsCI(n, "player") || ContainsCI(n, "hands"))
                    break;
            }

            return best ?? t;
        }

        /// <summary>
        /// True when this renderer IS the collimator (holographic/red-dot) image surface: it sits on
        /// a node carrying a <see cref="CollimatorSight"/> and/or draws with a `CW FX/Collimator`
        /// material. Such a surface paints the reticle dot onto itself, so it must never be emptied —
        /// see the _refusedDotSurface comment for the decompiled/shader evidence.
        /// </summary>
        internal static bool IsCollimatorDotSurface(Renderer r)
        {
            if (r == null) return true;

            try
            {
                if (r.GetComponentInParent<CollimatorSight>(true) != null)
                    return true;
            }
            catch { }

            try
            {
                var mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var shaderName = mats[i]?.shader?.name;
                        if (!string.IsNullOrEmpty(shaderName) &&
                            shaderName.IndexOf("Collimator", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Returns cached lens meshes for the stencil pass while the live lens renderers
        /// stay empty-meshed during ADS.
        /// </summary>
        public static List<LensMaskEntry> CollectLensMaskEntries(OpticSight os)
        {
            var result = new List<LensMaskEntry>();
            if (os == null) return result;

            Transform searchRoot = FindScopeSearchRoot(os.transform);
            for (int i = 0; i < _hidden.Count; i++)
            {
                var entry = _hidden[i];
                if (entry.Renderer == null || entry.OriginalMesh == null) continue;
                if (!entry.Renderer.gameObject.activeInHierarchy) continue;
                if (entry.Renderer.transform != searchRoot && !entry.Renderer.transform.IsChildOf(searchRoot)) continue;

                result.Add(new LensMaskEntry
                {
                    Renderer = entry.Renderer,
                    Mesh = entry.OriginalMesh,
                });

                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency] LensMask +mesh: go='{entry.Renderer.gameObject.name}'" +
                    $" mesh='{entry.OriginalMesh.name}' verts={entry.OriginalMesh.vertexCount}");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] CollectLensMaskEntries: {result.Count} entry(s)" +
                $" (searchRoot='{searchRoot?.name ?? "null"}' from '{os.name}')");

            return result;
        }

        /// <summary>
        /// Returns all active, non-lens renderers in the scope hierarchy.
        /// These are the scope housing/body meshes that should act as a stencil
        /// mask so the reticle is hidden wherever the housing covers screen-centre.
        /// </summary>
        public static List<Renderer> CollectHousingRenderers(OpticSight os)
        {
            var result = new List<Renderer>();
            if (os == null) return result;

            Transform searchRoot = FindScopeSearchRoot(os.transform);
            var allRenderers = searchRoot.GetComponentsInChildren<Renderer>(true);

            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (IsLensSurfaceRenderer(r)) continue;
                if (IsCollimatorDotSurface(r)) continue;

                // Must have real geometry (lens renderers are already empty-meshed, but
                // check explicitly so we don't add zero-vert renderers to the mask pass).
                var mf  = r.GetComponent<MeshFilter>();
                var smr = r as SkinnedMeshRenderer;
                Mesh mesh = mf?.sharedMesh ?? smr?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;

                string meshName = mesh.name ?? string.Empty;
                string goName = r.gameObject.name ?? string.Empty;

                if (ContainsCI(meshName, "LOD1"))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] HousingMask -skip LOD1 mesh: go='{goName}' mesh='{meshName}'");
                    continue;
                }

                if (!IsForcedScopeBodyMaskRenderer(goName, meshName) &&
                    (LooksLikeLensName(meshName) || LooksLikeLensName(goName)))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] HousingMask -skip lens-like mesh: go='{goName}' mesh='{meshName}'");
                    continue;
                }

                result.Add(r);
                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency] HousingMask +renderer: go='{r.gameObject.name}'" +
                    $" mesh='{mesh.name}' verts={mesh.vertexCount}" +
                    $" shader='{(r.sharedMaterial?.shader?.name ?? "null")}'");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] CollectHousingRenderers: {result.Count} renderer(s)" +
                $" (searchRoot='{searchRoot?.name ?? "null"}' from '{os.name}')");

            return result;
        }

        private static bool IsForcedScopeBodyMaskRenderer(string goName, string meshName)
        {
            return string.Equals(goName, "scope_lens_front.006", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(meshName, "scope_lens_front.006", StringComparison.OrdinalIgnoreCase);
        }

        // ===== Diagnostics =====

        private static bool _dumpedOnce;
        private static void DumpHierarchy(Transform root)
        {
            if (_dumpedOnce)
                return;
            _dumpedOnce = true;

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] === SCOPE HIERARCHY DUMP: '{root.name}' ===");

            var allRenderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in allRenderers)
            {
                if (r == null) continue;

                string matInfo = "";
                try
                {
                    var mats = r.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var m = mats[i];
                            if (m == null) { parts.Add("null"); continue; }
                            parts.Add($"{m.name}[shader={m.shader?.name ?? "?"}]");
                        }
                        matInfo = string.Join("; ", parts);
                    }
                }
                catch { matInfo = "(error)"; }

                var mf = r.GetComponent<MeshFilter>();
                int verts = -1;
                string meshName = "";
                if (mf != null && mf.sharedMesh != null)
                {
                    verts = mf.sharedMesh.vertexCount;
                    meshName = mf.sharedMesh.name ?? "";
                }

                bool isLens = IsLensSurfaceRenderer(r);
                string path = GetRelativePath(r.transform, root);

                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency]   {(isLens ? "★LENS★" : "      ")} " +
                    $"'{path}' mesh='{meshName}' verts={verts} enabled={r.enabled} " +
                    $"active={r.gameObject.activeSelf} layer={r.gameObject.layer} " +
                    $"mats=[{matInfo}]");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] === END DUMP ({allRenderers.Length} renderers) ===");
        }

        private static string GetRelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent)
                parts.Add(cur.name ?? "?");
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
