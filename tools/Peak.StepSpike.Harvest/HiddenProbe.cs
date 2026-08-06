using Peak.NextStep.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// What does SolidWorks write for a HIDDEN component?
    ///
    /// SolidWorks prompts about hidden and suppressed components when you
    /// export an assembly interactively, but swSaveAsOptions_Silent suppresses
    /// that prompt and there is no preference behind it: the STEP options page
    /// (swconst/FileSaveAsSTEPOptions.htm) lists every STEP setting the API
    /// exposes and hidden components are not among them. So the behaviour has
    /// to be measured rather than configured.
    ///
    /// The model is never saved: visibility is restored and the document is
    /// closed without saving.
    /// </summary>
    public static class HiddenProbe
    {
        public static int Run(string corpus, string outDir, bool visible, string only)
        {
            Directory.CreateDirectory(outDir);
            using (var sw = SwSession.Connect(visible))
            {
                foreach (var asm in Directory.GetFiles(corpus, "*.SLDASM")
                                             .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                                             .Where(f => only == null ||
                                                    Path.GetFileName(f).IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Probe(sw, asm, outDir);
                }
            }
            return 0;
        }

        private static void Probe(SwSession sw, string asmPath, string outDir)
        {
            string name = Path.GetFileNameWithoutExtension(asmPath);
            Log.Info($"--- {name} ---");

            string error;
            var model = sw.Open(asmPath, out error);
            if (model == null) { Log.Info("  SKIP could not open: " + error); return; }

            var assy = model as IAssemblyDoc;
            if (assy == null) { Log.Info("  SKIP not an assembly"); return; }

            var comps = (assy.GetComponents(false) as object[] ?? new object[0])
                        .OfType<IComponent2>().ToList();
            Log.Info($"  {comps.Count} component(s)");

            sw.App.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214);

            // Hide the last component and export under both appearance
            // settings. The add-in turns swStepExportAppearances on and a
            // plain export leaves it off, so that toggle is the one difference
            // between what this probe measured first and what the add-in does.
            var target = comps.LastOrDefault();
            if (target == null) { sw.Close(model); return; }

            int wasVisible = target.Visible;
            bool savedAppearances = false;
            try { savedAppearances = sw.App.GetUserPreferenceToggle(SwSession.SwStepExportAppearances); }
            catch { }

            try
            {
                foreach (bool appearances in new[] { false, true })
                {
                    try { sw.App.SetUserPreferenceToggle(SwSession.SwStepExportAppearances, appearances); }
                    catch { Log.Info("  swStepExportAppearances unavailable"); }

                    target.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
                    string vis = Path.Combine(outDir, $"{name}__vis_app{(appearances ? 1 : 0)}.step");
                    Save(model, vis);
                    Report($"visible  app={appearances}", vis);

                    target.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                    string hid = Path.Combine(outDir, $"{name}__hid_app{(appearances ? 1 : 0)}.step");
                    Save(model, hid);
                    Report($"hid {target.Name2} app={appearances}", hid);

                    // The native export is only half the story. Run the
                    // add-in's own pipeline over that file too: the appearance
                    // ladder walks every component including the hidden one,
                    // and what the matcher then does with a component that has
                    // no occurrence is the thing worth measuring.
                    if (appearances)
                    {
                        RunAddInPipeline(model, hid, outDir, name);
                        RunIncludeHidden(model, outDir, name, target, sw);
                    }
                }
            }
            finally
            {
                // Restore before closing, and close without saving, so the
                // corpus model on disk is never modified by the probe.
                try { target.Visible = wasVisible; } catch (Exception ex) { Log.Info("  restore failed: " + ex.Message); }
                try { sw.App.SetUserPreferenceToggle(SwSession.SwStepExportAppearances, savedAppearances); } catch { }
                sw.Close(model);
            }
        }

        /// <summary>The add-in's post-process, run over a file that has a
        /// hidden component missing from it.</summary>
        private static void RunAddInPipeline(IModelDoc2 model, string nativePath,
                                             string outDir, string name)
        {
            string fixedPath = Path.Combine(outDir, name + "__hid_peak.step");
            File.Copy(nativePath, fixedPath, true);

            var rw = new StepRewriter(fixedPath, m => Log.Info("      " + m));
            var occurrences = AppearanceLadder.Resolve(model, null);
            var refs = rw.FindOccurrences();
            Log.Info($"    ladder saw {occurrences.Count} component(s); " +
                     $"file has {refs.Count} occurrence(s)");

            var pairs = new OccurrenceMatcher(model, m => Log.Info("      " + m))
                        .Match(occurrences, rw);
            foreach (var p in pairs)
                Log.Info($"      {p.Key.Path,-20} -> " +
                         (p.Value == null ? "UNMATCHED" : "NAUO #" + p.Value.NauoId));

            rw.ApplyOccurrenceColours(pairs, deInstance: true);
            rw.Save(fixedPath);
            Report("addin deinst", fixedPath);
        }

        /// <summary>
        /// The "Include hidden components" option: reveal, export, re-hide.
        /// Checks both that the component comes back AND that the model is
        /// left exactly as it was found.
        /// </summary>
        private static void RunIncludeHidden(IModelDoc2 model, string outDir, string name,
                                             IComponent2 target, SwSession sw)
        {
            int before = target.Visible;

            var assy = (IAssemblyDoc)model;
            var revealed = new List<IComponent2>();
            foreach (var o in assy.GetComponents(false) as object[] ?? new object[0])
            {
                var c = o as IComponent2;
                if (c == null) continue;
                if (c.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                if (c.Visible != (int)swComponentVisibilityState_e.swComponentHidden) continue;
                c.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
                revealed.Add(c);
            }
            Log.Info($"    revealed {revealed.Count} hidden component(s)");

            string p = Path.Combine(outDir, name + "__hid_included.step");
            Save(model, p);
            Report("incl hidden", p);

            foreach (var c in revealed) c.Visible = (int)swComponentVisibilityState_e.swComponentHidden;

            int after = target.Visible;
            Log.Info(before == after
                ? $"    visibility restored ({before})"
                : $"    RESTORE FAILED: was {before}, now {after}");
        }

        private static void Save(IModelDoc2 model, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            int errors = 0, warnings = 0;
            model.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                ref errors, ref warnings);
            if (errors != 0) Log.Info($"  SaveAs3 errors={errors} warnings={warnings}");
        }

        private static void Report(string label, string path)
        {
            if (!File.Exists(path)) { Log.Info($"  {label,-12}: NOT WRITTEN"); return; }

            var step = new Part21(path);
            int solids = step.ByType("MANIFOLD_SOLID_BREP").Count;
            int nauo = step.ByType("NEXT_ASSEMBLY_USAGE_OCCURRENCE").Count;
            int products = step.ByType("PRODUCT").Count;
            var names = step.ByType("PRODUCT").Select(p => step.NameOf(p)).ToList();
            long bytes = new FileInfo(path).Length;

            Log.Info($"  {label,-12}: solids={solids} occurrences={nauo} products={products} " +
                     $"bytes={bytes} [{string.Join(", ", names)}]");
        }
    }
}
