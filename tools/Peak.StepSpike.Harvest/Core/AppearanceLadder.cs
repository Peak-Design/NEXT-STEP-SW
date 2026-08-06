using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.NextStep.Core
{
    public struct Rgb
    {
        public double R, G, B;
        public Rgb(double r, double g, double b) { R = r; G = g; B = b; }

        /// <summary>SolidWorks packs appearance colour as COLORREF 0x00BBGGRR.</summary>
        public static Rgb FromColorRef(int c)
            => new Rgb((c & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0);

        public bool ApproxEquals(Rgb o, double tol = 1e-4)
            => Math.Abs(R - o.R) < tol && Math.Abs(G - o.G) < tol && Math.Abs(B - o.B) < tol;

        public override string ToString() => $"({R:F3},{G:F3},{B:F3})";
    }

    /// <summary>The appearance that applies to one component occurrence.</summary>
    public sealed class OccurrenceAppearance
    {
        /// <summary>Component path, e.g. "sub-1/part-2" -- unique per occurrence.</summary>
        public string Path;
        public string ComponentName;
        public string ReferencedConfiguration;
        public Rgb Colour;
        public double Transparency;
        /// <summary>Which scope supplied the winning colour, for diagnostics.</summary>
        public string WinningScope;
        /// <summary>True when the winner came from above the part, so it must
        /// override the part's own face/body/feature styling in the STEP.</summary>
        public bool OverridesPartInternals;
    }

    /// <summary>
    /// Resolves the appearance SolidWorks actually displays for each component
    /// occurrence.
    ///
    /// The rule (stated by the maintainer, corroborated by
    /// corpus/C4_component_override_2.JPG where a top-level green override beats
    /// two component-level overrides, and by C2_stacked_overrides.jpg):
    ///
    ///   ACROSS documents, the HIGHEST level wins:
    ///     top assembly > sub-assembly override > sub-assembly >
    ///     part override (component level) > part
    ///   WITHIN one part, the MOST SPECIFIC wins:
    ///     face > feature > body > part
    ///
    /// Note this is NOT what IComponent2.MaterialPropertyValues returns: that
    /// call ignores overrides applied above the component, and reports orange
    /// and yellow for C4_component_override_2 where SolidWorks displays green.
    /// The ladder therefore has to be walked explicitly.
    /// </summary>
    public static class AppearanceLadder
    {
        /// <summary>
        /// An appearance harvested from the model, tagged with the scope it was
        /// applied at and the entities it covers.
        /// </summary>
        private sealed class ScopedAppearance
        {
            public Rgb Colour;
            public double Transparency;
            public string Scope;          // assembly | component | part | body | feature | face
            public HashSet<string> ComponentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool CoversWholeDocument;
        }

        public static List<OccurrenceAppearance> Resolve(IModelDoc2 model, Action<string> log)
        {
            var results = new List<OccurrenceAppearance>();
            var ext = model.Extension;

            // Every appearance in the document, tagged by scope.
            var scoped = HarvestScoped(ext, log);

            // The document-level (top assembly) override, if any, beats everything.
            var topOverride = scoped.FirstOrDefault(s => s.Scope == "assembly");

            if (!(model is IAssemblyDoc assy))
            {
                // A part on its own: no occurrences to resolve. The part's own
                // face/feature/body/part styling is already correct in
                // SolidWorks' STEP output (S0 section 2.2), so nothing to do.
                return results;
            }

            var comps = assy.GetComponents(false) as object[] ?? new object[0];
            foreach (var o in comps)
            {
                var comp = o as IComponent2;
                if (comp == null) continue;

                var occ = new OccurrenceAppearance
                {
                    Path = comp.Name2,
                    ComponentName = comp.Name2,
                    ReferencedConfiguration = comp.ReferencedConfiguration,
                };

                if (topOverride != null)
                {
                    // Highest level wins, for every occurrence beneath it.
                    occ.Colour = topOverride.Colour;
                    occ.Transparency = topOverride.Transparency;
                    occ.WinningScope = "assembly";
                    occ.OverridesPartInternals = true;
                }
                else
                {
                    var compLevel = scoped.FirstOrDefault(
                        s => s.Scope == "component" && s.ComponentPaths.Contains(comp.Name2));
                    if (compLevel != null)
                    {
                        occ.Colour = compLevel.Colour;
                        occ.Transparency = compLevel.Transparency;
                        occ.WinningScope = "component";
                        occ.OverridesPartInternals = true;
                    }
                    else
                    {
                        // Nothing above the part applies: SolidWorks' own export
                        // already carries the part's internal styling correctly,
                        // so record it but mark it as needing no intervention.
                        var vals = comp.MaterialPropertyValues as double[];
                        if (vals != null && vals.Length >= 3 && vals[0] >= 0)
                        {
                            occ.Colour = new Rgb(vals[0], vals[1], vals[2]);
                            occ.Transparency = vals.Length > 7 ? vals[7] : 0.0;
                        }
                        occ.WinningScope = "part";
                        occ.OverridesPartInternals = false;
                    }
                }

                results.Add(occ);
                log?.Invoke($"    {occ.Path,-24} {occ.WinningScope,-10} {occ.Colour} " +
                            $"overridesPart={occ.OverridesPartInternals}");
            }

            return results;
        }

        private static List<ScopedAppearance> HarvestScoped(IModelDocExtension ext, Action<string> log)
        {
            var outList = new List<ScopedAppearance>();
            var raw = ext.GetRenderMaterials2(
                (int)swDisplayStateOpts_e.swAllDisplayState, null) as object[];
            if (raw == null) return outList;

            foreach (var r in raw)
            {
                var rm = r as IRenderMaterial;
                if (rm == null) continue;

                var sa = new ScopedAppearance
                {
                    Colour = Rgb.FromColorRef(rm.PrimaryColor),
                    Transparency = SafeDouble(() => rm.Transparency),
                    Scope = "unknown",
                };

                var ents = SafeArray(() => rm.GetEntities() as object[]);
                foreach (var e in ents)
                {
                    if (e is IComponent2 c)
                    {
                        sa.Scope = "component";
                        sa.ComponentPaths.Add(c.Name2);
                    }
                    else if (e is IFace2) sa.Scope = Weakest(sa.Scope, "face");
                    else if (e is IFeature) sa.Scope = Weakest(sa.Scope, "feature");
                    else if (e is IBody2) sa.Scope = Weakest(sa.Scope, "body");
                    else if (e is IPartDoc) sa.Scope = Weakest(sa.Scope, "part");
                    else if (e is IModelDoc2)
                    {
                        // An appearance whose entity is the document itself is the
                        // assembly-level override -- the one that beats everything.
                        sa.Scope = "assembly";
                        sa.CoversWholeDocument = true;
                    }
                }

                outList.Add(sa);
                log?.Invoke($"    appearance {sa.Colour} scope={sa.Scope} " +
                            $"paths=[{string.Join(",", sa.ComponentPaths)}]");
            }
            return outList;
        }

        // "component" and "assembly" are decided by entity identity, never
        // downgraded by a later entity in the same appearance.
        private static string Weakest(string current, string candidate)
            => (current == "component" || current == "assembly") ? current : candidate;

        private static double SafeDouble(Func<double> f)
        { try { return f(); } catch { return 0.0; } }

        private static object[] SafeArray(Func<object[]> f)
        { try { return f() ?? new object[0]; } catch { return new object[0]; } }
    }
}
