using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Peak.NextStep.Core
{
    /// <summary>
    /// De-instance mode: give every overridden occurrence its own product,
    /// geometry and plain STYLED_ITEM.
    ///
    /// Why this exists. The instanced encoding
    /// (CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM) is correct per ISO 10303-46
    /// but measured to be ignored by both Fusion 360 and STEPper NEXT, which
    /// fall back to the referred part's colour. De-instancing reduces the
    /// problem to plain per-solid colour, which every consumer reads, at the
    /// cost of duplicated geometry.
    ///
    /// Copies are made per DISTINCT APPEARANCE, not per occurrence. Two
    /// occurrences of one part that resolve to the same colour stay genuine
    /// instances of a single product -- they only need separating when they
    /// actually look different. Splitting them regardless would duplicate
    /// geometry for no visible gain and cost the consumer its instancing.
    /// </summary>
    public sealed class DeInstancer
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;

        public DeInstancer(Part21 step, Action<string> log)
        {
            _step = step;
            _log = log;
        }

        /// <summary>One cloned copy of a part, ready to be coloured and shared.</summary>
        public sealed class PartCopy
        {
            /// <summary>original id -> cloned id, for rewiring occurrences.</summary>
            public Dictionary<int, int> Map;
            /// <summary>The cloned solid that carries the copy's colour, or -1.</summary>
            public int SolidId;
            public int EntityCount;
        }

        /// <summary>
        /// Contexts and units are shared by the whole file. Cloning them would
        /// duplicate the unit system and can make readers reject the file.
        /// </summary>
        private static bool IsShared(string type)
        {
            if (type == null) return true;
            if (type.IndexOf("REPRESENTATION_CONTEXT", StringComparison.Ordinal) >= 0) return true;
            foreach (var p in new[]
            {
                "SI_UNIT", "CONVERSION_BASED_UNIT", "NAMED_UNIT", "DIMENSIONAL_EXPONENTS",
                "UNCERTAINTY_MEASURE_WITH_UNIT", "LENGTH_MEASURE_WITH_UNIT",
                "PLANE_ANGLE_MEASURE_WITH_UNIT", "SOLID_ANGLE_MEASURE_WITH_UNIT",
                "APPLICATION_CONTEXT", "APPLICATION_PROTOCOL_DEFINITION",
                "PRODUCT_CONTEXT", "PRODUCT_DEFINITION_CONTEXT", "MECHANICAL_CONTEXT",
                "DESIGN_CONTEXT", "PRODUCT_RELATED_PRODUCT_CATEGORY",
            })
                if (type.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Everything reachable from the roots that is not shared.</summary>
        private HashSet<int> Closure(IEnumerable<int> roots)
        {
            var set = new HashSet<int>();
            var stack = new Stack<int>(roots);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!_step.Entities.ContainsKey(id)) continue;
                if (IsShared(_step.TypeOf(id))) continue;
                if (!set.Add(id)) continue;
                foreach (var r in _step.Refs(id)) stack.Push(r);
            }
            return set;
        }

        /// <summary>Clone a set of entities, remapping internal references.</summary>
        private Dictionary<int, int> CloneAll(HashSet<int> ids)
        {
            var map = new Dictionary<int, int>();
            foreach (var id in ids) map[id] = _step.NextId();

            foreach (var id in ids)
            {
                string type = _step.TypeOf(id);
                string args = _step.ArgsOf(id);
                string remapped = Remap(args, map);
                // Complex instances already carry their own type list in args.
                string line = type.StartsWith("COMPLEX:", StringComparison.Ordinal)
                    ? $"#{map[id]}={remapped};"
                    : $"#{map[id]}={type}{remapped};";
                _step.Append(line);
            }
            return map;
        }

        private static string Remap(string args, Dictionary<int, int> map)
            => Regex.Replace(args, @"#(\d+)", m =>
            {
                int id = int.Parse(m.Groups[1].Value);
                return map.TryGetValue(id, out var n) ? "#" + n : m.Value;
            });

        /// <summary>Rewrite one existing entity with references remapped.</summary>
        private void RewireEntity(int id, Dictionary<int, int> map)
        {
            string type = _step.TypeOf(id);
            string args = Remap(_step.ArgsOf(id), map);
            string line = type.StartsWith("COMPLEX:", StringComparison.Ordinal)
                ? $"#{id}={args};"
                : $"#{id}={type}{args};";
            _step.Replace(id, line);
        }

        /// <summary>
        /// Make one fresh copy of the part this occurrence references.
        ///
        /// Cloning and rewiring are deliberately separate calls: one copy is
        /// shared by every occurrence that resolved to the same appearance, so
        /// the copy is made once and pointed at N times.
        /// </summary>
        public PartCopy ClonePart(StepRewriter.OccurrenceRef occ)
        {
            // The NAUO's second product_definition reference is the part.
            var nauoRefs = _step.Refs(occ.NauoId);
            int partPd = nauoRefs.LastOrDefault(r => _step.TypeOf(r) == "PRODUCT_DEFINITION");
            if (partPd == 0) { _log?.Invoke($"    NAUO #{occ.NauoId}: no part product_definition"); return null; }

            // product_definition_shape -> shape_definition_representation -> shape_representation
            int pds = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                           .FirstOrDefault(p => _step.Refs(p).Contains(partPd));
            int sdr = pds == 0 ? 0 : _step.ByType("SHAPE_DEFINITION_REPRESENTATION")
                           .FirstOrDefault(s => _step.Refs(s).Contains(pds));
            int partSr = sdr == 0 ? 0 : _step.Refs(sdr)
                           .FirstOrDefault(r => _step.TypeOf(r) == "SHAPE_REPRESENTATION");
            if (partSr == 0) { _log?.Invoke($"    NAUO #{occ.NauoId}: no part shape_representation"); return null; }

            // shape_representation_relationship links the part's SR to its B-rep.
            int srr = _step.ByType("SHAPE_REPRESENTATION_RELATIONSHIP")
                           .FirstOrDefault(s => _step.Refs(s).Contains(partSr));
            int absr = srr == 0 ? 0 : _step.Refs(srr).FirstOrDefault(
                           r => _step.TypeOf(r) == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                             || _step.TypeOf(r) == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION");

            // The product chain: product_definition -> formation -> product.
            var chain = new List<int> { partPd, pds, sdr, partSr };
            if (srr != 0) chain.Add(srr);
            if (absr != 0) chain.Add(absr);

            var roots = new List<int> { partSr };
            if (absr != 0) roots.Add(absr);
            var toClone = Closure(roots);
            foreach (var c in chain) if (!IsShared(_step.TypeOf(c))) toClone.Add(c);
            // product_definition pulls in its formation and product already via
            // Closure, but chain members added above may bring new refs.
            foreach (var extra in Closure(chain)) toClone.Add(extra);

            var map = CloneAll(toClone);

            // The cloned solid is what gets the copy's colour.
            int solid = toClone.FirstOrDefault(i => _step.TypeOf(i) == "MANIFOLD_SOLID_BREP");
            return new PartCopy
            {
                Map = map,
                SolidId = solid == 0 ? -1 : map[solid],
                EntityCount = toClone.Count,
            };
        }

        /// <summary>
        /// Point one occurrence at a copy: its NAUO's part product definition,
        /// and the placement chain's reference to the part's shape
        /// representation. Safe to call for several occurrences with the same
        /// copy -- each rewires only its own entities.
        /// </summary>
        public void PointOccurrenceAt(StepRewriter.OccurrenceRef occ, PartCopy copy)
        {
            var map = copy.Map;
            RewireEntity(occ.NauoId, map);

            foreach (var cdsr in _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                bool mine = _step.Refs(cdsr).Any(r =>
                    _step.TypeOf(r) == "PRODUCT_DEFINITION_SHAPE"
                    && _step.Refs(r).Contains(occ.NauoId));
                if (!mine) continue;

                foreach (var rel in _step.Refs(cdsr))
                {
                    var t = _step.TypeOf(rel) ?? "";
                    if (t.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) >= 0)
                    {
                        RewireEntity(rel, map);
                        foreach (var idt in _step.Refs(rel))
                            if (_step.TypeOf(idt) == "ITEM_DEFINED_TRANSFORMATION")
                                RewireEntity(idt, map);
                    }
                }
            }
        }

        /// <summary>
        /// Recolour EVERY styled item that affects the part this occurrence
        /// refers to, for the occurrence that keeps the original geometry.
        ///
        /// SolidWorks writes two styled items per part -- one on the
        /// MANIFOLD_SOLID_BREP and one on the ADVANCED_BREP_SHAPE_REPRESENTATION
        /// -- and OCCT's reader lets the representation-level one win. Recolouring
        /// only the solid leaves the old colour visible, which is exactly how
        /// C4_component_override_2 came back orange instead of green.
        ///
        /// Returns the number of styled items repointed.
        /// </summary>
        public int RecolourPart(StepRewriter.OccurrenceRef occ, Rgb colour, double transparency)
        {
            var geometry = PartGeometryOf(occ.NauoId);
            if (geometry.Count == 0)
            {
                _log?.Invoke($"    NAUO #{occ.NauoId}: no part geometry found to recolour");
                return 0;
            }

            var mine = new HashSet<int>(_step.ByType("STYLED_ITEM")
                            .Where(s => _step.Refs(s).Any(geometry.Contains)));
            if (mine.Count == 0) return 0;

            // Prefer rewriting the existing COLOUR_RGB in place over building a
            // new style chain and repointing at it. Repointing leaves the old
            // chain in the file, unreferenced but still present -- dead
            // FILL_AREA_STYLE_COLOUR entities carrying the ORIGINAL colour,
            // which is both untidy and a plausible way for a reader to end up
            // showing the colour we meant to replace.
            int count = 0;
            foreach (var styled in mine)
            {
                var colourIds = ColoursUnder(styled);
                bool shared = colourIds.Any(c => IsReferencedOutside(c, mine));

                if (!shared && colourIds.Count > 0)
                {
                    foreach (var c in colourIds)
                        _step.Replace(c, $"#{c}=COLOUR_RGB('',{Part21.Num(colour.R)}," +
                                         $"{Part21.Num(colour.G)},{Part21.Num(colour.B)});");
                    count++;
                }
                else
                {
                    // Shared with styling we must not disturb: fall back to a
                    // fresh chain for this item only.
                    RecolourStyledItem(styled, colour, transparency);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// The geometric items belonging to the part this occurrence refers to:
        /// its shape representation, its B-rep representation, its solids and
        /// its faces.
        ///
        /// Faces are included because an override applied at or above the
        /// component beats everything inside the part -- including per-face
        /// colours, which SolidWorks exports correctly and which would
        /// otherwise keep winning over the override we are applying. The
        /// traversal to reach them is deliberately two levels deep
        /// (solid -> shell -> face) rather than a full closure: descending into
        /// the whole B-rep would walk every edge, vertex and surface in the
        /// part for no gain.
        /// </summary>
        private HashSet<int> PartGeometryOf(int nauoId)
        {
            var found = new HashSet<int>();
            int partPd = _step.Refs(nauoId).LastOrDefault(r => _step.TypeOf(r) == "PRODUCT_DEFINITION");
            if (partPd == 0) return found;

            int pds = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                           .FirstOrDefault(p => _step.Refs(p).Contains(partPd));
            int sdr = pds == 0 ? 0 : _step.ByType("SHAPE_DEFINITION_REPRESENTATION")
                           .FirstOrDefault(s => _step.Refs(s).Contains(pds));
            int partSr = sdr == 0 ? 0 : _step.Refs(sdr)
                           .FirstOrDefault(r => _step.TypeOf(r) == "SHAPE_REPRESENTATION");
            if (partSr == 0) return found;
            found.Add(partSr);

            foreach (var srr in _step.ByType("SHAPE_REPRESENTATION_RELATIONSHIP"))
            {
                if (!_step.Refs(srr).Contains(partSr)) continue;
                foreach (var r in _step.Refs(srr))
                {
                    var t = _step.TypeOf(r);
                    if (t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                     || t == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION")
                    {
                        found.Add(r);
                        foreach (var item in _step.Refs(r))
                            if (_step.TypeOf(item) == "MANIFOLD_SOLID_BREP"
                             || _step.TypeOf(item) == "SHELL_BASED_SURFACE_MODEL"
                             || _step.TypeOf(item) == "BREP_WITH_VOIDS")
                            {
                                found.Add(item);
                                AddFacesOf(item, found);
                            }
                    }
                }
            }
            return found;
        }

        /// <summary>solid -> shell -> face, two levels only.</summary>
        private void AddFacesOf(int solid, HashSet<int> found)
        {
            foreach (var shell in _step.Refs(solid))
            {
                var st = _step.TypeOf(shell);
                if (st != "CLOSED_SHELL" && st != "OPEN_SHELL") continue;
                foreach (var face in _step.Refs(shell))
                    if (_step.TypeOf(face) == "ADVANCED_FACE") found.Add(face);
            }
        }

        /// <summary>Every COLOUR_RGB reachable from a styled item.</summary>
        private List<int> ColoursUnder(int styledItem)
        {
            var found = new List<int>();
            var seen = new HashSet<int>();
            var stack = new Stack<int>(new[] { styledItem });
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!seen.Add(id) || !_step.Entities.ContainsKey(id)) continue;
                if (_step.TypeOf(id) == "COLOUR_RGB") { found.Add(id); continue; }
                // Do not descend into geometry: a styled item references the
                // solid it styles, and that subtree holds no colours worth
                // rewriting.
                var t = _step.TypeOf(id);
                if (t == "MANIFOLD_SOLID_BREP" || t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                 || t == "ADVANCED_FACE") continue;
                foreach (var r in _step.Refs(id)) stack.Push(r);
            }
            return found;
        }

        /// <summary>
        /// colour id -> every styled item that reaches it. Built once from the
        /// file SolidWorks wrote.
        ///
        /// The obvious implementation -- re-walk every styled item for every
        /// colour -- is quadratic in styled items, and a part whose faces are
        /// individually coloured has one per face. On a large assembly that is
        /// the difference between an export and a hang.
        ///
        /// Style chains we append ourselves are not indexed, and do not need to
        /// be: their colours are freshly allocated ids that no pre-existing
        /// styled item can reference.
        /// </summary>
        private Dictionary<int, HashSet<int>> _colourOwners;

        private Dictionary<int, HashSet<int>> ColourOwners()
        {
            if (_colourOwners != null) return _colourOwners;
            _colourOwners = new Dictionary<int, HashSet<int>>();
            foreach (var styled in _step.ByType("STYLED_ITEM")
                                        .Concat(_step.ByType("OVER_RIDING_STYLED_ITEM"))
                                        .Concat(_step.ByType("CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM")))
            {
                foreach (var c in ColoursUnder(styled))
                {
                    if (!_colourOwners.TryGetValue(c, out var owners))
                        _colourOwners[c] = owners = new HashSet<int>();
                    owners.Add(styled);
                }
            }
            return _colourOwners;
        }

        /// <summary>
        /// True when this colour is reachable from some styled item outside the
        /// set we are recolouring -- in which case rewriting it in place would
        /// silently change another part's appearance.
        /// </summary>
        private bool IsReferencedOutside(int colourId, HashSet<int> ours)
        {
            if (!ColourOwners().TryGetValue(colourId, out var owners)) return false;
            foreach (var styled in owners)
                if (!ours.Contains(styled)) return true;
            return false;
        }

        /// <summary>Repoint one existing styled item at a fresh colour.</summary>
        public void RecolourStyledItem(int styledItemId, Rgb colour, double transparency)
        {
            int psa = BuildStyleChain(colour, transparency);
            string args = _step.ArgsOf(styledItemId);
            var m = Regex.Match(args ?? "", @"^\(\s*('(?:[^']|'')*'|\$)\s*,\s*\(([^)]*)\)\s*,\s*#(\d+)");
            if (!m.Success)
            {
                _log?.Invoke($"    could not parse styled item #{styledItemId}; leaving it alone");
                return;
            }
            _step.Replace(styledItemId,
                $"#{styledItemId}=STYLED_ITEM({m.Groups[1].Value},(#{psa}),#{m.Groups[3].Value});");
        }

        /// <summary>Plain styled item on a solid -- what every consumer reads.</summary>
        public int AddPlainStyle(int itemId, Rgb colour, double transparency)
        {
            int psa = BuildStyleChain(colour, transparency);
            int styled = _step.NextId();
            _step.Append($"#{styled}=STYLED_ITEM('colour',(#{psa}),#{itemId});");
            return styled;
        }

        private int BuildStyleChain(Rgb colour, double transparency)
        {
            int col = _step.NextId();
            _step.Append($"#{col}=COLOUR_RGB('',{Part21.Num(colour.R)}," +
                         $"{Part21.Num(colour.G)},{Part21.Num(colour.B)});");
            int fac = _step.NextId();
            _step.Append($"#{fac}=FILL_AREA_STYLE_COLOUR('',#{col});");
            int fas = _step.NextId();
            _step.Append($"#{fas}=FILL_AREA_STYLE('',(#{fac}));");
            int ssfa = _step.NextId();
            _step.Append($"#{ssfa}=SURFACE_STYLE_FILL_AREA(#{fas});");

            var elems = new List<string> { $"#{ssfa}" };
            if (transparency > 1e-6)
            {
                int tr = _step.NextId();
                _step.Append($"#{tr}=SURFACE_STYLE_TRANSPARENT({Part21.Num(transparency)});");
                int rend = _step.NextId();
                _step.Append($"#{rend}=SURFACE_STYLE_RENDERING_WITH_PROPERTIES(" +
                             $".NORMAL_SHADING.,#{col},(#{tr}));");
                elems.Add($"#{rend}");
            }

            int sss = _step.NextId();
            _step.Append($"#{sss}=SURFACE_SIDE_STYLE('',({string.Join(",", elems)}));");
            int ssu = _step.NextId();
            _step.Append($"#{ssu}=SURFACE_STYLE_USAGE(.BOTH.,#{sss});");
            int psa = _step.NextId();
            _step.Append($"#{psa}=PRESENTATION_STYLE_ASSIGNMENT((#{ssu}));");
            return psa;
        }
    }
}
