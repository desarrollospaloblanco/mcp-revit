using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Builds excavation and backfill masses for foundations as Toposolids.
    ///
    /// For each footing or foundation beam the excavation runs from the underside of the
    /// element up to the slab above it, and the backfill is that same mass with the concrete
    /// and any columns cut out of it.
    ///
    /// Two rules here were learned the hard way and are the whole point of the command:
    ///
    /// - The ceiling is max(top of the element, underside of the first slab above its *bottom*).
    ///   Searching upwards from the element's top instead skips a slab-on-grade that is flush
    ///   with or embedded in it, and the excavation then runs to the next storey. On a shallow
    ///   tie beam that turned 0.60 m into 3.68 m.
    /// - Every downward face at the element's bottom is used, not just the largest one. Revit
    ///   splits the underside of a long beam where footings cross it, so taking the biggest
    ///   face alone can drop most of the footprint.
    ///
    /// When the ceiling lands on the element's own top there is nothing to backfill, so no fill
    /// mass is created for it.
    /// </summary>
    public class FoundationEarthworksEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public List<BuiltInCategory> Categories { get; private set; }
        public string FramingCodePrefix { get; private set; }
        public string ExcavationCode { get; private set; }
        public string FillCode { get; private set; }
        public string FillParameterName { get; private set; }
        public string OriginParameterName { get; private set; }
        public bool CutColumns { get; private set; }
        public bool ReplaceExisting { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(
            string documentTitle,
            List<BuiltInCategory> categories,
            string framingCodePrefix,
            string excavationCode,
            string fillCode,
            string fillParameterName,
            string originParameterName,
            bool cutColumns,
            bool replaceExisting)
        {
            DocumentTitle = documentTitle;
            Categories = categories;
            FramingCodePrefix = framingCodePrefix;
            ExcavationCode = excavationCode;
            FillCode = fillCode;
            FillParameterName = fillParameterName;
            OriginParameterName = originParameterName;
            CutColumns = cutColumns;
            ReplaceExisting = replaceExisting;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 300000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
#if REVIT2024_OR_GREATER
            try
            {
                string error;
                Document doc = QuantificationUtils.ResolveDocument(app, DocumentTitle, out error);
                if (doc == null)
                {
                    Result = Fail(error);
                    return;
                }

                var targets = CollectTargets(doc);
                if (targets.Count == 0)
                {
                    Result = Fail("No foundation elements matched the given categories");
                    return;
                }

                List<ElementId> floorIds;
                List<BoundingBoxXYZ> floorBoxes;
                QuantificationUtils.IndexFloors(doc, out floorIds, out floorBoxes);

                List<Level> levels = QuantificationUtils.GetLevelsByElevation(doc);
                if (levels.Count == 0)
                {
                    Result = Fail("The document has no levels");
                    return;
                }

                var columns = new List<Element>();
                var columnBoxes = new List<BoundingBoxXYZ>();
                if (CutColumns)
                {
                    foreach (Element column in new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralColumns)
                        .WhereElementIsNotElementType())
                    {
                        BoundingBoxXYZ box = column.get_BoundingBox(null);
                        if (box == null)
                            continue;
                        columns.Add(column);
                        columnBoxes.Add(box);
                    }
                }

                int excavations = 0, fills = 0, elementOnly = 0, skipped = 0, columnCuts = 0;
                double excavationVolume = 0, fillVolume = 0;
                var problems = new List<string>();

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Foundation earthworks"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    ToposolidType template = FindTemplateType(doc);
                    if (template == null)
                    {
                        transaction.RollBack();
                        Result = Fail("The document has no Toposolid type to duplicate");
                        return;
                    }

                    if (ReplaceExisting)
                        RemovePrevious(doc);

                    foreach (Element source in targets)
                    {
                        var solids = QuantificationUtils.GetSolids(source);
                        if (solids.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        BoundingBoxXYZ sourceBox = source.get_BoundingBox(null);
                        if (sourceBox == null)
                        {
                            skipped++;
                            continue;
                        }

                        double bottom = QuantificationUtils.MinZ(solids);
                        double top = sourceBox.Max.Z;
                        var faces = QuantificationUtils.GetBottomFaces(solids);
                        if (faces.Count == 0)
                        {
                            skipped++;
                            problems.Add(Utils.ElementIdExtensions.GetValue(source.Id) + ": no horizontal underside");
                            continue;
                        }

                        bool isFoundationCategory = source.Category != null &&
                            Utils.ElementIdExtensions.GetValue(source.Category.Id) == (long)BuiltInCategory.OST_StructuralFoundation;
                        string originText = isFoundationCategory ? "Zapata" : "Viga de cimentacion";
                        string label = isFoundationCategory ? "zapata" : "viga";
                        string mark = QuantificationUtils.GetTypeMarkOrName(doc, source);

                        int segment = 0;
                        foreach (PlanarFace face in faces)
                        {
                            segment++;

                            CurveLoop probe = QuantificationUtils.FlattenBoundary(face, bottom);
                            CurveLoop profile = QuantificationUtils.FlattenBoundary(face, 0);
                            if (probe == null || profile == null)
                            {
                                skipped++;
                                problems.Add(Utils.ElementIdExtensions.GetValue(source.Id) + ": underside is not made of straight segments");
                                continue;
                            }

                            double underside = QuantificationUtils.FindSlabUndersideAbove(
                                doc, probe, bottom, sourceBox, floorIds, floorBoxes);

                            double ceiling = underside == double.MaxValue ? top : Math.Max(top, underside);
                            double thickness = Math.Round((ceiling - bottom) * QuantificationUtils.FeetToMeters, 2);
                            if (thickness <= 0)
                            {
                                skipped++;
                                continue;
                            }

                            bool nothingToFill = Math.Abs(ceiling - top) < 0.033;
                            if (nothingToFill)
                                elementOnly++;

                            string suffix = faces.Count > 1 ? " tramo " + segment : string.Empty;
                            string comment = "Excavacion " + label + " " + mark + suffix +
                                             " (id " + Utils.ElementIdExtensions.GetValue(source.Id) + ")";

                            Toposolid excavation = CreateMass(
                                doc, template, profile, levels, ceiling, thickness,
                                (isFoundationCategory ? "Excavacion_Zapata_" : "Excavacion_Viga_"),
                                ExcavationCode, comment, originText);

                            if (excavation == null)
                            {
                                skipped++;
                                continue;
                            }

                            excavations++;
                            excavationVolume += QuantificationUtils.GetVolume(excavation);

                            if (nothingToFill)
                            {
                                SetDouble(excavation, FillParameterName, 0);
                                continue;
                            }

                            Toposolid fill = CopyAsFill(doc, excavation, thickness,
                                (isFoundationCategory ? "Relleno_Zapata_" : "Relleno_Viga_"),
                                comment.Replace("Excavacion ", "Relleno "));

                            if (fill == null)
                                continue;

                            Cut(doc, source, fill);
                            if (CutColumns)
                                columnCuts += CutIntersecting(doc, fill, columns, columnBoxes);

                            fills++;
                            double volume = QuantificationUtils.GetVolume(fill);
                            fillVolume += volume;

                            SetDouble(excavation, FillParameterName, volume);
                            SetDouble(fill, FillParameterName, volume);
                        }
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "sourceElements", targets.Count },
                    { "excavationMasses", excavations },
                    { "fillMasses", fills },
                    { "excavationEqualsElement", elementOnly },
                    { "skipped", skipped },
                    { "columnCuts", columnCuts },
                    { "excavationVolumeCubicMeters", Math.Round(excavationVolume * QuantificationUtils.CubicFeetToCubicMeters, 3) },
                    { "fillVolumeCubicMeters", Math.Round(fillVolume * QuantificationUtils.CubicFeetToCubicMeters, 3) },
                    { "problems", problems }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Created " + excavations + " excavation and " + fills + " fill masses in " +
                              doc.Title + failures.Summarize(),
                    Response = payload
                };
            }
            catch (Exception ex)
            {
                Result = Fail(ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
#else
            Result = Fail("Toposolids require Revit 2024 or newer");
            _resetEvent.Set();
#endif
        }

#if REVIT2024_OR_GREATER
        private List<Element> CollectTargets(Document doc)
        {
            var targets = new List<Element>();

            foreach (BuiltInCategory category in Categories)
            {
                foreach (Element element in new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType())
                {
                    // Framing is only included when its classification says substructure,
                    // otherwise every beam in the building would get an excavation.
                    if (category == BuiltInCategory.OST_StructuralFraming &&
                        !string.IsNullOrWhiteSpace(FramingCodePrefix))
                    {
                        string code = QuantificationUtils.GetTypeText(doc, element, "Assembly Code");
                        if (code == null || !code.StartsWith(FramingCodePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    targets.Add(element);
                }
            }

            return targets;
        }

        private void RemovePrevious(Document doc)
        {
            var stale = new List<ElementId>();
            foreach (Element element in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Toposolid)
                .WhereElementIsNotElementType())
            {
                var type = doc.GetElement(element.GetTypeId()) as ElementType;
                if (type == null)
                    continue;
                if (type.Name.StartsWith("Excavacion_", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.StartsWith("Relleno_", StringComparison.OrdinalIgnoreCase))
                    stale.Add(element.Id);
            }

            foreach (ElementId id in stale)
            {
                try
                {
                    doc.Delete(id);
                }
                catch
                {
                }
            }
        }

        private static ToposolidType FindTemplateType(Document doc)
        {
            foreach (ToposolidType type in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Toposolid)
                .WhereElementIsElementType())
            {
                if (!type.Name.StartsWith("Excavacion_", StringComparison.OrdinalIgnoreCase) &&
                    !type.Name.StartsWith("Relleno_", StringComparison.OrdinalIgnoreCase))
                    return type;
            }
            return null;
        }

        private Toposolid CreateMass(
            Document doc,
            ToposolidType template,
            CurveLoop profile,
            List<Level> levels,
            double ceiling,
            double thicknessMeters,
            string typePrefix,
            string assemblyCode,
            string comment,
            string originText)
        {
            string typeName = typePrefix + thicknessMeters.ToString("0.00").Replace(",", ".") + "m";
            ToposolidType type = FindType(doc, typeName);

            if (type == null)
            {
                type = template.Duplicate(typeName) as ToposolidType;
                if (type == null)
                    return null;

                try
                {
                    CompoundStructure structure = type.GetCompoundStructure();
                    structure.SetLayerWidth(0, thicknessMeters / QuantificationUtils.FeetToMeters);
                    type.SetCompoundStructure(structure);
                }
                catch
                {
                }

                if (!string.IsNullOrWhiteSpace(assemblyCode))
                {
                    Parameter code = type.LookupParameter("Assembly Code");
                    if (code != null && !code.IsReadOnly)
                        code.Set(assemblyCode);
                }
            }

            Level host = levels[0];
            foreach (Level level in levels)
            {
                if (level.Elevation <= ceiling + 1e-6)
                    host = level;
            }

            try
            {
                var profiles = new List<CurveLoop> { profile };
                Toposolid mass = Toposolid.Create(doc, profiles, type.Id, host.Id);

                Parameter offset = mass.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM);
                if (offset != null && !offset.IsReadOnly)
                    offset.Set(ceiling - host.Elevation);

                Parameter comments = mass.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments != null && !comments.IsReadOnly)
                    comments.Set(comment);

                SetText(mass, OriginParameterName, originText);
                return mass;
            }
            catch
            {
                return null;
            }
        }

        private Toposolid CopyAsFill(Document doc, Toposolid excavation, double thicknessMeters, string typePrefix, string comment)
        {
            string typeName = typePrefix + thicknessMeters.ToString("0.00").Replace(",", ".") + "m";
            ToposolidType type = FindType(doc, typeName);

            if (type == null)
            {
                var source = doc.GetElement(excavation.GetTypeId()) as ToposolidType;
                if (source == null)
                    return null;

                type = source.Duplicate(typeName) as ToposolidType;
                if (type == null)
                    return null;

                if (!string.IsNullOrWhiteSpace(FillCode))
                {
                    Parameter code = type.LookupParameter("Assembly Code");
                    if (code != null && !code.IsReadOnly)
                        code.Set(FillCode);
                }
            }

            try
            {
                var ids = new List<ElementId> { excavation.Id };
                ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, ids, XYZ.Zero);
                foreach (ElementId id in copies)
                {
                    Element copy = doc.GetElement(id);
                    copy.ChangeTypeId(type.Id);

                    Parameter comments = copy.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comments != null && !comments.IsReadOnly)
                        comments.Set(comment);

                    return copy as Toposolid;
                }
            }
            catch
            {
            }

            return null;
        }

        private static ToposolidType FindType(Document doc, string name)
        {
            foreach (ToposolidType type in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Toposolid)
                .WhereElementIsElementType())
            {
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                    return type;
            }
            return null;
        }

        /// <summary>Joins two elements so that the cutter removes material from the cut one.</summary>
        private static void Cut(Document doc, Element cutter, Element target)
        {
            try
            {
                if (!JoinGeometryUtils.AreElementsJoined(doc, cutter, target))
                    JoinGeometryUtils.JoinGeometry(doc, cutter, target);
                if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, cutter, target))
                    JoinGeometryUtils.SwitchJoinOrder(doc, cutter, target);
            }
            catch
            {
            }
        }

        private static int CutIntersecting(Document doc, Element target, List<Element> candidates, List<BoundingBoxXYZ> boxes)
        {
            BoundingBoxXYZ box = target.get_BoundingBox(null);
            if (box == null)
                return 0;

            int count = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                BoundingBoxXYZ other = boxes[i];
                if (other.Max.X < box.Min.X || other.Min.X > box.Max.X)
                    continue;
                if (other.Max.Y < box.Min.Y || other.Min.Y > box.Max.Y)
                    continue;
                if (other.Max.Z < box.Min.Z || other.Min.Z > box.Max.Z)
                    continue;

                Cut(doc, candidates[i], target);
                count++;
            }

            return count;
        }

        private static void SetText(Element element, string parameterName, string value)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                return;

            Parameter param = element.LookupParameter(parameterName);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                param.Set(value);
        }

        private static void SetDouble(Element element, string parameterName, double value)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                return;

            Parameter param = element.LookupParameter(parameterName);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.Double)
                param.Set(value);
        }
#endif

        private static AIResult<Dictionary<string, object>> Fail(string message)
        {
            return new AIResult<Dictionary<string, object>>
            {
                Success = false,
                Message = message
            };
        }

        public string GetName()
        {
            return "Foundation earthworks";
        }
    }
}
