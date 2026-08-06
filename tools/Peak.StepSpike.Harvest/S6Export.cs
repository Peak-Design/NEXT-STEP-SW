using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.NextStep.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// S6 -- the end-to-end vertical slice, and the prototype of what the
    /// add-in will do:
    ///
    ///   1. let SolidWorks export AP214 with appearances (per-face styling is
    ///      already correct, so it is left alone)
    ///   2. resolve the appearance ladder per occurrence
    ///   3. splice occurrence-bound styling into the file, geometry untouched
    /// </summary>
    public static class S6Export
    {
        public static bool DeInstance;
        public static bool Materials;

        public static int Run(string corpusDir, string outDir, bool visible, string only)
        {
            Directory.CreateDirectory(outDir);
            var models = Directory.GetFiles(corpusDir, "*.SLDASM").ToList();
            if (!string.IsNullOrEmpty(only))
                models = models.Where(m => Path.GetFileNameWithoutExtension(m)
                    .IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            models.Sort(StringComparer.OrdinalIgnoreCase);

            if (models.Count == 0) { Log.Info("no assemblies matched"); return 2; }

            using (var sw = SwSession.Connect(visible))
            {
                // Appearance export on, AP214, no periodic splitting: we want the
                // file SolidWorks is best at, and splitting only inflates faces.
                sw.SetInt(swUserPreferenceIntegerValue_e.swStepAP, 214);
                sw.TrySetToggle(SwSession.SwStepExportAppearances, true, "swStepExportAppearances");
                sw.TrySetToggle(swUserPreferenceToggle_e.swStepExportSplitPeriodic, false);

                foreach (var path in models)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    Log.Info($"--- {name} ---");
                    string err;
                    var model = sw.Open(path, out err);
                    if (model == null) { Log.Info($"  SKIP {err}"); continue; }

                    try { ExportOne(model, name, outDir); }
                    catch (Exception ex) { Log.Info($"  FAILED: {ex}"); }
                    finally { sw.Close(model); }
                }
            }
            return 0;
        }

        private static void ExportOne(IModelDoc2 model, string name, string outDir)
        {
            // 1. SolidWorks' own export.
            string native = Path.Combine(outDir, name + "__native.step");
            int errors = 0, warnings = 0;
            bool ok = model.Extension.SaveAs3(
                native, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                ref errors, ref warnings);
            if (!ok) { Log.Info($"  native export failed (errors={errors})"); return; }
            Log.Info($"  native export: {new FileInfo(native).Length} bytes");

            // 2. Resolve what SolidWorks actually displays, per occurrence.
            Log.Info("  resolving appearance ladder:");
            var occurrences = AppearanceLadder.Resolve(model, m => Log.Info(m));
            var needFixing = occurrences.Where(o => o.OverridesPartInternals).ToList();
            Log.Info($"  {occurrences.Count} occurrences, {needFixing.Count} need occurrence styling");

            // 3. Splice.
            var rw = new StepRewriter(native, m => Log.Info(m));
            var refs = rw.FindOccurrences();
            Log.Info($"  found {refs.Count} occurrence(s) in the STEP file:");
            foreach (var r in refs)
                Log.Info($"    NAUO #{r.NauoId} translation=" +
                         (r.Translation == null ? "?" :
                          string.Join(",", r.Translation.Select(v => v.ToString("F2")))) +
                         $" target=#{r.TargetItemId} base=#{r.BaseStyledItemId}");

            if (refs.Count != occurrences.Count)
                Log.Info($"  WARNING: {occurrences.Count} components but {refs.Count} " +
                         "occurrences in file -- mapping is not 1:1, colours may be wrong");

            var matched = new OccurrenceMatcher(model, m => Log.Info(m)).Match(occurrences, refs);
            int applied = rw.ApplyOccurrenceColours(matched, DeInstance);

            if (Materials)
            {
                var mats = MaterialHarvester.Harvest(model, m => Log.Info(m));
                int n = new MaterialWriter(rw.Document, m => Log.Info(m))
                    .Apply(mats, DeInstance ? rw.BucketIndexByProduct : null);
                Log.Info($"  engineering material written for {n} part(s)");
            }

            string fixedPath = Path.Combine(outDir, name + (DeInstance ? "__peak_deinst.step" : "__peak.step"));
            rw.Save(fixedPath);
            Log.Info($"  {applied} occurrence colour(s) applied -> {Path.GetFileName(fixedPath)}");
        }
    }
}
