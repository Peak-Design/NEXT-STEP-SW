using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Peak.NextStep.Core
{
    /// <summary>
    /// Matches the component occurrences of SolidWorks to the occurrences in the
    /// STEP output of SolidWorks.
    ///
    /// SolidWorks names them 'NAUO1', 'NAUO2' and so on, with no component name.
    /// The identity must therefore come from the placement transform. A
    /// component transform is in metres. The STEP file is in millimetres.
    ///
    /// A wrong match with no message is the failure that matters, because it
    /// gives a wrong colour that looks correct. This code therefore reports an
    /// occurrence that it cannot match, and leaves it alone. It does not guess.
    /// </summary>
    public sealed class OccurrenceMatcher
    {
        private const double ToleranceMm = 0.05;

        private readonly IModelDoc2 _model;
        private readonly Action<string> _log;

        public OccurrenceMatcher(IModelDoc2 model, Action<string> log)
        {
            _model = model;
            _log = log;
        }

        public List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> Match(
            List<OccurrenceAppearance> occurrences,
            List<StepRewriter.OccurrenceRef> refs)
        {
            var pairs = new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>();
            var assy = _model as IAssemblyDoc;
            if (assy == null) return pairs;

            var comps = (assy.GetComponents(false) as object[] ?? new object[0])
                .OfType<IComponent2>().ToList();
            var used = new HashSet<int>();

            // A component that SolidWorks did not write has no occurrence to
            // find. To count it here gives a warning that the mapping is not 1
            // to 1, on every export of an assembly with a hidden component. This
            // code would then report each one as unmatched. Those are two false
            // alarms for correct behaviour, and they teach the user to ignore a
            // real warning.
            var expected = occurrences.Where(o => o.Exported).ToList();
            int excluded = occurrences.Count - expected.Count;
            if (excluded > 0)
                _log?.Invoke($"  {excluded} component(s) hidden or suppressed, so absent from " +
                             "the file by design; not matched");

            if (refs.Count != expected.Count)
                _log?.Invoke($"  WARNING: {expected.Count} exported component(s) but {refs.Count} " +
                             "occurrence(s) in the STEP file; mapping is not 1:1");

            foreach (var occ in occurrences)
            {
                if (!occ.Exported) { pairs.Add(Pair(occ, null)); continue; }

                var comp = comps.FirstOrDefault(c => c.Name2 == occ.Path);
                double[] want = TranslationMm(comp);

                StepRewriter.OccurrenceRef best = null;
                double bestDist = double.MaxValue, secondDist = double.MaxValue;

                if (want != null)
                {
                    foreach (var r in refs)
                    {
                        if (used.Contains(r.NauoId) || r.Translation == null) continue;
                        double d = Distance(r.Translation, want);
                        if (d < bestDist) { secondDist = bestDist; bestDist = d; best = r; }
                        else if (d < secondDist) secondDist = d;
                    }
                }

                if (best != null && bestDist <= ToleranceMm)
                {
                    // Two occurrences at the same position make the match
                    // unclear. Report this instead of a choice.
                    if (secondDist <= ToleranceMm)
                        _log?.Invoke($"  WARNING: {occ.Path} is ambiguous -- two occurrences " +
                                     $"within {ToleranceMm} mm; taking NAUO #{best.NauoId}");
                    used.Add(best.NauoId);
                    pairs.Add(Pair(occ, best));
                    _log?.Invoke($"  matched {occ.Path} -> NAUO #{best.NauoId} ({bestDist:F4} mm)");
                }
                else
                {
                    pairs.Add(Pair(occ, null));
                    _log?.Invoke($"  UNMATCHED {occ.Path} (nearest occurrence " +
                                 $"{(bestDist == double.MaxValue ? -1 : bestDist):F3} mm away) -- " +
                                 "left with SolidWorks' colour");
                }
            }
            return pairs;
        }

        private static KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>
            Pair(OccurrenceAppearance a, StepRewriter.OccurrenceRef b)
            => new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(a, b);

        private static double Distance(double[] a, double[] b)
            => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0])
                       + (a[1] - b[1]) * (a[1] - b[1])
                       + (a[2] - b[2]) * (a[2] - b[2]));

        private static double[] TranslationMm(IComponent2 comp)
        {
            var t = comp?.Transform2?.ArrayData as double[];
            if (t == null || t.Length < 12) return null;
            return new[] { t[9] * 1000.0, t[10] * 1000.0, t[11] * 1000.0 };
        }
    }
}
