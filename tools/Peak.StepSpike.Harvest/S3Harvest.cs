using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// S3 -- appearance harvest fidelity. Can we extract from SolidWorks,
    /// correctly and completely, the appearance the user actually sees?
    ///
    /// Two independent channels are dumped so they can be compared:
    ///   * the RESOLVED channel -- MaterialPropertyValues, a 9-double vector
    ///     [R,G,B,Ambient,Diffuse,Specular,Shininess,Transparency,Emission]
    ///     available on IFace2, IBody2, IComponent2 and IPartDoc. If this
    ///     already answers "what colour is this face, in this instance", we
    ///     never need to reimplement SolidWorks' six-scope precedence.
    ///   * the RICH channel -- IRenderMaterial, which carries everything the
    ///     appearance really has (roughness, specular spread, texture, mapping)
    ///     but is attached per scope and must be resolved by us.
    ///
    /// Note on a plan assumption that did not survive contact: the plan hoped to
    /// use IComponent2.IGetMaterialPropertyValuesForFace(face) as a ready-made
    /// resolver. In the interop it is declared `Double
    /// IGetMaterialPropertyValuesForFace(Object)` -- the C++ pointer-returning
    /// form, with no C#-usable overload -- so it cannot be called from managed
    /// code. The comparison below therefore uses IComponent2's own
    /// MaterialPropertyValues plus per-face values instead.
    /// </summary>
    public static class S3Harvest
    {
        public static int Run(string corpusDir, string outDir, bool visible, int maxFaces,
                              string singleModel = null, string reportName = "s3-harvest.json")
        {
            Directory.CreateDirectory(outDir);
            var models = new List<string>();
            if (!string.IsNullOrEmpty(singleModel))
            {
                // C6 lives outside the repo and is referenced by path, so the
                // perf run targets one file rather than a directory.
                models.Add(singleModel);
            }
            else
            {
                foreach (var pattern in new[] { "*.SLDPRT", "*.SLDASM" })
                    models.AddRange(Directory.GetFiles(corpusDir, pattern));
                models.Sort(StringComparer.OrdinalIgnoreCase);
            }

            var json = new StringBuilder();
            json.AppendLine("{");
            bool firstModel = true;

            using (var sw = SwSession.Connect(visible))
            {
                foreach (var path in models)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    Log.Info($"--- {name} ---");
                    string openError;
                    var model = sw.Open(path, out openError);
                    if (model == null) { Log.Info($"  SKIP {openError}"); continue; }

                    try
                    {
                        if (!firstModel) json.AppendLine(",");
                        firstModel = false;
                        json.Append($"  \"{Esc(name)}\": ");
                        json.Append(HarvestModel(model, name, maxFaces));
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"  FAILED: {ex.Message}");
                        json.Append($"{{\"error\": \"{Esc(ex.Message)}\"}}");
                    }
                    finally { sw.Close(model); }
                }
            }

            json.AppendLine();
            json.AppendLine("}");
            string report = Path.Combine(outDir, reportName);
            File.WriteAllText(report, json.ToString());
            Log.Info($"wrote {report}");
            return 0;
        }

        private static string HarvestModel(IModelDoc2 model, string name, int maxFaces)
        {
            var sb = new StringBuilder();
            var clock = Stopwatch.StartNew();
            int faceCount = 0, propCalls = 0;

            sb.AppendLine("{");
            sb.AppendLine($"    \"docType\": {model.GetType()},");

            // ---- document-level appearances (the rich channel) -------------
            var ext = model.Extension;
            int docCount = ext.GetRenderMaterialsCount2(
                (int)swDisplayStateOpts_e.swAllDisplayState, null);
            sb.AppendLine($"    \"docRenderMaterialCount\": {docCount},");
            sb.Append("    \"docRenderMaterials\": ");
            sb.AppendLine(DumpRenderMaterials(
                ext.GetRenderMaterials2((int)swDisplayStateOpts_e.swAllDisplayState, null)) + ",");

            // ---- configurations and display states -------------------------
            var cfgNames = model.GetConfigurationNames() as string[] ?? new string[0];
            sb.AppendLine($"    \"configurations\": [{string.Join(", ", cfgNames.Select(c => $"\"{Esc(c)}\""))}],");
            var activeCfg = model.GetActiveConfiguration() as IConfiguration;
            var dsNames = activeCfg?.GetDisplayStates() as string[] ?? new string[0];
            sb.AppendLine($"    \"displayStates\": [{string.Join(", ", dsNames.Select(d => $"\"{Esc(d)}\""))}],");

            // ---- components (assemblies only) ------------------------------
            if (model is IAssemblyDoc assy)
            {
                var comps = assy.GetComponents(false) as object[] ?? new object[0];
                sb.AppendLine($"    \"componentCount\": {comps.Length},");
                sb.AppendLine("    \"components\": [");
                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = (IComponent2)comps[i];
                    sb.Append("      {");
                    sb.Append($"\"name\": \"{Esc(comp.Name2)}\", ");
                    sb.Append($"\"refConfig\": \"{Esc(comp.ReferencedConfiguration)}\", ");
                    sb.Append($"\"resolvedMaterialPropertyValues\": {Dump9(comp.MaterialPropertyValues)}, ");
                    sb.Append($"\"hasMaterialPropertyValues\": {Low(comp.HasMaterialPropertyValues())}, ");
                    propCalls++;

                    int cCount = comp.GetRenderMaterialsCount2(
                        (int)swDisplayStateOpts_e.swAllDisplayState, null);
                    sb.Append($"\"renderMaterialCount\": {cCount}, ");
                    sb.Append("\"renderMaterials\": ");
                    sb.Append(DumpRenderMaterials(comp.GetRenderMaterials2(
                        (int)swDisplayStateOpts_e.swAllDisplayState, null)));

                    // Per-face resolved values inside this component instance.
                    var faceDump = DumpComponentFaces(comp, maxFaces, ref faceCount, ref propCalls);
                    sb.Append($", \"faces\": {faceDump}");
                    sb.Append("}");
                    if (i < comps.Length - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("    ],");
            }
            else
            {
                sb.Append("    \"faces\": ");
                sb.AppendLine(DumpPartFaces(model, maxFaces, ref faceCount, ref propCalls) + ",");
            }

            clock.Stop();
            double secs = clock.Elapsed.TotalSeconds;
            sb.AppendLine($"    \"faceCount\": {faceCount},");
            sb.AppendLine($"    \"propertyCalls\": {propCalls},");
            sb.AppendLine($"    \"harvestSeconds\": {secs.ToString("F3", CultureInfo.InvariantCulture)},");
            sb.AppendLine($"    \"facesPerSecond\": {(faceCount / Math.Max(secs, 1e-6)).ToString("F1", CultureInfo.InvariantCulture)}");
            sb.Append("  }");

            Log.Info($"  faces={faceCount} propCalls={propCalls} " +
                     $"{secs.ToString("F2", CultureInfo.InvariantCulture)}s " +
                     $"({(faceCount / Math.Max(secs, 1e-6)).ToString("F0", CultureInfo.InvariantCulture)} faces/s)");
            return sb.ToString();
        }

        private static string DumpComponentFaces(IComponent2 comp, int maxFaces,
                                                 ref int faceCount, ref int propCalls)
        {
            var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[];
            return DumpBodies(bodies, maxFaces, ref faceCount, ref propCalls);
        }

        private static string DumpPartFaces(IModelDoc2 model, int maxFaces,
                                            ref int faceCount, ref int propCalls)
        {
            object[] bodies = null;
            if (model is IPartDoc part)
                bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            return DumpBodies(bodies, maxFaces, ref faceCount, ref propCalls);
        }

        private static string DumpBodies(object[] bodies, int maxFaces,
                                         ref int faceCount, ref int propCalls)
        {
            var sb = new StringBuilder("[");
            if (bodies == null) return "[]";
            bool first = true;
            int emitted = 0;

            foreach (var b in bodies)
            {
                var body = (IBody2)b;
                var face = (IFace2)body.GetFirstFace();
                while (face != null)
                {
                    faceCount++;
                    if (emitted < maxFaces)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        emitted++;

                        object vals = null;
                        bool has = false;
                        try
                        {
                            has = face.HasMaterialPropertyValues();
                            vals = face.GetMaterialPropertyValues2(
                                (int)swInConfigurationOpts_e.swThisConfiguration, null);
                            propCalls += 2;
                        }
                        catch { /* recorded as null below */ }

                        var surf = face.GetSurface() as ISurface;
                        string surfType = SurfaceType(surf);

                        // Decals are a separate channel from appearances and do
                        // not show up in GetRenderMaterials2 at all.
                        int decals = 0;
                        try
                        {
                            var dec = face.GetAllDecalProperties() as object[];
                            decals = dec?.Length ?? 0;
                        }
                        catch { decals = -1; }

                        sb.Append("{");
                        sb.Append($"\"decalCount\": {decals}, ");
                        sb.Append($"\"surface\": \"{surfType}\", ");
                        sb.Append($"\"area\": {face.GetArea().ToString("G9", CultureInfo.InvariantCulture)}, ");
                        sb.Append($"\"hasOwnAppearance\": {Low(has)}, ");
                        sb.Append($"\"appearanceName\": \"{Esc(face.MaterialUserName ?? "")}\", ");
                        sb.Append($"\"appearanceId\": \"{Esc(face.MaterialIdName ?? "")}\", ");
                        sb.Append($"\"resolved\": {Dump9(vals)}");
                        sb.Append("}");
                    }
                    face = (IFace2)face.GetNextFace();
                }
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string SurfaceType(ISurface s)
        {
            if (s == null) return "none";
            try
            {
                if (s.IsPlane()) return "plane";
                if (s.IsCylinder()) return "cylinder";
                if (s.IsCone()) return "cone";
                if (s.IsSphere()) return "sphere";
                if (s.IsTorus()) return "torus";
            }
            catch { }
            return "other";
        }

        /// <summary>Dump every IRenderMaterial property the study cares about.</summary>
        private static string DumpRenderMaterials(object raw)
        {
            var arr = raw as object[];
            if (arr == null || arr.Length == 0) return "[]";

            var sb = new StringBuilder("[");
            for (int i = 0; i < arr.Length; i++)
            {
                var rm = arr[i] as IRenderMaterial;
                if (rm == null) continue;
                if (i > 0) sb.Append(", ");
                sb.Append("{");
                sb.Append($"\"fileName\": \"{Esc(SafeStr(() => rm.FileName))}\", ");
                sb.Append($"\"primaryColor\": {ColorJson(SafeInt(() => rm.PrimaryColor))}, ");
                sb.Append($"\"secondaryColor\": {ColorJson(SafeInt(() => rm.SecondaryColor))}, ");
                sb.Append($"\"specularColor\": {ColorJson(SafeInt(() => rm.SpecularColor))}, ");
                // Shading. Note the real property set differs from what the
                // API help index suggests: there is no SpecularSpread,
                // RoughnessSpacing, BlurryReflection or Luminous on
                // IRenderMaterial. The nearest real equivalents are Glossy,
                // Emission and the Metallic* family.
                sb.Append($"\"ambient\": {D(SafeDbl(() => rm.Ambient))}, ");
                sb.Append($"\"diffuse\": {D(SafeDbl(() => rm.Diffuse))}, ");
                sb.Append($"\"specular\": {D(SafeDbl(() => rm.Specular))}, ");
                sb.Append($"\"glossy\": {D(SafeDbl(() => rm.Glossy))}, ");
                sb.Append($"\"roughness\": {D(SafeDbl(() => rm.Roughness))}, ");
                sb.Append($"\"reflectivity\": {D(SafeDbl(() => rm.Reflectivity))}, ");
                sb.Append($"\"transparency\": {D(SafeDbl(() => rm.Transparency))}, ");
                sb.Append($"\"translucency\": {D(SafeDbl(() => rm.Translucency))}, ");
                sb.Append($"\"emission\": {D(SafeDbl(() => rm.Emission))}, ");
                sb.Append($"\"indexOfRefraction\": {D(SafeDbl(() => rm.IndexOfRefraction))}, ");
                sb.Append($"\"metallicRoughness\": {D(SafeDbl(() => rm.MetallicRoughness))}, ");
                sb.Append($"\"metallicMix\": {D(SafeDbl(() => rm.MetallicMix))}, ");
                sb.Append($"\"doubleSided\": {Low(SafeBool(() => rm.DoubleSided))}, ");
                sb.Append($"\"illuminationShaderType\": {SafeInt(() => rm.IlluminationShaderType)}, ");
                // Texture and its real-world mapping frame -- the data the
                // study needs for the glTF companion (R3).
                sb.Append($"\"textureFilename\": \"{Esc(SafeStr(() => rm.TextureFilename))}\", ");
                sb.Append($"\"bumpTextureFilename\": \"{Esc(SafeStr(() => rm.BumpTextureFilename))}\", ");
                sb.Append($"\"bumpAmplitude\": {D(SafeDbl(() => rm.BumpAmplitude))}, ");
                sb.Append($"\"mappingType\": {SafeInt(() => rm.MappingType)}, ");
                sb.Append($"\"projectionReference\": {SafeInt(() => rm.ProjectionReference)}, ");
                sb.Append($"\"widthMetres\": {D(SafeDbl(() => rm.Width))}, ");
                sb.Append($"\"heightMetres\": {D(SafeDbl(() => rm.Height))}, ");
                sb.Append($"\"xPosition\": {D(SafeDbl(() => rm.XPosition))}, ");
                sb.Append($"\"yPosition\": {D(SafeDbl(() => rm.YPosition))}, ");
                sb.Append($"\"rotationAngle\": {D(SafeDbl(() => rm.RotationAngle))}, ");
                sb.Append($"\"fitWidth\": {Low(SafeBool(() => rm.FitWidth))}, ");
                sb.Append($"\"fitHeight\": {Low(SafeBool(() => rm.FitHeight))}, ");
                sb.Append($"\"entityCount\": {SafeInt(() => rm.GetEntitiesCount())}, ");
                sb.Append($"\"entityTypes\": {EntityTypes(rm)}, ");
                sb.Append($"\"linkedDisplayStates\": {StrArray(SafeObj(() => rm.GetLinkedDisplayStates()))}");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string EntityTypes(IRenderMaterial rm)
        {
            object raw;
            try { raw = rm.GetEntities(); } catch { return "[]"; }
            var arr = raw as object[];
            if (arr == null) return "[]";
            var kinds = new List<string>();
            foreach (var e in arr)
            {
                if (e is IFace2) kinds.Add("face");
                else if (e is IBody2) kinds.Add("body");
                else if (e is IComponent2) kinds.Add("component");
                else if (e is IFeature) kinds.Add("feature");
                else if (e is IPartDoc) kinds.Add("part");
                else kinds.Add(e?.GetType().Name ?? "null");
            }
            return "[" + string.Join(", ", kinds.Select(k => $"\"{k}\"")) + "]";
        }

        // -- small helpers -------------------------------------------------
        private static string Dump9(object vals)
        {
            var d = vals as double[];
            if (d == null) return "null";
            return "[" + string.Join(", ",
                d.Select(v => v.ToString("G6", CultureInfo.InvariantCulture))) + "]";
        }

        private static string ColorJson(int c)
        {
            // SolidWorks packs appearance colour as COLORREF: 0x00BBGGRR.
            if (c < 0) return "null";
            int r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;
            return $"[{(r / 255.0).ToString("F4", CultureInfo.InvariantCulture)}, " +
                   $"{(g / 255.0).ToString("F4", CultureInfo.InvariantCulture)}, " +
                   $"{(b / 255.0).ToString("F4", CultureInfo.InvariantCulture)}]";
        }

        private static string StrArray(object raw)
        {
            var arr = raw as string[];
            if (arr == null) return "[]";
            return "[" + string.Join(", ", arr.Select(s => $"\"{Esc(s)}\"")) + "]";
        }

        private static string D(double v)
            => double.IsNaN(v) ? "null" : v.ToString("G6", CultureInfo.InvariantCulture);

        private static string Low(bool b) => b ? "true" : "false";
        private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
        private static double SafeDbl(Func<double> f) { try { return f(); } catch { return double.NaN; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return -1; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static object SafeObj(Func<object> f) { try { return f(); } catch { return null; } }

        private static string Esc(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\r", " ").Replace("\n", " ");
    }
}
