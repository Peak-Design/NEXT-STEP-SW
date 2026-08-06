using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Peak.NextStep.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// Runs the full add-in pipeline on the document that is open in the
    /// RUNNING SolidWorks, and writes the result to the output directory. The
    /// user's file is never touched. This exists to test the pipeline on a
    /// real assembly without the dialog, and without a rebuild of the add-in.
    ///
    /// This verb attaches to the user's session, so it must not change the
    /// session: no Visible, no UserControl, no document open or close.
    /// </summary>
    public static class FixVerb
    {
        public static int Run(string outDir, bool deInstance, bool materials,
                              string modelPath = null)
        {
            ISldWorks app = null;
            SwSession owned = null;
            IModelDoc2 opened = null;
            try
            {
                try { app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
                catch (COMException)
                {
                    if (modelPath == null)
                    {
                        Log.Info("no running SolidWorks instance to attach to, "
                               + "and no --model to open");
                        return 2;
                    }
                    owned = SwSession.Connect(visible: false);
                    app = owned.App;
                }

                var model = app.ActiveDoc as IModelDoc2;
                if (model == null && modelPath != null && owned != null)
                {
                    Log.Info($"opening {modelPath}");
                    var t = DateTime.Now;
                    model = opened = owned.Open(modelPath, out var error);
                    if (model == null) { Log.Info("open failed: " + error); return 2; }
                    Log.Info($"opened in {(DateTime.Now - t).TotalSeconds:F0}s");
                }
                if (model == null) { Log.Info("no active document"); return 2; }
                return RunOn(app, model, outDir, deInstance, materials);
            }
            finally
            {
                if (opened != null) owned.Close(opened);
                owned?.Dispose();
            }
        }

        private static int RunOn(ISldWorks app, IModelDoc2 model, string outDir,
                                 bool deInstance, bool materials)
        {
            string docName = Path.GetFileNameWithoutExtension(model.GetPathName());
            Log.Info($"active document: {docName}");

            string native = Path.Combine(outDir, docName + "__native.step");
            string target = Path.Combine(outDir, docName + "__nextstep.step");

            // ── 1. The SolidWorks export, with saved and restored prefs ─────
            int savedAp = app.GetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swStepAP);
            bool savedAppearances = false, appearancesAvailable = true;
            try { savedAppearances = app.GetUserPreferenceToggle(SwSession.SwStepExportAppearances); }
            catch { appearancesAvailable = false; }

            bool ok;
            int errors = 0, warnings = 0;
            try
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214);
                if (appearancesAvailable)
                    app.SetUserPreferenceToggle(SwSession.SwStepExportAppearances, true);

                var t0 = DateTime.Now;
                ok = model.Extension.SaveAs3(
                    native, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                    ref errors, ref warnings);
                Log.Info($"native export: ok={ok} errors={errors} warnings={warnings} " +
                         $"in {(DateTime.Now - t0).TotalSeconds:F0}s, " +
                         $"{(ok ? new FileInfo(native).Length : 0)} bytes");
            }
            finally
            {
                try { app.SetUserPreferenceIntegerValue(
                    (int)swUserPreferenceIntegerValue_e.swStepAP, savedAp); } catch { }
                if (appearancesAvailable)
                    try { app.SetUserPreferenceToggle(
                        SwSession.SwStepExportAppearances, savedAppearances); } catch { }
            }
            if (!ok) return 3;

            // ── 2. The repair ───────────────────────────────────────────────
            File.Copy(native, target, true);
            var t1 = DateTime.Now;
            var rw = new StepRewriter(target, m => Log.Info(m));
            Log.Info($"parsed {rw.Document.Entities.Count} entities " +
                     $"in {(DateTime.Now - t1).TotalSeconds:F1}s");

            var occurrences = AppearanceLadder.Resolve(model, m => Log.Info(m));
            var refs = rw.FindOccurrences();
            Log.Info($"STEP file: {refs.Count} occurrence(s), root pd #{rw.RootPd}");

            var pairs = new OccurrenceMatcher(model, m => Log.Info(m)).Match(occurrences, rw);

            int matched = pairs.Count(p => p.Value != null);
            int unmatchedOverridden = pairs.Count(
                p => p.Key.OverridesPartInternals && p.Value == null);
            int applied = rw.ApplyOccurrenceColours(pairs, deInstance);

            if (materials)
            {
                var mats = MaterialHarvester.Harvest(model, m => Log.Info(m));
                int n = new MaterialWriter(rw.Document, m => Log.Info(m))
                    .Apply(mats, deInstance ? rw.BucketIndexByProduct : null);
                Log.Info($"engineering material written for {n} part(s)");
            }

            rw.Save(target);

            Log.Info("");
            Log.Info("== SUMMARY ==");
            Log.Info($"  occurrences (SolidWorks): {occurrences.Count}, " +
                     $"exported {occurrences.Count(o => o.Exported)}");
            Log.Info($"  occurrences (STEP file):  {refs.Count}");
            Log.Info($"  matched: {matched}   overridden-but-unmatched: {unmatchedOverridden}");
            Log.Info($"  appearances applied: {applied}");
            Log.Info($"  output: {target}");
            return unmatchedOverridden == 0 ? 0 : 1;
        }
    }
}
