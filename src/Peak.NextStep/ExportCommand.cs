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
    /// The export command. SolidWorks writes the STEP file it is good at. This
    /// class then adds the appearance that SolidWorks loses.
    ///
    /// SolidWorks exports the colour of each face and each body correctly. This
    /// was measured. See FINDINGS.md section 2.2. This class therefore does not
    /// change that styling.
    ///
    /// SolidWorks flattens the overrides at component and assembly level. It
    /// writes every occurrence of a shared part against one shape representation
    /// that holds one styled item. All the occurrences except one then get the
    /// wrong colour. This class repairs that.
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

            // Counted before the dialog opens, because the dialog cannot
            // offer "only the selected ones" when nothing is selected - the
            // option would silently export an empty file.
            int selected = Core.Selection.Count(model);

            bool deInstance, includeMaterial, includeHidden, onlySelected;
            if (!ExportOptionsDialog.Show(selected, out deInstance, out includeMaterial,
                                          out includeHidden, out onlySelected))
                return;

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
                var report = Export(app, model, target, deInstance, includeMaterial,
                                    includeHidden, onlySelected);
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
                                    bool deInstance, bool includeMaterial,
                                    bool includeHidden = false,
                                    bool onlySelected = false)
        {
            // Null means no restriction. Selection.KeepSet returns null when
            // nothing is selected, so an empty selection exports everything
            // rather than nothing.
            var keep = onlySelected ? Core.Selection.KeepSet(model, AddIn.Log) : null;

            // ── 1. The SolidWorks export ────────────────────────────────────
            // These preferences apply to the whole application. This code saves
            // them before the export and puts them back afterwards. The
            // settings of the user must survive an export.
            int savedAp = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP);
            bool savedAppearances = false, appearancesAvailable = true;
            try { savedAppearances = app.GetUserPreferenceToggle(StepPrefs.ExportAppearances); }
            catch { appearancesAvailable = false; }

            bool ok;
            int errors = 0, warnings = 0;
            var restore = new List<KeyValuePair<IComponent2, int>>();
            try
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214);
                if (appearancesAvailable)
                    app.SetUserPreferenceToggle(StepPrefs.ExportAppearances, true);

                restore.AddRange(ApplyVisibility(model, includeHidden, keep));

                ok = model.Extension.SaveAs3(
                    target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                    ref errors, ref warnings);
            }
            finally
            {
                RestoreVisibility(restore);
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

            // Occurrence appearance applies only to assemblies. SolidWorks
            // already writes the face, body and feature styling of a part
            // correctly.
            if (model is IAssemblyDoc)
            {
                // The ladder knows which components the export revealed: with
                // includeHidden, a hidden component counts as exported, and only
                // suppression and envelopes still exclude.
                var occurrences = AppearanceLadder.Resolve(model, AddIn.Log, includeHidden, keep);

                int unselected = occurrences.Count(
                    o => !o.Exported && o.ExcludedBecause == AppearanceLadder.NotSelected);
                int skipped = occurrences.Count(o => !o.Exported) - unselected;
                if (skipped > 0)
                    notes.Add($"{skipped} component(s) hidden or suppressed, and not exported.");
                if (unselected > 0)
                    notes.Add($"{unselected} component(s) outside the selection, "
                            + "and not exported.");

                int needFixing = occurrences.Count(o => o.Exported && o.OverridesPartInternals);
                if (needFixing == 0)
                {
                    notes.Add($"{occurrences.Count} component(s). None carries an "
                            + "override, so no appearance needed repair.");
                }
                else
                {
                    rw.FindOccurrences();
                    var pairs = new OccurrenceMatcher(model, AddIn.Log)
                        .Match(occurrences, rw);
                    unmatched = pairs.Count(p => p.Key.OverridesPartInternals && p.Value == null);
                    applied = rw.ApplyOccurrenceColours(pairs, deInstance);
                    notes.Add($"{occurrences.Count} component(s), {needFixing} with an "
                            + $"override. Restored {applied} appearance(s) "
                            + (deInstance ? "(de-instanced)." : "(instancing kept)."));
                }
            }
            else
            {
                notes.Add("Part document. No appearance needed repair.");
            }

            // ── 3. Engineering material, which SolidWorks never exports ─────
            if (includeMaterial)
            {
                var materials = MaterialHarvester.Harvest(model, AddIn.Log);

                // With de-instancing on, each appearance group gets its own
                // numbered variant of the material name. STEP cannot relate a
                // material to an appearance. A consumer that builds one material
                // for each material name would otherwise merge every copy of a
                // part into one material and lose the colours. With instancing
                // kept there is one product for each part and nothing to tell
                // apart, so the names do not change.
                int withMaterial = new MaterialWriter(rw.Document, AddIn.Log)
                    .Apply(materials, deInstance ? rw.BucketIndexByProduct : null);

                notes.Add(withMaterial > 0
                    ? $"Wrote the engineering material for {withMaterial} part(s)."
                    : "No part has an engineering material.");
            }

            rw.Save(target);

            string msg = $"Exported {Path.GetFileName(target)}.\n\n" + string.Join("\n", notes);
            if (unmatched > 0)
                msg += $"\n\nWARNING: {unmatched} occurrence(s) could not be matched, and "
                     + "keep the SolidWorks colour. See nextstep-debug.log.";
            return msg;
        }

        /// <summary>
        /// Puts every component into the visibility the export needs, and
        /// returns what it changed so that the caller can put it back.
        ///
        /// One pass, not two. Revealing hidden components and hiding the ones
        /// outside a selection both act on the same property, and a component
        /// that is hidden AND unselected is touched by both - run as separate
        /// passes with separate undo lists, the order the two restores happen
        /// in decides whether the user gets their assembly back. Deciding the
        /// wanted state once, and recording only what actually changed, has no
        /// such ordering.
        ///
        /// A silent SaveAs3 leaves out the hidden components, and the API has no
        /// preference to change this. The STEP options page lists every setting
        /// the API gives, and this is not one of them. SolidWorks asks the user
        /// instead, and swSaveAsOptions_Silent answers the question. A change of
        /// visibility is how this code gives a different answer.
        ///
        /// This code changes visibility, not suppression, on purpose. To resolve
        /// a suppressed component, SolidWorks rebuilds the assembly, which can
        /// disturb mates and in-context features. To show a hidden component
        /// changes only the display, and reverses exactly. NEXT-STEP therefore
        /// never exports suppressed components. The dialog says so, instead of
        /// working around it.
        /// </summary>
        private static List<KeyValuePair<IComponent2, int>> ApplyVisibility(
            IModelDoc2 model, bool includeHidden, HashSet<string> keep)
        {
            var changed = new List<KeyValuePair<IComponent2, int>>();
            if (!(model is IAssemblyDoc assy)) return changed;
            if (!includeHidden && keep == null) return changed;

            int hiddenState = (int)swComponentVisibilityState_e.swComponentHidden;
            int visibleState = (int)swComponentVisibilityState_e.swComponentVisible;
            int revealed = 0, trimmed = 0;

            foreach (var o in assy.GetComponents(false) as object[] ?? new object[0])
            {
                var comp = o as IComponent2;
                if (comp == null) continue;
                try
                {
                    // A suppressed component has no geometry to write, so
                    // neither showing nor hiding it achieves anything.
                    if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                        continue;

                    int was = comp.Visible;
                    int want = was;
                    if (keep != null && !keep.Contains(comp.Name2 ?? ""))
                        want = hiddenState;
                    else if (includeHidden)
                        want = visibleState;

                    if (want == was) continue;
                    comp.Visible = want;
                    changed.Add(new KeyValuePair<IComponent2, int>(comp, was));
                    if (want == visibleState) revealed++; else trimmed++;
                }
                catch (Exception ex) { AddIn.Log($"visibility {comp.Name2}: {ex.Message}"); }
            }

            if (revealed > 0)
                AddIn.Log($"    revealed {revealed} hidden component(s) for the export");
            if (trimmed > 0)
                AddIn.Log($"    hid {trimmed} component(s) outside the selection");
            return changed;
        }

        /// <summary>
        /// Puts back every visibility ApplyVisibility changed. This runs in a
        /// finally block. To leave an assembly showing components the user had
        /// hidden - or missing the ones the export trimmed - is a worse defect
        /// than any this add-in repairs.
        /// </summary>
        private static void RestoreVisibility(
            List<KeyValuePair<IComponent2, int>> changed)
        {
            foreach (var entry in changed)
            {
                try { entry.Key.Visible = entry.Value; }
                catch (Exception ex) { AddIn.Log($"restore visibility: {ex.Message}"); }
            }
        }
    }

    internal static class StepPrefs
    {
        /// <summary>
        /// swStepExportAppearances = 787. The swconst of SW2024 has it. The
        /// swconst of SW2022 does not, and this add-in compiles against SW2022.
        /// This code therefore uses the raw preference id. Every read has a
        /// guard, because on SW2022 the preference does not exist.
        /// </summary>
        public const int ExportAppearances = 787;
    }
}
