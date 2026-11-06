using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// The ACERO schedules (by size, by level and element, piece list, laps, welded mesh) on the
    /// "MCP Acero" parameters, and the 3D views "ACERO - 3D completo" (all steel, concrete see-through)
    /// and "ACERO - Zonas de traslape" (spliced pieces in magenta, the rest grey) with a section box
    /// around the steel. Existing schedules and views are kept unless replace is asked for.
    /// </summary>
    public static class RebarDocumentation
    {
        private static readonly BuiltInCategory[] Concrete =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs
        };

        public const string FullView = "ACERO - 3D completo";
        public const string LapView = "ACERO - Zonas de traslape";
        public const string LapFilter = "ACERO - Piezas traslapadas";

        public static List<string> Schedules(Document doc, string prefix, IList<string> zones, IList<string> levels, bool replace)
        {
            var made = new List<string>();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
            var rebar = new ElementId(BuiltInCategory.OST_Rebar);
            var fabric = new ElementId(BuiltInCategory.OST_FabricAreas);
            ElementId P(string name) => RebarParameters.Id(doc, name);

            ViewSchedule Start(string suffix, ElementId category)
            {
                string name = prefix + " " + suffix;
                if (existing.TryGetValue(name, out ViewSchedule old))
                {
                    if (!replace) return null;
                    doc.Delete(old.Id);
                }
                var vs = ViewSchedule.CreateSchedule(doc, category);
                vs.Name = name;
                made.Add(name);
                return vs;
            }

            ViewSchedule s = Start("01 - Resumen por diámetro", rebar);
            if (s != null)
            {
                var d = s.Definition;
                var size = Field(d, P(RebarParameters.Size), "Diámetro");
                Total(d, new ElementId(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS), "Barras", false);
                Total(d, new ElementId(BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH), "Longitud total", false);
                Total(d, P(RebarParameters.Weight), "Peso neto (qq)", true);
                foreach (string z in zones) Total(d, P(RebarParameters.WeightIn(z)), z + " (qq)", true);
                Group(d, size.FieldId, false);
            }

            s = Start("02 - Por nivel y elemento", rebar);
            if (s != null)
            {
                var d = s.Definition;
                var g = Field(d, P(RebarParameters.Group), "Elemento");
                foreach (string l in levels) Total(d, P(RebarParameters.WeightIn(l)), l + " (qq)", true);
                Total(d, P(RebarParameters.Weight), "Total (qq)", true);
                Group(d, g.FieldId, false);
            }

            s = Start("03 - Despiece por pieza", rebar);
            if (s != null)
            {
                var d = s.Definition;
                var m = Field(d, P(RebarParameters.PieceMark), "Pieza");
                Field(d, P(RebarParameters.Size), "Diámetro");
                Field(d, new ElementId(BuiltInParameter.REBAR_SHAPE), "Forma Revit");
                Field(d, P(RebarParameters.CutLength), "Largo de corte (m)");
                Total(d, new ElementId(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS), "Cantidad", false);
                Total(d, P(RebarParameters.Weight), "Peso (qq)", true);
                Group(d, m.FieldId, false);
            }

            s = Start("04 - Traslapes", rebar);
            if (s != null)
            {
                var d = s.Definition;
                var t = Field(d, P(RebarParameters.Lap), "Traslape");
                Field(d, P(RebarParameters.Element), "Elemento");
                Field(d, P(RebarParameters.Description), "Barra");
                Field(d, P(RebarParameters.Size), "Diámetro");
                Field(d, new ElementId(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS), "Barras");
                Field(d, P(RebarParameters.CutLength), "Largo pieza (m)");
                d.AddFilter(new ScheduleFilter(t.FieldId, ScheduleFilterType.Contains, "T"));
                d.AddSortGroupField(new ScheduleSortGroupField(t.FieldId));
                d.IsItemized = true;
            }

            s = Start("05 - Electromalla", fabric);
            if (s != null)
            {
                var d = s.Definition;
                var m = Field(d, P(RebarParameters.Size), "Malla");
                Field(d, P(RebarParameters.Element), "Elemento");
                Total(d, P(RebarParameters.MeshArea), "Área neta (m²)", true);
                Total(d, P(RebarParameters.Weight), "Peso (qq)", true);
                foreach (string z in zones) Total(d, P(RebarParameters.WeightIn(z)), z + " (qq)", true);
                d.AddSortGroupField(new ScheduleSortGroupField(m.FieldId) { ShowFooter = true });
                d.IsItemized = true;
                d.ShowGrandTotal = true;
                d.ShowGrandTotalTitle = true;
            }
            return made;
        }

        private static ScheduleField Field(ScheduleDefinition d, ElementId id, string heading)
        {
            var f = d.AddField(ScheduleFieldType.Instance, id);
            f.ColumnHeading = heading;
            return f;
        }

        private static void Total(ScheduleDefinition d, ElementId id, string heading, bool twoDecimals)
        {
            var f = Field(d, id, heading);
            f.DisplayType = ScheduleFieldDisplayType.Totals;
#if REVIT2022_OR_GREATER
            if (twoDecimals)
            {
                try
                {
                    var fo = new FormatOptions(UnitTypeId.Fixed) { Accuracy = 0.01 };
                    f.SetFormatOptions(fo);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // fields that are not plain numbers keep the project format
                }
            }
#endif
        }

        private static void Group(ScheduleDefinition d, ScheduleFieldId field, bool itemized)
        {
            d.AddSortGroupField(new ScheduleSortGroupField(field));
            d.IsItemized = itemized;
            d.ShowGrandTotal = true;
            d.ShowGrandTotalTitle = true;
        }

        // ------------------------------------------------------------------ 3D views

        public static List<string> Views(Document doc, bool replace, List<string> notes)
        {
            var made = new List<string>();
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).ToList();
            var rebarCat = new ElementId(BuiltInCategory.OST_Rebar);
            ElementId lapParam = RebarParameters.Id(doc, RebarParameters.Lap);

            ParameterFilterElement filter = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == LapFilter);
            if (filter == null)
            {
#if REVIT2023_OR_GREATER
                FilterRule rule = ParameterFilterRuleFactory.CreateContainsRule(lapParam, "T");
#else
                FilterRule rule = ParameterFilterRuleFactory.CreateContainsRule(lapParam, "T", false);
#endif
                filter = ParameterFilterElement.Create(doc, LapFilter, new List<ElementId> { rebarCat }, new ElementParameterFilter(rule));
            }
            ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .First(p => p.GetFillPattern().IsSolidFill).Id;
            BoundingBoxXYZ box = SteelBox(doc);

            foreach (string name in new[] { FullView, LapView })
            {
                View3D old = views.FirstOrDefault(v => v.Name == name);
                if (old != null)
                {
                    if (!replace) continue;
                    doc.Delete(old.Id);
                }
                View3D v = View3D.CreateIsometric(doc, vft.Id);
                v.Name = name;
                v.DetailLevel = ViewDetailLevel.Fine;
                v.DisplayStyle = DisplayStyle.ShadingWithEdges;
                bool laps = name == LapView;
                var keep = new HashSet<long>(Concrete.Concat(new[] { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricAreas,
                    BuiltInCategory.OST_FabricReinforcement, BuiltInCategory.OST_Lines }).Select(b => (long)b));
                foreach (Category c in doc.Settings.Categories)
                    if (c.CategoryType == CategoryType.Model && v.CanCategoryBeHidden(c.Id) && !keep.Contains(c.Id.GetValue()))
                        v.SetCategoryHidden(c.Id, true);
                foreach (BuiltInCategory bic in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox })
                {
                    var id = new ElementId(bic);
                    if (v.CanCategoryBeHidden(id)) v.SetCategoryHidden(id, true);
                }
                foreach (BuiltInCategory bic in Concrete)
                {
                    var o = new OverrideGraphicSettings();
                    o.SetSurfaceTransparency(laps ? 85 : 70);
                    o.SetProjectionLineWeight(1);
                    o.SetCutLineWeight(1);
                    v.SetCategoryOverrides(new ElementId(bic), o);
                }
                if (laps)
                {
                    var grey = new OverrideGraphicSettings();
                    grey.SetHalftone(true);
                    grey.SetProjectionLineColor(new Color(190, 190, 190));
                    v.SetCategoryOverrides(rebarCat, grey);
                    v.SetCategoryHidden(new ElementId(BuiltInCategory.OST_FabricReinforcement), true);
                    v.SetCategoryHidden(new ElementId(BuiltInCategory.OST_FabricAreas), true);
                    v.AddFilter(filter.Id);
                    var magenta = new OverrideGraphicSettings();
                    magenta.SetProjectionLineColor(new Color(230, 0, 126));
                    magenta.SetProjectionLineWeight(8);
                    magenta.SetSurfaceForegroundPatternId(solid);
                    magenta.SetSurfaceForegroundPatternColor(new Color(230, 0, 126));
                    v.SetFilterOverrides(filter.Id, magenta);
                }
                else
                {
                    var mesh = new OverrideGraphicSettings();
                    mesh.SetProjectionLineColor(new Color(170, 200, 230));
                    mesh.SetHalftone(true);
                    v.SetCategoryOverrides(new ElementId(BuiltInCategory.OST_FabricReinforcement), mesh);
                    v.SetCategoryOverrides(new ElementId(BuiltInCategory.OST_FabricAreas), mesh);
                    var bars = new OverrideGraphicSettings();
                    bars.SetProjectionLineColor(new Color(160, 40, 30));
                    v.SetCategoryOverrides(rebarCat, bars);
                }
                if (box != null)
                {
                    v.IsSectionBoxActive = true;
                    v.SetSectionBox(box);
                }
                int solidBars = 0;
                foreach (Autodesk.Revit.DB.Structure.Rebar r in new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Structure.Rebar)))
                {
                    try { r.SetUnobscuredInView(v, true); solidBars++; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                }
                notes.Add($"{name}: {solidBars} bar sets shown unobscured");
                made.Add(name);
            }
            return made;
        }

        /// <summary>The box around all the steel, half a metre larger on each side.</summary>
        private static BoundingBoxXYZ SteelBox(Document doc)
        {
            XYZ min = null, max = null;
            foreach (BuiltInCategory bic in new[] { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricAreas })
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) continue;
                    min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
                    max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
                }
            if (min == null) return null;
            double m = Metric.Ft(0.5);
            return new BoundingBoxXYZ { Min = min - new XYZ(m, m, m), Max = max + new XYZ(m, m, m) };
        }
    }
}
