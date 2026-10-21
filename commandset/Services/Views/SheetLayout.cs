namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    /// Sheet geometry in millimetres of paper. Viewport outlines include annotation that sticks out
    /// of the crop, so a corner alignment uses the outline, and a model-point alignment uses the
    /// view outline whose origin is the project origin: a model point (x, y) sits on the sheet at
    /// BoxCenter + (x / scale - cU, y / scale - cV), with (cU, cV) the centre of View.Outline.
    /// </summary>
    public static class SheetLayout
    {
        public const double FeetPerMm = 1 / 304.8;

        public static double Mm(double feet) => Math.Round(feet * 304.8, 2);

        public static XYZ Paper(double xMm, double yMm) => new XYZ(xMm * FeetPerMm, yMm * FeetPerMm, 0);

        public static FamilySymbol TitleBlock(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>().ToList();
            if (all.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) return all[0];
            return all.FirstOrDefault(s => s.FamilyName + " : " + s.Name == name)
                   ?? all.FirstOrDefault(s => s.Name == name)
                   ?? all.FirstOrDefault(s => s.FamilyName == name)
                   ?? throw new InvalidOperationException("No title block '" + name + "'. Title blocks: " +
                                                          string.Join(", ", all.Select(s => s.FamilyName + " : " + s.Name)));
        }

        /// <summary>Box of the title block instance on the sheet, or of the sheet outline without one.</summary>
        public static BoundingBoxXYZ Frame(ViewSheet sheet)
        {
            Element tb = new FilteredElementCollector(sheet.Document, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().FirstElement();
            BoundingBoxXYZ box = tb?.get_BoundingBox(sheet);
            if (box != null) return box;
            BoundingBoxUV o = sheet.Outline;
            return new BoundingBoxXYZ { Min = new XYZ(o.Min.U, o.Min.V, 0), Max = new XYZ(o.Max.U, o.Max.V, 0) };
        }

        /// <summary>Moves a viewport so the top-left corner of its outline lands on the target point.</summary>
        public static void TopLeftTo(Viewport vp, XYZ target)
        {
            for (int i = 0; i < 3; i++)
            {
                Outline o = vp.GetBoxOutline();
                XYZ delta = new XYZ(target.X - o.MinimumPoint.X, target.Y - o.MaximumPoint.Y, 0);
                if (delta.GetLength() < 1e-6) return;
                vp.SetBoxCenter(vp.GetBoxCenter() + delta);
            }
        }

        public static void TopLeftTo(ScheduleSheetInstance schedule, XYZ target)
        {
            BoundingBoxXYZ b = schedule.get_BoundingBox(schedule.Document.GetElement(schedule.OwnerViewId) as View);
            if (b == null) return;
            schedule.Point += new XYZ(target.X - b.Min.X, target.Y - b.Max.Y, 0);
        }

        /// <summary>Moves a viewport so a model point (feet) lands on a sheet point (feet).</summary>
        public static void ModelPointTo(Viewport vp, XYZ modelPoint, XYZ sheetPoint)
        {
            View view = (View)vp.Document.GetElement(vp.ViewId);
            for (int i = 0; i < 3; i++)
            {
                XYZ now = SheetPointOf(vp, view, modelPoint);
                XYZ delta = new XYZ(sheetPoint.X - now.X, sheetPoint.Y - now.Y, 0);
                if (delta.GetLength() < 1e-7) return;
                vp.SetBoxCenter(vp.GetBoxCenter() + delta);
            }
        }

        public static XYZ SheetPointOf(Viewport vp, View view, XYZ modelPoint)
        {
            double e = 1.0 / view.Scale;
            BoundingBoxUV o = view.Outline;
            double cu = (o.Min.U + o.Max.U) / 2, cv = (o.Min.V + o.Max.V) / 2;
            XYZ c = vp.GetBoxCenter();
            return new XYZ(c.X + modelPoint.X * e - cu, c.Y + modelPoint.Y * e - cv, 0);
        }

        public static object Describe(Viewport vp)
        {
            View view = (View)vp.Document.GetElement(vp.ViewId);
            Outline o = vp.GetBoxOutline();
            return new
            {
                viewportId = Utils.ElementIdExtensions.GetValue(vp.Id),
                view = view?.Name,
                scale = view?.Scale,
                centerMm = new[] { Mm(vp.GetBoxCenter().X), Mm(vp.GetBoxCenter().Y) },
                outlineMm = new[] { Mm(o.MinimumPoint.X), Mm(o.MinimumPoint.Y), Mm(o.MaximumPoint.X), Mm(o.MaximumPoint.Y) }
            };
        }
    }
}
