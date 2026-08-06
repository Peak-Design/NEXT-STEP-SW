using Peak.NextStep.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Peak.NextStep
{
    /// <summary>
    /// The export command: SolidWorks writes the STEP it is good at, then we
    /// add the appearance it loses.
    ///
    /// SolidWorks exports per-face and per-body colour correctly (measured --
    /// see FINDINGS.md section 2.2), so that styling is left untouched. What it
    /// flattens is component- and assembly-level overrides: every occurrence of
    /// a shared part is written against one shape representation carrying one
    /// styled item, so all but one occurrence come out the wrong colour. That
    /// is what this repairs.
    /// </summary>
    public static class ExportCommand
    {
        public static void Run(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            if (model == null)
            {
                app.SendMsgToUser2("Open a part or assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            string suggested = Path.ChangeExtension(model.GetPathName(), ".step");
            if (string.IsNullOrEmpty(model.GetPathName()))
            {
                app.SendMsgToUser2("Save the document before exporting.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            bool deInstance, includeMaterial;
            if (!ExportOptionsDialog.Show(out deInstance, out includeMaterial)) return;

            string target;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export STEP with appearances",
                Filter = "STEP files (*.step;*.stp)|*.step;*.stp",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested),
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                target = dlg.FileName;
            }

            try
            {
                var report = Export(app, model, target, deInstance, includeMaterial);
                app.SendMsgToUser2(report, (int)swMessageBoxIcon_e.swMbInformation,
                                   (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("export failed: " + ex);
                app.SendMsgToUser2("Export failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public static string Export(ISldWorks app, IModelDoc2 model, string target,
                                    bool deInstance, bool includeMaterial)
        {
            // ── 1. SolidWorks' own export ───────────────────────────────────
            // These are application-wide preferences, so they are saved and
            // restored around the export -- the user's own settings must
            // survive an export run.
            int savedAp = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP);
            bool savedAppearances = false, appearancesAvailable = true;
            try { savedAppearances = app.GetUserPreferenceToggle(StepPrefs.ExportAppearances); }
            catch { appearancesAvailable = false; }

            bool ok;
            int errors = 0, warnings = 0;
            try
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214);
                if (appearancesAvailable)
                    app.SetUserPreferenceToggle(StepPrefs.ExportAppearances, true);

                ok = model.Extension.SaveAs3(
                    target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                    ref errors, ref warnings);
            }
            finally
            {
                try { app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, savedAp); }
                catch (Exception ex) { AddIn.Log("restore swStepAP: " + ex.Message); }
                if (appearancesAvailable)
                {
                    try { app.SetUserPreferenceToggle(StepPrefs.ExportAppearances, savedAppearances); }
                    catch (Exception ex) { AddIn.Log("restore appearances: " + ex.Message); }
                }
            }

            if (!ok)
                throw new InvalidOperationException(
                    $"SolidWorks STEP export failed (errors={errors}, warnings={warnings})");

            // ── 2. Repair what SolidWorks lost ──────────────────────────────
            var rw = new StepRewriter(target, AddIn.Log);
            var notes = new List<string>();
            int applied = 0, unmatched = 0;

            // Occurrence appearance only applies to assemblies: a part's own
            // face/body/feature styling is already written correctly.
            if (model is IAssemblyDoc)
            {
                var occurrences = AppearanceLadder.Resolve(model, AddIn.Log);
                int needFixing = occurrences.Count(o => o.OverridesPartInternals);
                if (needFixing == 0)
                {
                    notes.Add($"{occurrences.Count} component(s), none carrying a component "
                            + "or assembly level override, so appearance needed no repair.");
                }
                else
                {
                    var pairs = new OccurrenceMatcher(model, AddIn.Log)
                        .Match(occurrences, rw.FindOccurrences());
                    unmatched = pairs.Count(p => p.Key.OverridesPartInternals && p.Value == null);
                    applied = rw.ApplyOccurrenceColours(pairs, deInstance);
                    notes.Add($"{occurrences.Count} component(s); {applied} occurrence "
                            + "appearance(s) restored that SolidWorks' own export drops "
                            + (deInstance ? "(de-instanced)." : "(instancing preserved)."));
                }
            }
            else
            {
                notes.Add("Part document: SolidWorks' own per-face appearance export is "
                        + "already correct, so appearance needed no repair.");
            }

            // ── 3. Engineering material, which SolidWorks never exports ─────
            if (includeMaterial)
            {
                var materials = MaterialHarvester.Harvest(model, AddIn.Log);

                // With de-instancing on, each appearance bucket gets its own
                // numbered variant of the material name. STEP cannot associate
                // a material with an appearance, so a consumer that builds one
                // material per material name would otherwise merge every
                // differently-coloured copy of a part back into one and lose
                // the colours. Instancing preserved means one product per part
                // and nothing to distinguish, so the names stay untouched.
                int withMaterial = new MaterialWriter(rw.Document, AddIn.Log)
                    .Apply(materials, deInstance ? rw.BucketIndexByProduct : null);

                notes.Add(withMaterial > 0
                    ? $"Engineering material written for {withMaterial} part(s)."
                    : "No engineering material assigned, so none was written.");
            }

            rw.Save(target);

            string msg = $"Exported {Path.GetFileName(target)}.\n\n" + string.Join("\n", notes);
            if (unmatched > 0)
                msg += $"\n\nWARNING: {unmatched} occurrence(s) could not be matched to the "
                     + "STEP file and kept SolidWorks' colour. See nextstep-debug.log.";
            return msg;
        }
    }

    internal static class StepPrefs
    {
        /// <summary>
        /// swStepExportAppearances = 787. Present from SW2024's swconst and
        /// ABSENT from SW2022's, which this add-in compiles against, so it is
        /// used as a raw preference id. Reads are guarded: on SW2022 the
        /// preference simply does not exist.
        /// </summary>
        public const int ExportAppearances = 787;
    }
}
