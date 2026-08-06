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

        /// <summary>SolidWorks stores an appearance colour as a COLORREF, in
        /// the form 0x00BBGGRR.</summary>
        public static Rgb FromColorRef(int c)
            => new Rgb((c & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0);

        public bool ApproxEquals(Rgb o, double tol = 1e-4)
            => Math.Abs(R - o.R) < tol && Math.Abs(G - o.G) < tol && Math.Abs(B - o.B) < tol;

        public override string ToString() => $"({R:F3},{G:F3},{B:F3})";
    }

    /// <summary>The appearance that applies to one component occurrence.</summary>
    public sealed class OccurrenceAppearance
    {
        /// <summary>The component path, such as "sub-1/part-2". Each occurrence
        /// has its own path.</summary>
        public string Path;
        public string ComponentName;
        public string ReferencedConfiguration;
        public Rgb Colour;
        public double Transparency;
        /// <summary>The scope that gave the winning colour. This is for
        /// diagnosis.</summary>
        public string WinningScope;
        /// <summary>True when the winning colour comes from above the part. The
        /// colour must then override the face, body and feature styling of the
        /// part in the STEP file.</summary>
        public bool OverridesPartInternals;
        /// <summary>
        /// False when SolidWorks does not write this occurrence at all, because
        /// it is hidden or suppressed. This was measured, not assumed. A silent
        /// SaveAs3 leaves out both kinds. See evidence/S8/hidden.log. Such a
        /// component has no occurrence to match, so a report of no match would
        /// be a false alarm.
        /// </summary>
        public bool Exported = true;
        /// <summary>Holds "hidden" or "suppressed" when Exported is
        /// false.</summary>
        public string ExcludedBecause;
    }

    /// <summary>
    /// Resolves the appearance that SolidWorks displays for each component
    /// occurrence.
    ///
    /// The maintainer stated the rule. Two corpus images confirm it.
    /// corpus/C4_component_override_2.JPG shows a green override at the top
    /// level that beats two overrides at component level.
    /// C2_stacked_overrides.jpg shows the second half of the rule.
    ///
    ///   ACROSS documents, the HIGHEST level wins:
    ///     top assembly > sub-assembly override > sub-assembly >
    ///     part override (component level) > part
    ///   WITHIN one part, the MOST EXACT scope wins:
    ///     face > feature > body > part
    ///
    /// IComponent2.MaterialPropertyValues does NOT return this result. That call
    /// ignores an override above the component. For C4_component_override_2 it
    /// reports orange and yellow, and SolidWorks displays green. This code must
    /// therefore walk the hierarchy itself.
    /// </summary>
    public static class AppearanceLadder
    {
        /// <summary>
        /// One appearance read from the model, with the scope that it was
        /// applied at and the entities that it covers.
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
                // A part on its own has no occurrences to resolve. The STEP
                // output of SolidWorks already holds the face, feature, body and
                // part styling correctly. See S0 section 2.2.
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

                occ.ExcludedBecause = ExclusionReason(comp);
                occ.Exported = occ.ExcludedBecause == null;

                if (topOverride != null)
                {
                    // The highest level wins, for every occurrence below it.
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
                        // Nothing above the part applies. The SolidWorks export
                        // already carries the internal styling of the part
                        // correctly. Record the colour, but mark it as needing
                        // no repair.
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
                            $"overridesPart={occ.OverridesPartInternals}" +
                            (occ.Exported ? "" : $" EXCLUDED ({occ.ExcludedBecause})"));
            }

            return results;
        }

        /// <summary>
        /// The reason that SolidWorks leaves this component out of the STEP
        /// file. Returns null when SolidWorks writes the component.
        ///
        /// Both cases were measured, not assumed. A silent SaveAs3 drops the
        /// hidden components as well as the suppressed ones. This holds for
        /// every corpus assembly, and with swStepExportAppearances both on and
        /// off. See evidence/S8/hidden.log. No preference controls this.
        /// SolidWorks asks the user, and swSaveAsOptions_Silent answers the
        /// question.
        /// </summary>
        public static string ExclusionReason(IComponent2 comp)
        {
            try
            {
                int suppression = comp.GetSuppression2();
                if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed)
                    return "suppressed";
            }
            catch { }

            try
            {
                if (comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden)
                    return "hidden";
            }
            catch { }

            return null;
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
                        // An appearance whose entity is the document itself is
                        // the override at assembly level. It beats everything.
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

        // The entity itself decides the scopes "component" and "assembly". A
        // later entity in the same appearance never lowers them.
        private static string Weakest(string current, string candidate)
            => (current == "component" || current == "assembly") ? current : candidate;

        private static double SafeDouble(Func<double> f)
        { try { return f(); } catch { return 0.0; } }

        private static object[] SafeArray(Func<object[]> f)
        { try { return f() ?? new object[0]; } catch { return new object[0]; } }
    }
}
