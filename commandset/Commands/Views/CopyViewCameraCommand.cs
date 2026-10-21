using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Quantification;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Copies the camera of a 3D view (orientation, absolute crop box, section box, display style,
    /// far clip) onto the view of the same name in other documents, creating it when missing, so two
    /// design options are seen exactly from the same place.
    /// </summary>
    public class CopyViewCameraCommand : DelegateCommand
    {
        public override string CommandName => "copy_view_camera";

        public CopyViewCameraCommand(UIApplication uiApp) : base(uiApp, "MCP: copiar cámara")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var warnings = new List<string>();
            string sourceTitle = parameters?["sourceDocument"]?.Value<string>();
            string viewKey = parameters?["view"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(viewKey))
                throw new ArgumentException("view (name or id of the source 3D view) is required");
            var targets = (parameters["targetDocuments"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("targetDocuments is required");
            bool copySectionBox = parameters["copySectionBox"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document source = QuantificationUtils.ResolveDocument(app, sourceTitle, out string error)
                                  ?? throw new InvalidOperationException(error);
                View3D from = ExportViewsCommand.FindView(source, viewKey) as View3D
                              ?? throw new InvalidOperationException("No 3D view '" + viewKey + "' in " + source.Title);

                var results = new List<object>();
                foreach (Document target in SetPerspectiveViewCommand.ResolveDocuments(app, targets))
                {
                    if (target.Equals(source)) continue;
                    ToolTransaction.Run(target, "MCP: copiar cámara " + from.Name, options, warnings, () =>
                    {
                        View3D to = Services.Views.PresentationViewService.GetOrCreatePerspective(target, from.Name, from.IsPerspective, out bool created);
                        if (to.IsPerspective != from.IsPerspective)
                            throw new InvalidOperationException(from.Name + " in " + target.Title + " is not the same kind of 3D view (perspective vs isometric)");
                        to.ViewTemplateId = ElementId.InvalidElementId;
                        to.SetOrientation(from.GetOrientation());
                        to.DisplayStyle = from.DisplayStyle;
                        to.DetailLevel = from.DetailLevel;
                        CopyFarClip(from, to);
                        to.CropBoxActive = from.CropBoxActive;
                        to.CropBoxVisible = from.CropBoxVisible;

                        // The crop is copied with its depth: the near plane of each document differs, and
                        // only the absolute box gives the same framing.
                        BoundingBoxXYZ crop = to.CropBox;
                        crop.Min = from.CropBox.Min;
                        crop.Max = from.CropBox.Max;
                        to.CropBox = crop;

                        if (copySectionBox)
                        {
                            to.IsSectionBoxActive = from.IsSectionBoxActive;
                            if (from.IsSectionBoxActive) to.SetSectionBox(from.GetSectionBox());
                        }
                        RunMarks.Set(to, options.RunMark);
                        results.Add(new { document = target.Title, view = to.Name, viewId = Utils.ElementIdExtensions.GetValue(to.Id), created });
                    });
                }
                return new { source = source.Title, view = from.Name, copied = results, warnings };
            }, 120);
        }

        private static void CopyFarClip(View3D from, View3D to)
        {
            foreach (var bip in new[] { BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR })
            {
                Parameter src = from.get_Parameter(bip), dst = to.get_Parameter(bip);
                if (src == null || dst == null || dst.IsReadOnly) continue;
                if (src.StorageType == StorageType.Integer) dst.Set(src.AsInteger());
                else if (src.StorageType == StorageType.Double) dst.Set(src.AsDouble());
            }
        }
    }
}
