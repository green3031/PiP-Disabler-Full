using System;
using System.Collections.Generic;
using EFT.CameraControl;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// The four-plane radii/positions handed to <see cref="MeshPlaneCutter.CutMeshFrustum"/>,
    /// plus the three parameters that are NOT derived (they keep their hand-tuned / per-scope
    /// values in both modes).
    /// </summary>
    internal struct CutProfileParams
    {
        public float NearRadius;        // plane 1 (t = 0)
        public float MidRadius;         // plane 2
        public float MidPosition;       // plane 2, normalised over CutLength
        public float Plane3Radius;
        public float Plane3Position;    // normalised over CutLength
        public float Plane4Radius;
        public float Plane4Position;    // normalised over CutLength
        public float StartOffset;       // unchanged in auto mode
        public float CutLength;         // unchanged in auto mode
        public float NearPreserveDepth; // unchanged in auto mode
    }

    /// <summary>
    /// Derives the mesh-surgery cut profile from the optic's own geometry instead of the
    /// hand-tuned 4.0.13 per-scope constants.
    ///
    /// WHY
    ///   The shipped per-scope numbers were tuned by the original author against the 4.0.13
    ///   optic prefabs. In 4.1.5 the same numbers can be too small for a given prefab, so the
    ///   scope's own tube/housing survives the cut and blocks the view (grey/black interior with
    ///   a stretched magnified rim). Example: scope_30mm_sig_tango6t_1_6x24 breaks while
    ///   scope_34mm_s&amp;b_pm_ii_5_25x56 works, although the two per-scope entries differ only in
    ///   the near/mid radius and the plane-3 position. 37 of the 82 shipped rows use a near
    ///   radius below 8 mm (two of them 1 mm), which cannot cover a 30/34 mm tube body at all.
    ///   Measuring the actual renderer bounds around the cut axis removes the guesswork.
    ///
    /// WHAT IT DOES (per rebuild, never per frame)
    ///   1. Collect candidate renderers: the optic's housing renderers
    ///      (<see cref="LensTransparency.CollectHousingRenderers"/>), falling back to the mesh
    ///      filters the surgery pass already collected when that set is empty. A renderer counts
    ///      only when it is enabled, active in the hierarchy and has a mesh with vertices
    ///      (<see cref="IsUsableRenderer"/>).
    ///   2. For every candidate, take the 8 corners of its world AABB and compute the axial
    ///      coordinate a = dot(corner - origin, dir) and the perpendicular distance
    ///      p = |(corner - origin) - a*dir|. Keep a0 = min(a), a1 = max(a), rmax = max(p).
    ///   3. Sample d in [cutStart, cutEnd] on a grid with a step of at most 10 mm and build the
    ///      envelope R_geo(d) = max{ rmax : a0 - 5 mm &lt;= d &lt;= a1 + 5 mm }. Samples covered by no
    ///      renderer inherit the nearest known value (0 if there is no covered sample at all).
    ///   4. Evaluate the hand-tuned profile R_manual(d) with the very same piecewise-linear
    ///      interpolation the cutter uses (<see cref="MeshPlaneCutter.RadiusAtT4"/>).
    ///   5. Compose R = max(R_geo, R_manual), then R = min(R * margin + extra, maxRadius), and
    ///      place the four control points at 0 / 25% / 55% / 100% of the measured geometry span
    ///      (scopeEnd = max a1, clamped to [min(cutStart + 20 mm, cutEnd), cutEnd]).
    ///
    /// AXIS CONVENTION (important)
    ///   <see cref="MeshPlaneCutter.CutMeshFrustum"/> measures axial distance from the plane point
    ///   along the normal and puts the near cut plane at axial == -startOffset
    ///   (see its IsInsideFrustum: cutStart = -_cStart). t = 0 therefore lives at the SIGNED
    ///   coordinate -StartOffset, not at +StartOffset, so every d used here is expressed in that
    ///   same signed coordinate system (cutStart = -StartOffset, cutEnd = cutStart + CutLength).
    ///   Using +StartOffset would shift the whole measurement window by 2*StartOffset (81.7 mm for
    ///   the 43 shipped rows that use 40.845 mm) and the max() comparison below would compare
    ///   different places - while still producing plausible-looking numbers.
    ///
    /// SAFETY PROPERTIES
    ///   * The composition is a max() against the hand-tuned curve and every control radius is
    ///     additionally floored at the hand-tuned value it has to cover, so auto mode can never
    ///     cut LESS than the tuned profile today ("only ever more aggressive, never weaker").
    ///     The floor is applied per segment (see the region maxima below), not only at the 4
    ///     samples, because the cutter interpolates between control points and holds the last
    ///     radius beyond the last one - a per-sample max would silently drop the far-field radius
    ///     when the measured geometry span is shorter than CutLength (e.g. span 0.35 m vs
    ///     CutLength 2.0 m for both example scopes).
    ///     The floor is a sampled estimate on a &lt;= 10 mm axis grid, not an analytic bound; the
    ///     residual error is bounded by slope * 5 mm (sub-millimetre for the shipped profiles).
    ///   * AABB corners over-estimate the perpendicular distance by up to sqrt(3) versus a true
    ///     oriented radius; the error is always on the "cut more" side, which is the safe side.
    ///   * Every bail-out path (no geometry, no coverage, degenerate axis, non-finite result)
    ///     returns the untouched hand-tuned profile and, with the diagnostics switch on, says so
    ///     in exactly one log line - silence must never be confusable with "the switch is dead".
    ///   * Everything is wrapped in try/catch at the call site; any failure logs one Warning and
    ///     falls back to the untouched hand-tuned profile.
    /// </summary>
    internal static class AutoCutProfileDeriver
    {
        // Envelope slack around a renderer's axial span (metres).
        private const float EnvelopeSlackMeters = 0.005f;

        // Envelope sampling step (metres). A finer step is used when a large CutLength would
        // otherwise need more than MaxSamples samples.
        private const float TargetStepMeters = 0.01f;
        private const int MaxSamples = 4096;

        // Preferred lower bound for the measured geometry span (the spec's [cutStart + 0.02, cutEnd]
        // clamp). If the cut window itself is shorter than this, the window wins - the sampling
        // range must never extend past the real cut end.
        private const float MinimumSpanMeters = 0.02f;

        // Fractions of the measured span used for the plane 2 / 3 / 4 control points.
        private const float MidFraction = 0.25f;
        private const float Plane3Fraction = 0.55f;
        private const float Plane4Fraction = 1.00f;

        // Used when the configured upper clamp is unusable (<= 0 / NaN / infinity).
        private const float DefaultMaxRadiusMeters = 0.25f;

        internal static CutProfileParams Derive(
            OpticSight os,
            Transform scopeRoot,
            Vector3 origin,
            Vector3 axisWorld,
            List<MeshFilter> fallbackTargets,
            CutProfileParams manual,
            string scopeKey,
            string opticName)
        {
            float cutLength = manual.CutLength;

            if (axisWorld.sqrMagnitude < 1e-12f)
            {
                LogBailOut(scopeKey, opticName, "axis-degenerate");
                return manual;
            }

            Vector3 dir = axisWorld.normalized;

            if (!(cutLength > 1e-4f))
            {
                LogBailOut(scopeKey, opticName, $"cut-length-invalid ({cutLength:F4})");
                return manual;
            }

            // Same signed coordinate system as MeshPlaneCutter.IsInsideFrustum (see class comment).
            float cutStart = -manual.StartOffset;
            float cutEnd = cutStart + cutLength;

            float margin = SanitizeMargin(Settings.AutoDeriveMargin.Value);
            float extra = SanitizeExtra(Settings.AutoDeriveExtraRadiusMeters.Value);
            float maxRadius = SanitizeMaxRadius(Settings.AutoDeriveMaxRadiusMeters.Value);

            // ---- 1. candidate renderers ------------------------------------------------
            var candidates = new List<Renderer>(16);
            string source = "housing";

            var housing = LensTransparency.CollectHousingRenderers(os);
            if (housing != null)
            {
                for (int i = 0; i < housing.Count; i++)
                {
                    if (IsUsableRenderer(housing[i]))
                        candidates.Add(housing[i]);
                }
            }

            if (candidates.Count == 0)
            {
                // Fallback: the mesh filters this surgery pass already collected, restricted to
                // the optic subtree. The whole-weapon target list is deliberately NOT used as-is:
                // FindTargetMeshFilters searches from the weapon root, so its AABB would describe
                // the receiver/stock/barrel rather than the optic and the derived radius would
                // saturate the max-radius clamp, boring a 2*maxRadius hole through the gun.
                source = "opticTargets";
                if (fallbackTargets != null && scopeRoot != null)
                {
                    for (int i = 0; i < fallbackTargets.Count; i++)
                    {
                        var mf = fallbackTargets[i];
                        if (mf == null || mf.transform == null) continue;
                        if (mf.transform != scopeRoot && !mf.transform.IsChildOf(scopeRoot)) continue;

                        var r = mf.GetComponent<Renderer>();
                        if (IsUsableRenderer(r))
                            candidates.Add(r);
                    }
                }
            }

            int candidateCount = candidates.Count;
            if (candidateCount == 0)
            {
                // No geometry to measure -> keep today's behaviour bit for bit (a max() against a
                // zero envelope would only apply margin/extra for no reason).
                LogBailOut(scopeKey, opticName, "src=none candidates=0 (housing=0, opticTargets=0)");
                return manual;
            }

            // ---- 2. per-candidate axial span + max perpendicular distance ---------------
            var spanMin = new float[candidateCount];
            var spanMax = new float[candidateCount];
            var spanRadius = new float[candidateCount];
            float scopeEnd = cutStart;

            for (int i = 0; i < candidateCount; i++)
            {
                Bounds b = candidates[i].bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;

                float a0 = float.PositiveInfinity;
                float a1 = float.NegativeInfinity;
                float rmax = 0f;

                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 corner = new Vector3(
                                c.x + e.x * sx,
                                c.y + e.y * sy,
                                c.z + e.z * sz) - origin;
                            float a = Vector3.Dot(corner, dir);
                            float p = (corner - a * dir).magnitude;

                            if (a < a0) a0 = a;
                            if (a > a1) a1 = a;
                            if (p > rmax) rmax = p;
                        }
                    }
                }

                spanMin[i] = a0;
                spanMax[i] = a1;
                spanRadius[i] = rmax;

                if (a1 > scopeEnd)
                    scopeEnd = a1;
            }

            // Lower bound may be clamped down to cutEnd when the cut window itself is shorter than
            // the preferred minimum span - an inverted Clamp range would otherwise return the
            // lower bound and push scopeEnd past the real cut end.
            scopeEnd = Mathf.Clamp(scopeEnd,
                Mathf.Min(cutStart + MinimumSpanMeters, cutEnd), cutEnd);

            // ---- 3. sample the geometric envelope and the hand-tuned curve -------------
            int sampleLast = Mathf.Clamp(
                Mathf.CeilToInt((cutEnd - cutStart) / TargetStepMeters), 1, MaxSamples);
            float step = (cutEnd - cutStart) / sampleLast;   // <= 10 mm unless MaxSamples capped it

            var geo = new float[sampleLast + 1];
            var manualCurve = new float[sampleLast + 1];
            var measured = new bool[sampleLast + 1];
            int coveredSamples = 0;

            for (int k = 0; k <= sampleLast; k++)
            {
                float d = cutStart + k * step;

                float best = 0f;
                bool covered = false;
                for (int i = 0; i < candidateCount; i++)
                {
                    if (spanMin[i] - EnvelopeSlackMeters > d) continue;
                    if (spanMax[i] + EnvelopeSlackMeters < d) continue;
                    if (!covered || spanRadius[i] > best) best = spanRadius[i];
                    covered = true;
                }

                if (covered) coveredSamples++;
                measured[k] = covered;
                geo[k] = covered ? best : float.NaN;

                float t = Mathf.Clamp01((d - cutStart) / cutLength);
                manualCurve[k] = MeshPlaneCutter.RadiusAtT4(t,
                    manual.NearRadius, manual.MidRadius, manual.MidPosition,
                    manual.Plane3Radius, manual.Plane3Position,
                    manual.Plane4Radius, manual.Plane4Position);
            }

            FillUnknownSamples(geo);

            if (coveredSamples == 0)
            {
                // Renderers were found but none of them overlaps the cut window (or the axis
                // convention does not match this prefab). Composing against a zero envelope would
                // only scale the hand-tuned profile by margin+extra for no measured reason, so
                // keep the hand-tuned profile untouched - same "no change" rule as "no geometry".
                LogBailOut(scopeKey, opticName,
                    $"src={source} candidates={candidateCount} covered=0 " +
                    $"(measured axial span [{MinSpan(spanMin, candidateCount):F4},{MaxSpan(spanMax, candidateCount):F4}] " +
                    $"vs cut window [{cutStart:F4},{cutEnd:F4}])");
                return manual;
            }

            // ---- 4. control points, region maxima, composition -------------------------
            float span = scopeEnd - cutStart;
            float dMid = cutStart + MidFraction * span;
            float dThird = cutStart + Plane3Fraction * span;
            float dFar = cutStart + Plane4Fraction * span;

            int kNear = 0;
            int kMid = SampleIndex(dMid, cutStart, step, sampleLast);
            int kThird = SampleIndex(dThird, cutStart, step, sampleLast);
            int kFar = SampleIndex(dFar, cutStart, step, sampleLast);

            var combinedMax = new float[4];
            var manualMax = new float[4];
            RegionMaxima(geo, manualCurve, kNear, kMid, out combinedMax[0], out manualMax[0]);
            RegionMaxima(geo, manualCurve, kMid, kThird, out combinedMax[1], out manualMax[1]);
            RegionMaxima(geo, manualCurve, kThird, kFar, out combinedMax[2], out manualMax[2]);
            RegionMaxima(geo, manualCurve, kFar, sampleLast, out combinedMax[3], out manualMax[3]);

            // A region's two bounding control points must both be >= that region's requirement,
            // because the cutter lerps between them and holds the last radius beyond the last one.
            var regionKnot = new float[4];
            for (int i = 0; i < 4; i++)
                regionKnot[i] = Compose(combinedMax[i], manualMax[i], margin, extra, maxRadius);

            float nearRadius = regionKnot[0];
            float midRadius = Mathf.Max(regionKnot[0], regionKnot[1]);
            float plane3Radius = Mathf.Max(regionKnot[1], regionKnot[2]);
            float plane4Radius = Mathf.Max(regionKnot[2], regionKnot[3]);

            if (!IsFinitePositive(nearRadius) || !IsFinitePositive(midRadius) ||
                !IsFinitePositive(plane3Radius) || !IsFinitePositive(plane4Radius))
            {
                LogBailOut(scopeKey, opticName,
                    $"non-finite radii (nearR={nearRadius} midR={midRadius} p3R={plane3Radius} farR={plane4Radius})");
                return manual;
            }

            var derived = manual;
            derived.NearRadius = nearRadius;
            derived.MidRadius = midRadius;
            derived.MidPosition = Mathf.Clamp01((dMid - cutStart) / cutLength);
            derived.Plane3Radius = plane3Radius;
            derived.Plane3Position = Mathf.Clamp01((dThird - cutStart) / cutLength);
            derived.Plane4Radius = plane4Radius;
            derived.Plane4Position = Mathf.Clamp01((dFar - cutStart) / cutLength);

            // ---- 5. controlled diagnostics --------------------------------------------
            if (Settings.AutoDeriveLogProfile.Value)
                LogProfile(scopeKey, opticName, source, candidateCount, manual, derived,
                    geo, measured, combinedMax, manualMax, regionKnot,
                    step, cutStart, cutEnd, scopeEnd, kMid, kThird, kFar,
                    margin, extra, maxRadius);

            return derived;
        }

        /// <summary>
        /// R = min(max(R_geo, R_manual) * margin + extra, maxRadius), floored at the hand-tuned
        /// value it has to cover so the configured clamp can never make auto weaker than today.
        /// </summary>
        private static float Compose(float combinedMax, float manualMax, float margin, float extra, float maxRadius)
        {
            float value = combinedMax * margin + extra;
            if (value > maxRadius)
                value = maxRadius;
            return value > manualMax ? value : manualMax;
        }

        private static void RegionMaxima(float[] geo, float[] manualCurve, int from, int to,
            out float combinedMax, out float manualMax)
        {
            if (to < from) to = from;

            combinedMax = 0f;
            manualMax = 0f;

            for (int k = from; k <= to; k++)
            {
                float m = manualCurve[k];
                if (m > manualMax) manualMax = m;

                float g = geo[k];
                float c = g > m ? g : m;
                if (c > combinedMax) combinedMax = c;
            }
        }

        /// <summary>Lowest axial coordinate among the measured renderers (diagnostics only).</summary>
        private static float MinSpan(float[] spanMin, int count)
        {
            if (count <= 0) return 0f;
            float min = spanMin[0];
            for (int i = 1; i < count; i++)
            {
                if (spanMin[i] < min) min = spanMin[i];
            }
            return min;
        }

        /// <summary>Highest axial coordinate among the measured renderers (diagnostics only).</summary>
        private static float MaxSpan(float[] spanMax, int count)
        {
            if (count <= 0) return 0f;
            float max = spanMax[0];
            for (int i = 1; i < count; i++)
            {
                if (spanMax[i] > max) max = spanMax[i];
            }
            return max;
        }

        private static int SampleIndex(float d, float cutStart, float step, int sampleLast)
        {
            if (step <= 1e-6f) return 0;
            int index = Mathf.RoundToInt((d - cutStart) / step);
            return Mathf.Clamp(index, 0, sampleLast);
        }

        /// <summary>
        /// Replaces samples no renderer covers with the nearest known value (forward pass, then
        /// backward pass for a leading gap); anything still unknown becomes 0.
        /// </summary>
        private static void FillUnknownSamples(float[] values)
        {
            float last = float.NaN;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsNaN(values[i])) last = values[i];
                else if (!float.IsNaN(last)) values[i] = last;
            }

            last = float.NaN;
            for (int i = values.Length - 1; i >= 0; i--)
            {
                if (!float.IsNaN(values[i])) last = values[i];
                else if (!float.IsNaN(last)) values[i] = last;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i]))
                    values[i] = 0f;
            }
        }

        /// <summary>
        /// A renderer takes part in the measurement only when it can actually be seen and carries
        /// geometry to measure: not null, enabled, active in the hierarchy, and holding a mesh with
        /// at least one vertex. Shared meshes are read, so the same renderer is never counted twice
        /// for a shared mesh.
        /// </summary>
        private static bool IsUsableRenderer(Renderer r)
        {
            if (r == null)
                return false;

            if (!r.enabled)
                return false;

            var go = r.gameObject;
            if (go == null || !go.activeInHierarchy)
                return false;

            Mesh mesh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null)
                mesh = mf.sharedMesh;

            if (mesh == null)
            {
                var smr = r as SkinnedMeshRenderer;
                if (smr != null)
                    mesh = smr.sharedMesh;
            }

            if (mesh != null)
            {
                return mesh.vertexCount > 0;
            }

            return false;
        }

        private static float SanitizeMargin(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                return 1f;
            return value;
        }

        private static float SanitizeExtra(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                return 0f;
            return value;
        }

        private static float SanitizeMaxRadius(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                return DefaultMaxRadiusMeters;
            return value;
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        /// <summary>
        /// One line per bail-out. "No line at all" would look like the diagnostics switch is dead
        /// instead of "nothing was measurable for this optic".
        /// </summary>
        private static void LogBailOut(string scopeKey, string opticName, string detail)
        {
            if (!Settings.AutoDeriveLogProfile.Value)
                return;

            var log = PiPDisablerPlugin.LogSource;
            if (log == null)
                return;

            log.LogInfo(
                $"[MeshSurgery][Auto] scope='{scopeKey}' optic='{opticName}' {detail} " +
                "-> hand-tuned profile kept");
        }

        private static void LogProfile(
            string scopeKey, string opticName, string source, int candidateCount,
            CutProfileParams manual, CutProfileParams derived,
            float[] geo, bool[] measured, float[] combinedMax, float[] manualMax, float[] regionKnot,
            float step, float cutStart, float cutEnd, float scopeEnd,
            int kMid, int kThird, int kFar,
            float margin, float extra, float maxRadius)
        {
            var log = PiPDisablerPlugin.LogSource;
            if (log == null)
                return;

            // geoR@* print the envelope AFTER gap filling, so the measured[] flags are what tells
            // a measured sample apart from an extrapolated one. Every d is in the cutter's own
            // signed coordinate system (t = 0 is at d = -CutStartOffset, NOT at d = 0).
            log.LogInfo(
                $"[MeshSurgery][Auto] scope='{scopeKey}' optic='{opticName}' src={source} candidates={candidateCount} " +
                $"nearR={derived.NearRadius:F4} midPos={derived.MidPosition:F4} midR={derived.MidRadius:F4} " +
                $"p3Pos={derived.Plane3Position:F4} p3R={derived.Plane3Radius:F4} farPos={derived.Plane4Position:F4} farR={derived.Plane4Radius:F4} " +
                $"(geoR@cutStart(d={cutStart:F4})={geo[0]:F4} geoR@mid(d={(cutStart + kMid * step):F4})={geo[kMid]:F4} " +
                $"geoR@p3(d={(cutStart + kThird * step):F4})={geo[kThird]:F4} geoR@far(d={(cutStart + kFar * step):F4})={geo[kFar]:F4} " +
                $"measured=[{Flag(measured[0])},{Flag(measured[kMid])},{Flag(measured[kThird])},{Flag(measured[kFar])}] " +
                $"regionManualMax=[{manualMax[0]:F4},{manualMax[1]:F4},{manualMax[2]:F4},{manualMax[3]:F4}] " +
                $"regionCombinedMax=[{combinedMax[0]:F4},{combinedMax[1]:F4},{combinedMax[2]:F4},{combinedMax[3]:F4}] " +
                $"regionKnot=[{regionKnot[0]:F4},{regionKnot[1]:F4},{regionKnot[2]:F4},{regionKnot[3]:F4}] " +
                $"manualNearR={manual.NearRadius:F4} cutStart={cutStart:F4} cutEnd={cutEnd:F4} scopeEnd={scopeEnd:F4} cutLen={manual.CutLength:F4} " +
                $"startOff={manual.StartOffset:F4} nearPreserve={manual.NearPreserveDepth:F4} step={step:F4} " +
                $"margin={margin:F4} extra={extra:F4} maxRadius={maxRadius:F4})");
        }

        private static string Flag(bool value) => value ? "T" : "F";
    }
}
