using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// A building described as data, in metres, ready to be built into Revit.
    ///
    /// Every element carries an id that is stable across runs. The builder stamps that id on
    /// the element it creates, so sending the same spec twice updates the model instead of
    /// duplicating it, and a spec corrected after review only touches what changed.
    ///
    /// Levels and element types are referenced by the spec id or by their Revit name.
    /// Coordinates are plan X/Y from the project's internal origin.
    ///
    /// A level reference may also be relative: "+1" is the next spec level above the element's
    /// base level and "-1" the one below. Together with repeatOn this lets one typical floor
    /// be written once and built on every level it repeats on.
    /// </summary>
    public class ModelSpec
    {
        [JsonProperty("levels")] public List<LevelSpec> Levels { get; set; } = new List<LevelSpec>();
        [JsonProperty("grids")] public List<GridSpec> Grids { get; set; } = new List<GridSpec>();
        [JsonProperty("types")] public TypesSpec Types { get; set; } = new TypesSpec();
        [JsonProperty("columns")] public List<ColumnSpec> Columns { get; set; } = new List<ColumnSpec>();
        [JsonProperty("walls")] public List<WallSpec> Walls { get; set; } = new List<WallSpec>();
        [JsonProperty("beams")] public List<BeamSpec> Beams { get; set; } = new List<BeamSpec>();
        [JsonProperty("floors")] public List<FloorSpec> Floors { get; set; } = new List<FloorSpec>();
        [JsonProperty("doors")] public List<OpeningSpec> Doors { get; set; } = new List<OpeningSpec>();
        [JsonProperty("windows")] public List<OpeningSpec> Windows { get; set; } = new List<OpeningSpec>();
    }

    public class LevelSpec
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("elevation")] public double Elevation { get; set; }
        /// <summary>Create a floor plan for the level when it has none. Defaults to true.</summary>
        [JsonProperty("createPlan")] public bool CreatePlan { get; set; } = true;
    }

    public class GridSpec
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("start")] public double[] Start { get; set; }
        [JsonProperty("end")] public double[] End { get; set; }
        /// <summary>Optional point on the arc, for a curved grid.</summary>
        [JsonProperty("mid")] public double[] Mid { get; set; }
    }

    public class TypesSpec
    {
        [JsonProperty("walls")] public List<LayeredTypeSpec> Walls { get; set; } = new List<LayeredTypeSpec>();
        [JsonProperty("floors")] public List<LayeredTypeSpec> Floors { get; set; } = new List<LayeredTypeSpec>();
        [JsonProperty("columns")] public List<SectionTypeSpec> Columns { get; set; } = new List<SectionTypeSpec>();
        [JsonProperty("beams")] public List<SectionTypeSpec> Beams { get; set; } = new List<SectionTypeSpec>();
        [JsonProperty("doors")] public List<OpeningTypeSpec> Doors { get; set; } = new List<OpeningTypeSpec>();
        [JsonProperty("windows")] public List<OpeningTypeSpec> Windows { get; set; } = new List<OpeningTypeSpec>();
    }

    /// <summary>A door or window type defined by its size, duplicated from a loaded family.</summary>
    public class OpeningTypeSpec
    {
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Loaded family to duplicate from. Required when the type does not exist yet.</summary>
        [JsonProperty("family")] public string Family { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
    }

    /// <summary>A wall or floor type defined by its total thickness.</summary>
    public class LayeredTypeSpec
    {
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Existing type to duplicate from. Defaults to the first basic type.</summary>
        [JsonProperty("base")] public string Base { get; set; }
        [JsonProperty("thickness")] public double Thickness { get; set; }
        /// <summary>Material of the single layer. Defaults to the base type's structural material.</summary>
        [JsonProperty("material")] public string Material { get; set; }
    }

    /// <summary>A column or beam type defined by its rectangular section.</summary>
    public class SectionTypeSpec
    {
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Loaded family to duplicate from. Defaults to the first one with a b × h section.</summary>
        [JsonProperty("family")] public string Family { get; set; }
        /// <summary>Section width (b), in metres.</summary>
        [JsonProperty("b")] public double B { get; set; }
        /// <summary>Section depth (h), in metres.</summary>
        [JsonProperty("h")] public double H { get; set; }
        /// <summary>Type parameter holding b. Tried from a list of common names when omitted.</summary>
        [JsonProperty("widthParam")] public string WidthParam { get; set; }
        /// <summary>Type parameter holding h. Tried from a list of common names when omitted.</summary>
        [JsonProperty("depthParam")] public string DepthParam { get; set; }
        /// <summary>Columns only: true for architectural columns. Defaults to structural.</summary>
        [JsonProperty("architectural")] public bool Architectural { get; set; }
    }

    /// <summary>Fields shared by the elements placed on a level.</summary>
    public abstract class PlacedSpec
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        /// <summary>
        /// Extra levels to build a copy of this element on. Each copy takes the listed level as
        /// its base level and gets the id "&lt;id&gt;@&lt;level&gt;".
        /// </summary>
        [JsonProperty("repeatOn")] public List<string> RepeatOn { get; set; }
    }

    public class ColumnSpec : PlacedSpec
    {
        [JsonProperty("at")] public double[] At { get; set; }
        [JsonProperty("baseLevel")] public string BaseLevel { get; set; }
        [JsonProperty("topLevel")] public string TopLevel { get; set; }
        [JsonProperty("baseOffset")] public double BaseOffset { get; set; }
        [JsonProperty("topOffset")] public double TopOffset { get; set; }
        /// <summary>Rotation about the column axis, in degrees counter-clockwise.</summary>
        [JsonProperty("rotation")] public double Rotation { get; set; }
    }

    public class WallSpec : PlacedSpec
    {
        [JsonProperty("start")] public double[] Start { get; set; }
        [JsonProperty("end")] public double[] End { get; set; }
        /// <summary>Optional point on the arc, for a curved wall.</summary>
        [JsonProperty("mid")] public double[] Mid { get; set; }
        [JsonProperty("baseLevel")] public string BaseLevel { get; set; }
        /// <summary>Level the top is tied to. Either this or height is required.</summary>
        [JsonProperty("topLevel")] public string TopLevel { get; set; }
        /// <summary>Unconnected height, used when there is no topLevel.</summary>
        [JsonProperty("height")] public double? Height { get; set; }
        [JsonProperty("baseOffset")] public double BaseOffset { get; set; }
        [JsonProperty("topOffset")] public double TopOffset { get; set; }
        [JsonProperty("structural")] public bool Structural { get; set; }
        /// <summary>
        /// Which line start → end describes: "center" (default), or the "left" or "right" face
        /// looking from start to end. Faces are shifted to the centreline by half the type width.
        /// </summary>
        [JsonProperty("alignment")] public string Alignment { get; set; }
    }

    public class BeamSpec : PlacedSpec
    {
        [JsonProperty("start")] public double[] Start { get; set; }
        [JsonProperty("end")] public double[] End { get; set; }
        [JsonProperty("mid")] public double[] Mid { get; set; }
        [JsonProperty("level")] public string Level { get; set; }
        /// <summary>Top of the beam relative to the level. 0 puts it flush with the level.</summary>
        [JsonProperty("offset")] public double Offset { get; set; }
        /// <summary>Top of the beam at its end, for a sloped beam. Defaults to offset.</summary>
        [JsonProperty("endOffset")] public double? EndOffset { get; set; }
    }

    public class FloorSpec : PlacedSpec
    {
        [JsonProperty("level")] public string Level { get; set; }
        /// <summary>Top of the slab relative to the level.</summary>
        [JsonProperty("offset")] public double Offset { get; set; }
        /// <summary>Outline as a list of [x, y] vertices; the closing edge is implied.</summary>
        [JsonProperty("boundary")] public List<double[]> Boundary { get; set; }
        /// <summary>Openings, each a list of [x, y] vertices inside the outline.</summary>
        [JsonProperty("holes")] public List<List<double[]>> Holes { get; set; } = new List<List<double[]>>();
        [JsonProperty("structural")] public bool Structural { get; set; } = true;
        /// <summary>Optional slope arrow for a ramp; the slab is at level + offset at its tail.</summary>
        [JsonProperty("slopeArrow")] public SlopeArrowSpec SlopeArrow { get; set; }
    }

    /// <summary>A door or window hosted in a wall built from the same spec.</summary>
    public class OpeningSpec : PlacedSpec
    {
        /// <summary>
        /// Spec id of the host wall. Copies made by repeatOn host in the wall copy on their own
        /// level, "&lt;hostWall&gt;@&lt;level&gt;", so a typical floor's openings follow its walls.
        /// </summary>
        [JsonProperty("hostWall")] public string HostWall { get; set; }
        /// <summary>Plan position of the opening's centre; it is projected onto the wall.</summary>
        [JsonProperty("at")] public double[] At { get; set; }
        /// <summary>Level the opening belongs to. Defaults to the host wall's base level.</summary>
        [JsonProperty("level")] public string Level { get; set; }
        /// <summary>Sill height above the level, in metres. 0 for doors.</summary>
        [JsonProperty("sill")] public double Sill { get; set; }
        /// <summary>Plan direction the opening should face (a door's swing side). Flipped to match.</summary>
        [JsonProperty("facing")] public double[] Facing { get; set; }
        /// <summary>Plan direction of the hand (hinge towards latch). Flipped to match.</summary>
        [JsonProperty("hand")] public double[] Hand { get; set; }
    }

    public class SlopeArrowSpec
    {
        [JsonProperty("from")] public double[] From { get; set; }
        [JsonProperty("to")] public double[] To { get; set; }
        /// <summary>Slope in percent, rising from "from" towards "to".</summary>
        [JsonProperty("percent")] public double Percent { get; set; }
    }
}
