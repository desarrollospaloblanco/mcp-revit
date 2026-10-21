using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Replaces the outer boundary of a floor's sketch with a new polygon in metres, keeping the
    /// floor itself (id, type, parameters, hosted elements) and, by default, its holes. Uses a sketch
    /// edit scope, which needs Revit 2022 or later and must run outside other transactions.
    /// </summary>
    public class ReplaceFloorOutlineCommand : DelegateCommand
    {
        public override string CommandName => "replace_floor_outline";

        public ReplaceFloorOutlineCommand(UIApplication uiApp) : base(uiApp, "MCP: contorno de piso")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            long floorId = parameters?["floorId"]?.Value<long>() ?? throw new ArgumentException("floorId is required");
            JArray outline = parameters?["outline"] as JArray ?? throw new ArgumentException("outline [[x, y], ...] in metres is required");
            var holes = (parameters?["holes"] as JArray)?.OfType<JArray>().ToList();
            bool keepHoles = parameters?["keepHoles"]?.Value<bool>() ?? holes == null;

            return RunInRevit(app =>
            {
#if REVIT2022_OR_GREATER
                Document doc = options.Document(app);
                Floor floor = doc.GetElement(Utils.ElementIdExtensions.ToElementId(floorId)) as Floor
                              ?? throw new InvalidOperationException("Element " + floorId + " is not a floor");
                var sketch = (Sketch)doc.GetElement(floor.SketchId);
                double areaBefore = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                var collector = new WarningCollector(options.SwallowWarnings);

                var scope = new SketchEditScope(doc, "MCP: contorno de piso " + floorId);
                scope.Start(sketch.Id);
                try
                {
                    using (var t = new Transaction(doc, "MCP: contorno de piso"))
                    {
                        t.Start();
                        double z = sketch.SketchPlane.GetPlane().Origin.Z;
                        var curveIds = sketch.GetAllElements().ToList();
                        int outerIndex = OuterLoop(sketch.Profile);
                        // Only boundary curves go: the outer loop, and the holes when they are replaced.
                        // Anything else in the sketch (a slope arrow) stays.
                        var drop = new HashSet<ElementId>();
                        for (int i = 0; i < sketch.Profile.Size; i++)
                        {
                            if (i != outerIndex && keepHoles) continue;
                            foreach (Curve c in sketch.Profile.get_Item(i))
                                foreach (ElementId id in curveIds)
                                    if (doc.GetElement(id) is CurveElement ce && Same(ce.GeometryCurve, c)) drop.Add(id);
                        }
                        doc.Delete(drop.ToList());
                        foreach (Curve c in Metric.Loop(outline, z)) doc.Create.NewModelCurve(c, sketch.SketchPlane);
                        foreach (JArray h in holes ?? new List<JArray>())
                            foreach (Curve c in Metric.Loop(h, z)) doc.Create.NewModelCurve(c, sketch.SketchPlane);
                        t.Commit();
                    }
                    scope.Commit(collector);
                }
                catch
                {
                    if (scope.IsActive) scope.Cancel();
                    throw;
                }
                double areaAfter = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                return new
                {
                    document = doc.Title,
                    floorId,
                    keptHoles = keepHoles,
                    areaBeforeM2 = Math.Round(areaBefore * Metric.SquareMetersPerSquareFoot, 2),
                    areaAfterM2 = Math.Round(areaAfter * Metric.SquareMetersPerSquareFoot, 2),
                    warnings = collector.Report()
                };
#else
                throw new NotSupportedException("Editing a floor sketch through the API needs Revit 2022 or later");
#endif
            }, 300);
        }

#if REVIT2022_OR_GREATER
        private static int OuterLoop(CurveArrArray profile)
        {
            int best = 0;
            double bestArea = -1;
            for (int i = 0; i < profile.Size; i++)
            {
                double a = 0;
                foreach (Curve c in profile.get_Item(i))
                {
                    XYZ p = c.GetEndPoint(0), q = c.GetEndPoint(1);
                    a += p.X * q.Y - q.X * p.Y;
                }
                if (Math.Abs(a) > bestArea) { bestArea = Math.Abs(a); best = i; }
            }
            return best;
        }

        private static bool Same(Curve a, Curve b)
        {
            XYZ a0 = a.GetEndPoint(0), a1 = a.GetEndPoint(1), b0 = b.GetEndPoint(0), b1 = b.GetEndPoint(1);
            return (a0.IsAlmostEqualTo(b0) && a1.IsAlmostEqualTo(b1)) || (a0.IsAlmostEqualTo(b1) && a1.IsAlmostEqualTo(b0));
        }
#endif
    }
}
