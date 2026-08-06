using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// S0 -- baseline. Two questions:
    ///   1. Entity-for-entity, what does SolidWorks' own STEP export produce,
    ///      across the full matrix of AP and appearance-related options?
    ///   2. Is PublishSTEP242File genuinely licence-blocked on this machine?
    ///
    /// This spike produces the files; tools/stepdump.py counts what is in them.
    /// Keeping the two apart matters: the counting must not be done by the
    /// thing that produced the files.
    /// </summary>
    public static class S0Baseline
    {
        private sealed class Variant
        {
            public string Id;
            public int Ap;
            public bool? Appearances;
            public bool? FaceEdgeProps;
            public bool? SplitPeriodic;
            public bool? ConfigData;
        }

        private static readonly Variant[] Variants =
        {
            new Variant { Id = "ap203",           Ap = 203 },
            new Variant { Id = "ap214_noappear",  Ap = 214, Appearances = false },
            new Variant { Id = "ap214_appear",    Ap = 214, Appearances = true },
            new Variant { Id = "ap214_appear_fe", Ap = 214, Appearances = true, FaceEdgeProps = true },
            new Variant { Id = "ap214_split",     Ap = 214, Appearances = true, SplitPeriodic = true },
            new Variant { Id = "ap214_nosplit",   Ap = 214, Appearances = true, SplitPeriodic = false },
            new Variant { Id = "ap214_cfg",       Ap = 214, Appearances = true, ConfigData = true },
        };

        /// <summary>
        /// Just the licence probe, without re-running the 63-file export
        /// matrix. Used after correcting the probe's file extension.
        /// </summary>
        public static int Run242Only(string corpusDir, string outDir, bool visible)
        {
            Directory.CreateDirectory(outDir);
            var models = new List<string>();
            foreach (var pattern in new[] { "*.SLDPRT", "*.SLDASM" })
                models.AddRange(Directory.GetFiles(corpusDir, pattern));
            models.Sort(StringComparer.OrdinalIgnoreCase);

            var json = new StringBuilder();
            json.AppendLine("[");
            bool first = true;

            using (var sw = SwSession.Connect(visible))
            {
                foreach (var path in models)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    string openError;
                    var model = sw.Open(path, out openError);
                    if (model == null) { Log.Info($"{name}: SKIP {openError}"); continue; }
                    Log.Info($"--- {name} ---");
                    try { Probe242(model, name, outDir, json, ref first); }
                    finally { sw.Close(model); }
                }
            }

            json.AppendLine("]");
            string reportPath = Path.Combine(outDir, "s0-ap242-probe.json");
            File.WriteAllText(reportPath, json.ToString());
            Log.Info($"wrote {reportPath}");
            return 0;
        }

        public static int Run(string corpusDir, string outDir, bool visible)
        {
            Directory.CreateDirectory(outDir);

            var models = new List<string>();
            foreach (var pattern in new[] { "*.SLDPRT", "*.SLDASM" })
                models.AddRange(Directory.GetFiles(corpusDir, pattern));
            models.Sort(StringComparer.OrdinalIgnoreCase);

            if (models.Count == 0)
            {
                Log.Info($"no models found in {corpusDir}");
                return 2;
            }
            Log.Info($"{models.Count} corpus models, {Variants.Length} variants each");

            var json = new StringBuilder();
            json.AppendLine("[");
            bool firstRecord = true;

            using (var sw = SwSession.Connect(visible))
            {
                // Probe once, up front: does this SolidWorks even expose the
                // appearance toggle? SW2022 has no such preference.
                bool appearanceToggleExists =
                    sw.TrySetToggle(SwSession.SwStepExportAppearances, true,
                                    "swStepExportAppearances");
                Log.Info($"swStepExportAppearances available: {appearanceToggleExists}");

                foreach (var path in models)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    Log.Info($"--- {name} ---");

                    string openError;
                    var model = sw.Open(path, out openError);
                    if (model == null)
                    {
                        Log.Info($"  SKIP: {openError}");
                        AppendRecord(json, ref firstRecord, new Dictionary<string, string>
                        {
                            ["model"] = name,
                            ["error"] = openError ?? "open failed",
                        });
                        continue;
                    }

                    try
                    {
                        foreach (var v in Variants)
                            RunVariant(sw, model, name, v, outDir, json, ref firstRecord,
                                       appearanceToggleExists);

                        Probe242(model, name, outDir, json, ref firstRecord);
                    }
                    finally
                    {
                        sw.Close(model);
                    }
                }
            }

            json.AppendLine("]");
            string reportPath = Path.Combine(outDir, "s0-exports.json");
            File.WriteAllText(reportPath, json.ToString());
            Log.Info($"wrote {reportPath}");
            return 0;
        }

        private static void RunVariant(SwSession sw, IModelDoc2 model, string name,
                                       Variant v, string outDir, StringBuilder json,
                                       ref bool first, bool appearanceToggleExists)
        {
            var applied = new List<string>();
            sw.SetInt(swUserPreferenceIntegerValue_e.swStepAP, v.Ap);
            applied.Add("ap=" + v.Ap);

            if (v.Appearances.HasValue && appearanceToggleExists)
            {
                if (sw.TrySetToggle(SwSession.SwStepExportAppearances,
                                    v.Appearances.Value, "swStepExportAppearances"))
                    applied.Add("appearances=" + v.Appearances.Value);
            }
            if (v.FaceEdgeProps.HasValue
                && sw.TrySetToggle(swUserPreferenceToggle_e.swStepExportFaceEdgeProps,
                                   v.FaceEdgeProps.Value))
                applied.Add("faceEdgeProps=" + v.FaceEdgeProps.Value);

            if (v.SplitPeriodic.HasValue
                && sw.TrySetToggle(swUserPreferenceToggle_e.swStepExportSplitPeriodic,
                                   v.SplitPeriodic.Value))
                applied.Add("splitPeriodic=" + v.SplitPeriodic.Value);

            if (v.ConfigData.HasValue
                && sw.TrySetToggle(swUserPreferenceToggle_e.swStepExportConfigurationData,
                                   v.ConfigData.Value))
                applied.Add("configData=" + v.ConfigData.Value);

            string target = Path.Combine(outDir, $"{name}__{v.Id}.step");
            int errors = 0, warnings = 0;
            bool ok = false;
            string failure = null;
            try
            {
                ok = model.Extension.SaveAs3(
                    target,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, null, ref errors, ref warnings);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            long size = File.Exists(target) ? new FileInfo(target).Length : 0;
            Log.Info($"  {v.Id,-16} ok={ok} errors={errors} warnings={warnings} " +
                     $"bytes={size}{(failure != null ? " EX:" + failure : "")}");

            AppendRecord(json, ref first, new Dictionary<string, string>
            {
                ["model"] = name,
                ["variant"] = v.Id,
                ["settings"] = string.Join(";", applied),
                ["ok"] = ok.ToString().ToLowerInvariant(),
                ["saveAsErrors"] = errors.ToString(CultureInfo.InvariantCulture),
                ["saveAsWarnings"] = warnings.ToString(CultureInfo.InvariantCulture),
                ["bytes"] = size.ToString(CultureInfo.InvariantCulture),
                ["file"] = Path.GetFileName(target),
                ["exception"] = failure,
            });
        }

        /// <summary>
        /// The premise check. swStep242Error_e: 0 Success, 1 InvalidPath,
        /// 3 UnknownError, 4 MBDLicenseNotAvailable. The literal integer is
        /// recorded rather than a screenshot, because this single value is
        /// what the "AP242 is locked behind MBD" claim rests on.
        /// </summary>
        public static void Probe242(IModelDoc2 model, string name, string outDir,
                                    StringBuilder json, ref bool first)
        {
            // A first attempt with a ".step" extension returned 1
            // (InvalidPath) for every model, which is a probe defect and not a
            // finding: the API help example uses ".STP". Try several forms and
            // report the best outcome, so "InvalidPath" can never be mistaken
            // for "licence blocked".
            var candidates = new List<string>
            {
                Path.Combine(outDir, name + "__ap242_publish.STP"),
                Path.Combine(outDir, name + "__ap242_publish.stp"),
                Path.Combine(Path.GetTempPath(), "peak242probe.STP"),
                Path.Combine(outDir, name + "__ap242_publish.step"),
            };

            int status = -999;
            string failure = null;
            string target = candidates[0];
            var attempts = new List<string>();

            foreach (var candidate in candidates)
            {
                int s = -999;
                string f = null;
                try { s = model.Extension.PublishSTEP242File(candidate); }
                catch (Exception ex) { f = ex.GetType().Name + ": " + ex.Message; }

                attempts.Add($"{Path.GetExtension(candidate)}@{Path.GetDirectoryName(candidate)}={s}");
                status = s;
                failure = f;
                target = candidate;

                // Anything other than InvalidPath is a real answer; stop.
                if (s != 1) break;
            }

            string meaning;
            switch (status)
            {
                case 0: meaning = "swPublishStep242_Success"; break;
                case 1: meaning = "swPublishStep242_InvalidPath"; break;
                case 3: meaning = "swPublishStep242_UnknownError"; break;
                case 4: meaning = "swPublishStep242_MBDLicenseNotAvailable"; break;
                default: meaning = failure != null ? "threw" : "undocumented value"; break;
            }

            long size = File.Exists(target) ? new FileInfo(target).Length : 0;
            Log.Info($"  PublishSTEP242File -> {status} ({meaning}) bytes={size} " +
                     $"[attempts: {string.Join(", ", attempts)}]");

            AppendRecord(json, ref first, new Dictionary<string, string>
            {
                ["model"] = name,
                ["variant"] = "ap242_publish",
                ["publishStep242Status"] = status.ToString(CultureInfo.InvariantCulture),
                ["publishStep242Meaning"] = meaning,
                ["attempts"] = string.Join(", ", attempts),
                ["bytes"] = size.ToString(CultureInfo.InvariantCulture),
                ["file"] = Path.GetFileName(target),
                ["exception"] = failure,
            });
        }

        // Small hand-rolled JSON writer: this exe deliberately has no NuGet
        // dependencies beyond the reference assemblies.
        private static void AppendRecord(StringBuilder json, ref bool first,
                                         Dictionary<string, string> fields)
        {
            if (!first) json.AppendLine(",");
            first = false;
            json.Append("  {");
            bool firstField = true;
            foreach (var kv in fields)
            {
                if (kv.Value == null) continue;
                if (!firstField) json.Append(", ");
                firstField = false;
                json.Append($"\"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\"");
            }
            json.Append("}");
        }

        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", " ").Replace("\n", " ");
    }
}
