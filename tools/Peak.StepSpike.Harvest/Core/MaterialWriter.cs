using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Peak.NextStep.Core
{
    public sealed class PartMaterial
    {
        /// <summary>The STEP product name, which SolidWorks names after the part file.</summary>
        public string ProductName;
        public string Name;
        public string Database;
        /// <summary>kg/m^3.</summary>
        public double Density;
    }

    /// <summary>
    /// Writes engineering material (name, description, density) into the STEP
    /// file. SolidWorks does not export this at all, in any AP.
    ///
    /// The encoding is the one OCCT itself writes and reads, verified against
    /// STEPper NEXT's own baselines (ci/baselines/mat_ap214.step):
    ///
    ///     PROPERTY_DEFINITION('material property','material name',#pd)
    ///       -> REPRESENTATION -> DESCRIPTIVE_REPRESENTATION_ITEM(name, description)
    ///     PROPERTY_DEFINITION('material property','density',#pd)
    ///       -> REPRESENTATION -> MEASURE_REPRESENTATION_ITEM('density', value, #kgPerM3)
    ///
    /// AP242 is NOT required for this: the emission is byte-identical in AP214
    /// and AP242, so no MBD licence is involved (FINDINGS.md 3b.1).
    /// </summary>
    public sealed class MaterialWriter
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;
        private int _densityUnit = -1;

        public MaterialWriter(Part21 step, Action<string> log)
        {
            _step = step;
            _log = log;
        }

        /// <summary>Reuse an existing representation context rather than inventing one.</summary>
        private int AnyContext()
        {
            foreach (var kv in _step.Entities)
                if ((kv.Value.Key ?? "").IndexOf("GEOMETRIC_REPRESENTATION_CONTEXT",
                                                 StringComparison.Ordinal) >= 0)
                    return kv.Key;
            return -1;
        }

        /// <summary>
        /// The density unit, created once per file.
        ///
        /// This replicates OCCT's own encoding exactly, taken from
        /// ci/baselines/mat_ap214.step:
        ///
        ///     DERIVED_UNIT(( DERIVED_UNIT_ELEMENT(&lt;gram&gt;, 3.),
        ///                    DERIVED_UNIT_ELEMENT(&lt;centimetre&gt;, 2.) ))
        ///
        /// It is NOT dimensionally kg/m^3 -- the exponents do not describe
        /// mass/length^3 in any reading -- but it is what OCCT writes and what
        /// its reader round-trips, and STEPper NEXT reads density through OCCT.
        /// A physically-correct DERIVED_UNIT of (kilogram, 1) and (metre, -3)
        /// was tried first and came back as 0.0078 instead of 7800, because the
        /// reader rescaled it against the file's millimetre length unit.
        /// Matching the encoding that demonstrably works beats being right on
        /// paper and wrong in the consumer.
        /// </summary>
        private int DensityUnit()
        {
            if (_densityUnit > 0) return _densityUnit;

            int gram = _step.NextId();
            _step.Append($"#{gram}=( MASS_UNIT() NAMED_UNIT(*) SI_UNIT($,.GRAM.) );");
            int centimetre = _step.NextId();
            _step.Append($"#{centimetre}=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.CENTI.,.METRE.) );");
            int e1 = _step.NextId();
            _step.Append($"#{e1}=DERIVED_UNIT_ELEMENT(#{gram},3.);");
            int e2 = _step.NextId();
            _step.Append($"#{e2}=DERIVED_UNIT_ELEMENT(#{centimetre},2.);");
            _densityUnit = _step.NextId();
            _step.Append($"#{_densityUnit}=DERIVED_UNIT((#{e1},#{e2}));");
            return _densityUnit;
        }

        /// <summary>
        /// Blender truncates material names at 60 characters, so a suffix
        /// appended past that would be cut off and two buckets would collapse
        /// back into one material. The base name gives way instead.
        /// </summary>
        private const int MaxNameLength = 60;

        internal static string BucketName(string baseName, int bucketIndex)
        {
            if (bucketIndex <= 0) return baseName;
            string suffix = "." + bucketIndex.ToString("000", CultureInfo.InvariantCulture);
            int room = MaxNameLength - suffix.Length;
            if (baseName.Length > room) baseName = baseName.Substring(0, room);
            return baseName + suffix;
        }

        /// <summary>
        /// Attach materials to every product whose name matches. Returns how
        /// many products were given a material.
        ///
        /// bucketIndexByProduct, when supplied, splits one CAD material into
        /// one named variant per appearance bucket: "Plain Carbon Steel",
        /// "Plain Carbon Steel.001" and so on.
        ///
        /// This is a workaround with a cost, and the cost is worth stating.
        /// STEP has no association between a material and an appearance -- the
        /// two are deliberately separate, and no entity in AP214 or AP242 links
        /// them. Consumers that build one material per material NAME therefore
        /// collapse every differently-coloured copy of a part into a single
        /// material and lose the colours. Numbering the name per bucket makes
        /// those consumers produce one material per colour instead. The price
        /// is that a file exported this way reports "Plain Carbon Steel.001" to
        /// anything reading the material properly, which is not the name of a
        /// material. It is opt-in for that reason.
        /// </summary>
        public int Apply(IEnumerable<PartMaterial> materials,
                         IDictionary<int, int> bucketIndexByProduct = null)
        {
            int context = AnyContext();
            if (context < 0)
            {
                _log?.Invoke("    no representation context found; skipping materials");
                return 0;
            }

            var byName = new Dictionary<string, PartMaterial>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in materials)
                if (!string.IsNullOrEmpty(m?.ProductName) && !string.IsNullOrEmpty(m.Name))
                    byName[m.ProductName] = m;
            if (byName.Count == 0) return 0;

            int applied = 0;
            foreach (var product in _step.ByType("PRODUCT"))
            {
                string productName = _step.NameOf(product);
                if (productName == null || !byName.TryGetValue(productName, out var mat)) continue;

                int pd = ProductDefinitionOf(product);
                if (pd < 0)
                {
                    _log?.Invoke($"    {productName}: no product_definition; skipped");
                    continue;
                }

                int bucket = 0;
                bucketIndexByProduct?.TryGetValue(product, out bucket);
                string name = BucketName(mat.Name, bucket);

                WriteNamed(pd, context, "material name",
                           $"DESCRIPTIVE_REPRESENTATION_ITEM({Part21.Str(name)}," +
                           $"{Part21.Str(mat.Database ?? "")})");

                if (mat.Density > 0)
                    WriteNamed(pd, context, "density",
                               $"MEASURE_REPRESENTATION_ITEM('density'," +
                               $"{Part21.Num(mat.Density)},#{DensityUnit()})");

                applied++;
                _log?.Invoke($"    {productName} (#{product}): material '{name}' " +
                             $"density {mat.Density:F1} kg/m^3");
            }
            return applied;
        }

        private void WriteNamed(int productDefinition, int context, string role, string itemEntity)
        {
            int item = _step.NextId();
            _step.Append($"#{item}={itemEntity};");
            int rep = _step.NextId();
            _step.Append($"#{rep}=REPRESENTATION({Part21.Str(role)},(#{item}),#{context});");
            int pdef = _step.NextId();
            _step.Append($"#{pdef}=PROPERTY_DEFINITION('material property'," +
                         $"{Part21.Str(role)},#{productDefinition});");
            int pdr = _step.NextId();
            _step.Append($"#{pdr}=PROPERTY_DEFINITION_REPRESENTATION(#{pdef},#{rep});");
        }

        /// <summary>PRODUCT &lt;- PRODUCT_DEFINITION_FORMATION &lt;- PRODUCT_DEFINITION.</summary>
        private int ProductDefinitionOf(int product)
        {
            var formations = _step.ByType("PRODUCT_DEFINITION_FORMATION")
                .Concat(_step.ByType("PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE"))
                .Where(f => _step.Refs(f).Contains(product))
                .ToList();

            foreach (var f in formations)
            {
                var pd = _step.ByType("PRODUCT_DEFINITION").FirstOrDefault(p => _step.Refs(p).Contains(f));
                if (pd != 0) return pd;
            }
            return -1;
        }
    }
}
