using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.NextStep.Core;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// Tests the shared-definition split on a real STEP file, without
    /// SolidWorks. The probe mirrors the occurrence tree of the file itself,
    /// paints every leaf under ONE use of the most-shared sub-assembly red,
    /// and runs the ordinary colour pass. The divergence forces a structure
    /// split, and the output can be verified entity by entity.
    /// </summary>
    public static class SplitProbe
    {
        public static int Run(string file, string outDir)
        {
            if (!File.Exists(file)) { Log.Info($"no such file: {file}"); return 2; }
            string target = Path.Combine(outDir,
                Path.GetFileNameWithoutExtension(file) + "__split.step");
            File.Copy(file, target, true);

            var rw = new StepRewriter(target, m => Log.Info(m));
            var refs = rw.FindOccurrences();
            Log.Info($"{refs.Count} occurrence(s), root pd #{rw.RootPd}");

            // A synthetic component tree that mirrors the file exactly. Uses of
            // a shared definition share the occurrence records, exactly as the
            // matcher produces them.
            var pairs = new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>();
            var roots = new List<OccurrenceAppearance>();
            BuildKids(rw, rw.RootPd, null, "", pairs, roots);
            Log.Info($"mirror tree: {pairs.Count} node(s)");

            // The most-used sub-assembly definition is the target.
            var defUse = pairs
                .Where(p => rw.ChildrenByParentPd.ContainsKey(p.Value.ChildPd))
                .GroupBy(p => p.Value.ChildPd)
                .Where(g => g.Select(p => p.Key).Count() > 1)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (defUse == null)
            {
                Log.Info("the file has no shared sub-assembly definition; nothing to probe");
                return 1;
            }

            var uses = defUse.Select(p => p.Key).ToList();
            var first = uses[0];
            Log.Info($"target definition: '{defUse.First().Value.ProductName}' used {uses.Count}x");
            Log.Info($"painting every leaf under {first.Path} red; the other "
                   + $"{uses.Count - 1} use(s) stay original");

            int painted = Paint(first);
            Log.Info($"{painted} leaf occurrence(s) painted");

            int applied = rw.ApplyOccurrenceColours(pairs, deInstance: true);
            rw.Save(target);

            Log.Info("");
            Log.Info("== SUMMARY ==");
            Log.Info($"  appearances applied: {applied} (expected {painted})");
            Log.Info($"  output: {target}");
            return applied == painted ? 0 : 1;
        }

        private static void BuildKids(StepRewriter rw, int parentPd, OccurrenceAppearance parent,
            string prefix,
            List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> pairs,
            List<OccurrenceAppearance> roots)
        {
            if (!rw.ChildrenByParentPd.TryGetValue(parentPd, out var kids)) return;
            int i = 0;
            foreach (var r in kids)
            {
                var n = new OccurrenceAppearance
                {
                    Path = prefix + (r.ProductName ?? "?") + "-" + (++i),
                    Parent = parent,
                    Exported = true,
                };
                n.ComponentName = n.Path;
                parent?.Children.Add(n);
                if (parent == null) roots.Add(n);
                pairs.Add(new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(n, r));
                BuildKids(rw, r.ChildPd, n, n.Path + "/", pairs, roots);
            }
        }

        private static int Paint(OccurrenceAppearance n)
        {
            if (n.Children.Count == 0)
            {
                n.OverridesPartInternals = true;
                n.Colour = new Rgb(1, 0, 0);
                n.Transparency = 0;
                return 1;
            }
            int painted = 0;
            foreach (var c in n.Children) painted += Paint(c);
            return painted;
        }
    }
}
