using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Peak.NextStep.Core
{
    /// <summary>
    /// Adds the appearance of each occurrence to the STEP output of SolidWorks.
    ///
    /// For a part used twice, SolidWorks writes one shared shape representation
    /// with one styled_item on it. Both occurrences must therefore show the same
    /// colour. See S0 section 2.3. This class adds one
    /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM for each occurrence, tied to
    /// the NEXT_ASSEMBLY_USAGE_OCCURRENCE of that occurrence. ISO 10303-46
    /// defines this entity for this purpose, and SolidWorks never writes it.
    ///
    /// This class does not change geometry.
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
        /// The appearance group that each product in the file carries. The
        /// numbers start again for each part. Group 0 keeps the shared geometry.
        /// Groups 1 and above are the copies.
        ///
        /// This map lets MaterialWriter give each group its own material name.
        /// STEP has no link between a material and an appearance. See
        /// MaterialWriter. But a consumer that builds one material for each
        /// material NAME builds one for each group when the names differ. Each
        /// material then takes the colour of the first product that uses it.
        /// </summary>
        public Dictionary<int, int> BucketIndexByProduct { get; } = new Dictionary<int, int>();

        public sealed class OccurrenceRef
        {
            public int NauoId;
            /// <summary>The position of this occurrence, in file units.</summary>
            public double[] Translation;
            /// <summary>The geometric item that the styling of this occurrence
            /// must point at.</summary>
            public int TargetItemId;
            public int BaseStyledItemId;
        }

        /// <summary>
        /// Finds every occurrence and its position. For the identity, this walks
        /// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION to product_definition_shape to
        /// NAUO. For the position, it walks item_defined_transformation to
        /// axis2_placement_3d to cartesian_point.
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

                // Point at the solid that the assembly shares. If no styled
                // item names a solid, use the shape representation.
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
        /// Reads the position of this occurrence in the assembly.
        ///
        /// This code follows an exact chain. A breadth-first search does not
        /// work here:
        ///     CDSR -> (representation_relationship_with_transformation)
        ///          -> ITEM_DEFINED_TRANSFORMATION(name, desc, item_1, item_2)
        /// Here item_1 is the position in ASSEMBLY space, which identifies the
        /// occurrence. item_2 is the origin of the part itself, and every
        /// occurrence SHARES it. A breadth-first search finds the point of
        /// item_2 just as easily, and then reports every occurrence at the
        /// origin.
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

        /// <summary>Breadth-first search for the nearest entity of a type.</summary>
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
        /// Writes the colour of one occurrence. Returns the id of the new styled
        /// item.
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
        /// Applies the resolved appearance to every matched occurrence.
        ///
        /// deInstance=false keeps the instancing of SolidWorks and writes
        /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM. That output is compact and
        /// correct under ISO 10303-46. But Fusion 360 and STEPper NEXT ignore it.
        /// This was measured. See FINDINGS.md 4.3.
        ///
        /// deInstance=true separates the occurrences by APPEARANCE, not one by
        /// one. For each part, this code groups the occurrences by the colour
        /// they resolved to. Each group gets one copy with a plain STYLED_ITEM,
        /// which every consumer reads. Two occurrences of a part with the same
        /// colour therefore stay true instances of one product. Only occurrences
        /// that look different cost extra geometry.
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
                // An occurrence with no override must keep the part exactly as
                // SolidWorks wrote it, including the colour of each face. If any
                // such occurrence exists, the shared geometry belongs to it, and
                // EVERY overridden group must be a copy. To recolour the shared
                // geometry would then repaint occurrences that have no
                // override.
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
                        // The shared geometry is group 0, recorded above. It
                        // still uses an index. The next group is a copy and must
                        // have the number 1, not 0. With the number 0 its
                        // material name matches this one, and a reader merges
                        // the two groups again.
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
        /// The key that makes two occurrences equal. The numbers are rounded.
        /// Colours from different SolidWorks scopes can differ in the last bit
        /// of a double, and they must still fall in the same group.
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

        /// <summary>Walks PRODUCT_DEFINITION to formation to PRODUCT.</summary>
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
