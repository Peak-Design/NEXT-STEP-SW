using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Peak.NextStep.Core
{
    /// <summary>
    /// Attaches per-occurrence appearance to SolidWorks' own STEP output.
    ///
    /// SolidWorks writes one shared shape representation for a part used twice,
    /// with a single styled_item on it, so both occurrences necessarily render
    /// the same colour (S0 section 2.3). This adds a
    /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM per occurrence, bound to that
    /// occurrence's NEXT_ASSEMBLY_USAGE_OCCURRENCE -- the entity ISO 10303-46
    /// defines for exactly this, which SolidWorks never emits.
    ///
    /// Geometry is never touched.
    /// </summary>
    public sealed class StepRewriter
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;

        public StepRewriter(string stepPath, Action<string> log)
        {
            _step = new Part21(stepPath);
            _log = log;
        }

        /// <summary>
        /// Which appearance bucket each product in the file ended up carrying,
        /// numbered per part: 0 is the bucket that kept the shared geometry,
        /// 1 and up are the copies.
        ///
        /// This exists so engineering material can be named per bucket. STEP
        /// itself has no link between material and appearance -- see
        /// MaterialWriter -- but a consumer that creates one material per
        /// material NAME will create one per bucket if the names differ, and
        /// each takes the colour of the product it first met.
        /// </summary>
        public Dictionary<int, int> BucketIndexByProduct { get; } = new Dictionary<int, int>();

        public sealed class OccurrenceRef
        {
            public int NauoId;
            /// <summary>Translation of this occurrence, in file units.</summary>
            public double[] Translation;
            /// <summary>The geometric item this occurrence's styling should target.</summary>
            public int TargetItemId;
            public int BaseStyledItemId;
        }

        /// <summary>
        /// Find every occurrence and its placement, by walking
        /// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION -> product_definition_shape ->
        /// NAUO for identity, and -> item_defined_transformation ->
        /// axis2_placement_3d -> cartesian_point for placement.
        /// </summary>
        public List<OccurrenceRef> FindOccurrences()
        {
            var result = new List<OccurrenceRef>();
            var styledByItem = new Dictionary<int, int>();
            foreach (var sid in _step.ByType("STYLED_ITEM"))
            {
                foreach (var r in _step.Refs(sid))
                {
                    var t = _step.TypeOf(r);
                    if (t == "MANIFOLD_SOLID_BREP" || t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                        || t == "SHELL_BASED_SURFACE_MODEL" || t == "BREP_WITH_VOIDS")
                        styledByItem[r] = sid;
                }
            }

            foreach (var cdsr in _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                int nauo = FindReachable(cdsr, "NEXT_ASSEMBLY_USAGE_OCCURRENCE", 4);
                if (nauo < 0) continue;

                double[] xyz = ReadOccurrencePlacement(cdsr);

                // Target the solid the assembly shares; fall back to the shape
                // representation when there is no explicit solid styled item.
                int target = styledByItem.Keys.FirstOrDefault(
                    k => _step.TypeOf(k) == "MANIFOLD_SOLID_BREP");
                if (target == 0)
                    target = styledByItem.Keys.FirstOrDefault();

                result.Add(new OccurrenceRef
                {
                    NauoId = nauo,
                    Translation = xyz,
                    TargetItemId = target,
                    BaseStyledItemId = styledByItem.TryGetValue(target, out var b) ? b : -1,
                });
            }

            result.Sort((a, b) => a.NauoId.CompareTo(b.NauoId));
            return result;
        }

        /// <summary>
        /// Read where this occurrence sits in the assembly.
        ///
        /// The chain is explicit and must not be searched breadth-first:
        ///     CDSR -> (representation_relationship_with_transformation)
        ///          -> ITEM_DEFINED_TRANSFORMATION(name, desc, item_1, item_2)
        /// where item_1 is the placement in ASSEMBLY space (what identifies the
        /// occurrence) and item_2 is the part's own origin placement, which is
        /// SHARED by every occurrence. A breadth-first search finds item_2's
        /// point just as readily and reports every occurrence at the origin.
        /// </summary>
        private double[] ReadOccurrencePlacement(int cdsr)
        {
            foreach (var rel in _step.Refs(cdsr))
            {
                var relType = _step.TypeOf(rel) ?? "";
                if (relType.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) < 0)
                    continue;

                foreach (var idt in _step.Refs(rel))
                {
                    if (!string.Equals(_step.TypeOf(idt), "ITEM_DEFINED_TRANSFORMATION",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var items = _step.Refs(idt);           // [transform_item_1, transform_item_2]
                    if (items.Count < 1) continue;

                    var placement = items[0];
                    if (!string.Equals(_step.TypeOf(placement), "AXIS2_PLACEMENT_3D",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var placementRefs = _step.Refs(placement);   // [location, axis, refDirection]
                    if (placementRefs.Count < 1) continue;

                    int location = placementRefs[0];
                    if (string.Equals(_step.TypeOf(location), "CARTESIAN_POINT",
                                      StringComparison.OrdinalIgnoreCase))
                        return ReadTriple(location);
                }
            }
            return null;
        }

        /// <summary>Breadth-first search for the nearest entity of a given type.</summary>
        private int FindReachable(int start, string type, int maxDepth)
        {
            var seen = new HashSet<int> { start };
            var frontier = new List<int> { start };
            for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<int>();
                foreach (var id in frontier)
                {
                    foreach (var r in _step.Refs(id))
                    {
                        if (!seen.Add(r)) continue;
                        if (string.Equals(_step.TypeOf(r), type, StringComparison.OrdinalIgnoreCase))
                            return r;
                        next.Add(r);
                    }
                }
                frontier = next;
            }
            return -1;
        }

        private double[] ReadTriple(int cartesianPointId)
        {
            var args = _step.ArgsOf(cartesianPointId) ?? "";
            var nums = System.Text.RegularExpressions.Regex
                .Matches(args, @"-?\d+\.?\d*(?:[eE][-+]?\d+)?")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();
            return nums.Length >= 3 ? new[] { nums[0], nums[1], nums[2] } : null;
        }

        /// <summary>
        /// Write one occurrence's colour. Returns the id of the new styled item.
        /// </summary>
        public int AddOccurrenceColour(OccurrenceRef occ, Rgb colour, double transparency)
        {
            int colourId = _step.NextId();
            _step.Append($"#{colourId}=COLOUR_RGB(''," +
                         $"{Part21.Num(colour.R)},{Part21.Num(colour.G)},{Part21.Num(colour.B)});");

            int fillId = _step.NextId();
            _step.Append($"#{fillId}=FILL_AREA_STYLE_COLOUR('',#{colourId});");
            int fasId = _step.NextId();
            _step.Append($"#{fasId}=FILL_AREA_STYLE('',(#{fillId}));");
            int ssfaId = _step.NextId();
            _step.Append($"#{ssfaId}=SURFACE_STYLE_FILL_AREA(#{fasId});");

            var sideElements = new List<string> { $"#{ssfaId}" };
            if (transparency > 1e-6)
            {
                int transpId = _step.NextId();
                _step.Append($"#{transpId}=SURFACE_STYLE_TRANSPARENT({Part21.Num(transparency)});");
                int rendId = _step.NextId();
                _step.Append($"#{rendId}=SURFACE_STYLE_RENDERING_WITH_PROPERTIES(" +
                             $".NORMAL_SHADING.,#{colourId},(#{transpId}));");
                sideElements.Add($"#{rendId}");
            }

            int sssId = _step.NextId();
            _step.Append($"#{sssId}=SURFACE_SIDE_STYLE('',({string.Join(",", sideElements)}));");
            int ssuId = _step.NextId();
            _step.Append($"#{ssuId}=SURFACE_STYLE_USAGE(.BOTH.,#{sssId});");
            int psaId = _step.NextId();
            _step.Append($"#{psaId}=PRESENTATION_STYLE_ASSIGNMENT((#{ssuId}));");

            int styledId = _step.NextId();
            if (occ.BaseStyledItemId > 0)
            {
                _step.Append($"#{styledId}=CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM(" +
                             $"'occurrence colour',(#{psaId}),#{occ.TargetItemId}," +
                             $"#{occ.BaseStyledItemId},(#{occ.NauoId}));");
            }
            else
            {
                // No base styled item to override: a plain styled item bound to
                // the item still carries the colour for readers that ignore
                // occurrence context.
                _step.Append($"#{styledId}=STYLED_ITEM('occurrence colour'," +
                             $"(#{psaId}),#{occ.TargetItemId});");
            }
            return styledId;
        }

        /// <summary>
        /// Apply the resolved appearance to every matched occurrence.
        ///
        /// deInstance=false keeps SolidWorks' instancing and writes
        /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM -- compact and correct per
        /// ISO 10303-46, but measured to be ignored by Fusion 360 and STEPper
        /// NEXT (FINDINGS.md 4.3).
        ///
        /// deInstance=true separates occurrences by APPEARANCE, not by
        /// occurrence: for each part, occurrences are bucketed by the colour
        /// they resolved to and each bucket gets one copy carrying a plain
        /// STYLED_ITEM, which every consumer reads. Two occurrences of a part
        /// that ended up the same colour therefore stay real instances of one
        /// product -- only genuinely different-looking occurrences cost extra
        /// geometry.
        /// </summary>
        public int ApplyOccurrenceColours(
            List<KeyValuePair<OccurrenceAppearance, OccurrenceRef>> pairs, bool deInstance)
        {
            int applied = 0;
            var matched = pairs.Where(p => p.Value != null).ToList();
            if (!matched.Any(p => p.Key.OverridesPartInternals)) return 0;

            if (!deInstance)
            {
                foreach (var p in matched.Where(p => p.Key.OverridesPartInternals))
                {
                    AddOccurrenceColour(p.Value, p.Key.Colour, p.Key.Transparency);
                    applied++;
                }
                return applied;
            }

            var di = new DeInstancer(_step, _log);

            foreach (var partGroup in matched.GroupBy(p => PartProductOf(p.Value.NauoId)))
            {
                // Occurrences carrying no override must keep the part exactly
                // as SolidWorks wrote it -- including per-face colours. If any
                // exist, the shared geometry belongs to them and EVERY
                // overridden bucket has to be a copy. Recolouring the shared
                // geometry in that case would repaint occurrences that were
                // never overridden.
                bool sharedGeometryTaken = partGroup.Any(p => !p.Key.OverridesPartInternals);

                var buckets = partGroup
                    .Where(p => p.Key.OverridesPartInternals)
                    .GroupBy(p => AppearanceKey(p.Key))
                    .ToList();

                int originalProduct = ProductOf(partGroup.Key);
                int bucketIndex = sharedGeometryTaken ? 1 : 0;
                if (originalProduct > 0 && !BucketIndexByProduct.ContainsKey(originalProduct))
                    BucketIndexByProduct[originalProduct] = 0;

                foreach (var bucket in buckets)
                {
                    var lead = bucket.First();
                    int n = bucket.Count();
                    string who = string.Join(", ", bucket.Select(b => b.Key.Path));

                    if (!sharedGeometryTaken)
                    {
                        sharedGeometryTaken = true;
                        int repointed = di.RecolourPart(lead.Value, lead.Key.Colour, lead.Key.Transparency);
                        if (repointed == 0 && lead.Value.TargetItemId > 0)
                            di.AddPlainStyle(lead.Value.TargetItemId, lead.Key.Colour, lead.Key.Transparency);
                        // The shared geometry is bucket 0, registered above.
                        // It still consumes an index: the next bucket is a copy
                        // and must be numbered 1, not 0, or its material name
                        // collides with this one and the two merge again.
                        bucketIndex++;

                        _log?.Invoke($"    {who}: {n} occurrence(s) keep the shared geometry, " +
                                     $"recoloured {repointed} styled item(s) to {lead.Key.Colour}");
                        applied += n;
                        continue;
                    }

                    var copy = di.ClonePart(lead.Value);
                    if (copy == null || copy.SolidId < 0)
                    {
                        _log?.Invoke($"    {who}: de-instancing FAILED, falling back to " +
                                     "occurrence styling");
                        foreach (var p in bucket)
                        {
                            AddOccurrenceColour(p.Value, p.Key.Colour, p.Key.Transparency);
                            applied++;
                        }
                        continue;
                    }

                    di.AddPlainStyle(copy.SolidId, lead.Key.Colour, lead.Key.Transparency);
                    foreach (var p in bucket) di.PointOccurrenceAt(p.Value, copy);

                    if (originalProduct > 0 && copy.Map.TryGetValue(originalProduct, out int clonedProduct))
                        BucketIndexByProduct[clonedProduct] = bucketIndex;
                    bucketIndex++;

                    _log?.Invoke($"    {who}: {n} occurrence(s) share one copy " +
                                 $"({copy.EntityCount} entities), solid #{copy.SolidId} = {lead.Key.Colour}");
                    applied += n;
                }
            }
            return applied;
        }

        /// <summary>
        /// What makes two occurrences interchangeable. Quantised, because
        /// colours that arrive from different SolidWorks scopes can differ in
        /// the last bit of a double and must still bucket together.
        /// </summary>
        private static string AppearanceKey(OccurrenceAppearance a)
            => string.Format(CultureInfo.InvariantCulture, "{0:F3}/{1:F3}/{2:F3}/{3:F3}",
                             a.Colour.R, a.Colour.G, a.Colour.B, a.Transparency);

        private int PartProductOf(int nauoId)
        {
            var refs = _step.Refs(nauoId);
            for (int i = refs.Count - 1; i >= 0; i--)
                if (_step.TypeOf(refs[i]) == "PRODUCT_DEFINITION") return refs[i];
            return -1;
        }

        /// <summary>PRODUCT_DEFINITION -&gt; formation -&gt; PRODUCT.</summary>
        private int ProductOf(int productDefinition)
        {
            if (productDefinition <= 0) return -1;
            foreach (var f in _step.Refs(productDefinition))
            {
                var t = _step.TypeOf(f) ?? "";
                if (!t.StartsWith("PRODUCT_DEFINITION_FORMATION", StringComparison.Ordinal)) continue;
                foreach (var p in _step.Refs(f))
                    if (_step.TypeOf(p) == "PRODUCT") return p;
            }
            return -1;
        }

        public void Save(string path)
        {
            _step.Save(path);
            _log?.Invoke($"    wrote {path}");
        }

        public Part21 Document => _step;
    }
}
