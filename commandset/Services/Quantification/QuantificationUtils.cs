using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Shared helpers for the quantification commands.
    ///
    /// Two rules are baked in here because getting them wrong is silent and expensive:
    ///
    /// 1. Never resolve the target document from the active UI document. The user switches
    ///    documents while commands are queued, so a command that reads the active document
    ///    can commit its edits to the wrong model. Every command takes an optional
    ///    documentTitle and resolves it against Application.Documents instead.
    ///
    /// 2. Never use ReferenceIntersector to find what sits above an element. It only sees
    ///    what is visible in the view it is given, so the answer changes when the user
    ///    changes the view or its section box. Use real solid booleans instead: they are
    ///    reproducible and, with a bounding-box prefilter, far faster.
    /// </summary>
    public static class QuantificationUtils
    {
        public const double FeetToMeters = 0.3048;
        public const double CubicFeetToCubicMeters = 0.0283168466;

        /// <summary>
        /// Finds the document to act on. When documentTitle is empty the active document is
        /// used; otherwise the open document whose Title matches exactly.
        /// </summary>
        public static Document ResolveDocument(UIApplication app, string documentTitle, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(documentTitle))
            {
                var active = app.ActiveUIDocument?.Document;
                if (active == null)
                    error = "There is no active document in Revit";
                return active;
            }

            foreach (Document candidate in app.Application.Documents)
            {
                if (candidate.IsFamilyDocument || candidate.IsLinked)
                    continue;
                if (string.Equals(candidate.Title, documentTitle, StringComparison.Ordinal))
                    return candidate;
            }

            var open = new List<string>();
            foreach (Document candidate in app.Application.Documents)
            {
                if (!candidate.IsFamilyDocument && !candidate.IsLinked)
                    open.Add(candidate.Title);
            }

            error = "No open document is titled '" + documentTitle + "'. Open documents: " +
                    (open.Count == 0 ? "(none)" : string.Join(", ", open.ToArray()));
            return null;
        }

        /// <summary>Geometry options used consistently across the commands.</summary>
        public static Options FineGeometry()
        {
            return new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };
        }

        /// <summary>Collects every solid of an element, recursing into geometry instances.</summary>
        public static List<Solid> GetSolids(Element element)
        {
            var solids = new List<Solid>();
            if (element == null)
                return solids;

            Collect(element.get_Geometry(FineGeometry()), solids);
            return solids;
        }

        private static void Collect(GeometryElement geometry, List<Solid> into)
        {
            if (geometry == null)
                return;

            foreach (GeometryObject obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null && solid.Volume > 1e-9)
                    into.Add(solid);

                var instance = obj as GeometryInstance;
                if (instance != null)
                    Collect(instance.GetInstanceGeometry(), into);
            }
        }

        /// <summary>Total solid volume in cubic feet.</summary>
        public static double GetVolume(Element element)
        {
            double total = 0;
            foreach (var solid in GetSolids(element))
                total += solid.Volume;
            return total;
        }

        /// <summary>Lowest Z of a solid, from its tessellated edges.</summary>
        public static double MinZ(Solid solid)
        {
            double min = double.MaxValue;
            foreach (Edge edge in solid.Edges)
            {
                foreach (XYZ point in edge.Tessellate())
                {
                    if (point.Z < min)
                        min = point.Z;
                }
            }
            return min;
        }

        /// <summary>Lowest Z across every solid of an element.</summary>
        public static double MinZ(IEnumerable<Solid> solids)
        {
            double min = double.MaxValue;
            foreach (var solid in solids)
            {
                double z = MinZ(solid);
                if (z < min)
                    min = z;
            }
            return min;
        }

        /// <summary>
        /// Every downward-facing planar face that sits at the element's true bottom.
        ///
        /// Returns a list rather than the single largest face on purpose: Revit splits the
        /// underside of a long beam into several faces where footings cross it, and taking
        /// only the biggest one silently drops most of the footprint.
        /// </summary>
        public static List<PlanarFace> GetBottomFaces(IEnumerable<Solid> solids, double tolerance = 0.05)
        {
            var faces = new List<PlanarFace>();
            double bottom = MinZ(solids);
            if (bottom == double.MaxValue)
                return faces;

            foreach (var solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    var planar = face as PlanarFace;
                    if (planar == null || planar.FaceNormal.Z > -0.9)
                        continue;
                    if (Math.Abs(planar.Origin.Z - bottom) > tolerance)
                        continue;
                    faces.Add(planar);
                }
            }

            return faces;
        }

        /// <summary>
        /// Projects a face's outer boundary onto a horizontal plane at the given elevation.
        /// Returns null when the boundary cannot be expressed as straight segments.
        /// </summary>
        public static CurveLoop FlattenBoundary(PlanarFace face, double elevation)
        {
            try
            {
                var loops = face.GetEdgesAsCurveLoops();
                if (loops == null || loops.Count == 0)
                    return null;

                var flat = new CurveLoop();
                foreach (Curve curve in loops[0])
                {
                    XYZ a = curve.GetEndPoint(0);
                    XYZ b = curve.GetEndPoint(1);
                    flat.Append(Line.CreateBound(new XYZ(a.X, a.Y, elevation), new XYZ(b.X, b.Y, elevation)));
                }
                return flat;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Underside of the lowest floor that sits above a footprint, or double.MaxValue when
        /// there is none.
        ///
        /// The prism is raised from the element's bottom, not its top, so a slab that is flush
        /// with or embedded in the element still counts. Candidates are prefiltered by bounding
        /// box and the search stops as soon as no remaining floor can be lower.
        /// </summary>
        public static double FindSlabUndersideAbove(
            Document doc,
            CurveLoop footprint,
            double fromElevation,
            BoundingBoxXYZ elementBox,
            IList<ElementId> floorIds,
            IList<BoundingBoxXYZ> floorBoxes)
        {
            Solid prism;
            try
            {
                var loops = new List<CurveLoop> { footprint };
                prism = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 250.0);
            }
            catch
            {
                return double.MaxValue;
            }

            var candidates = new List<int>();
            for (int i = 0; i < floorIds.Count; i++)
            {
                BoundingBoxXYZ box = floorBoxes[i];
                if (box.Max.Z <= fromElevation + 0.01)
                    continue;
                if (box.Max.X < elementBox.Min.X || box.Min.X > elementBox.Max.X)
                    continue;
                if (box.Max.Y < elementBox.Min.Y || box.Min.Y > elementBox.Max.Y)
                    continue;
                candidates.Add(i);
            }

            candidates.Sort(delegate (int a, int b) { return floorBoxes[a].Min.Z.CompareTo(floorBoxes[b].Min.Z); });

            double best = double.MaxValue;
            foreach (int index in candidates)
            {
                if (best <= floorBoxes[index].Min.Z)
                    break;

                Element floor = doc.GetElement(floorIds[index]);
                foreach (var solid in GetSolids(floor))
                {
                    Solid intersection;
                    try
                    {
                        intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            prism, solid, BooleanOperationsType.Intersect);
                    }
                    catch
                    {
                        continue;
                    }

                    if (intersection == null || intersection.Volume < 1e-7)
                        continue;

                    double z = MinZ(intersection);
                    if (z < best)
                        best = z;
                }
            }

            return best;
        }

        /// <summary>Indexes every floor in the document by id and bounding box.</summary>
        public static void IndexFloors(Document doc, out List<ElementId> ids, out List<BoundingBoxXYZ> boxes)
        {
            ids = new List<ElementId>();
            boxes = new List<BoundingBoxXYZ>();

            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType();

            foreach (Element floor in collector)
            {
                BoundingBoxXYZ box = floor.get_BoundingBox(null);
                if (box == null)
                    continue;
                ids.Add(floor.Id);
                boxes.Add(box);
            }
        }

        /// <summary>Levels sorted by elevation.</summary>
        public static List<Level> GetLevelsByElevation(Document doc)
        {
            var levels = new List<Level>();
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)))
                levels.Add(level);

            levels.Sort(delegate (Level a, Level b) { return a.Elevation.CompareTo(b.Elevation); });
            return levels;
        }

        /// <summary>Reads a type parameter as text, returning null when empty.</summary>
        public static string GetTypeText(Document doc, Element element, string parameterName)
        {
            if (element == null)
                return null;

            var type = doc.GetElement(element.GetTypeId()) as ElementType;
            if (type == null)
                return null;

            Parameter param = type.LookupParameter(parameterName);
            if (param == null)
                return null;

            string value = param.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>Type Mark of an element's type, falling back to the type name.</summary>
        public static string GetTypeMarkOrName(Document doc, Element element)
        {
            var type = doc.GetElement(element.GetTypeId()) as ElementType;
            if (type == null)
                return "?";

            Parameter mark = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string value = mark == null ? null : mark.AsString();
            return string.IsNullOrWhiteSpace(value) ? type.Name : value;
        }
    }
}
