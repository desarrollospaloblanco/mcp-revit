using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// What a built element remembers about the spec entry that produced it.
    /// </summary>
    public class SpecTag
    {
        /// <summary>Name of the spec run, so two specs in one model never touch each other.</summary>
        public string Spec;
        /// <summary>"kind:id", for example "wall:W12@N05".</summary>
        public string Key;
        /// <summary>Canonical text of the spec entry the element was last built from.</summary>
        public string Signature;
    }

    /// <summary>
    /// Stamps built elements with their spec key using extensible storage.
    ///
    /// Extensible storage rather than Comments or Mark on purpose: those are user-facing fields
    /// the team already fills in, and overwriting them to track provenance would be destructive.
    /// The stamp is invisible, survives save and reload, and travels with the element.
    /// </summary>
    public static class SpecTagStorage
    {
        private static readonly Guid SchemaGuid = new Guid("6b1d2f9e-4c1a-4f0e-9a57-3f8e2c7d5b10");
        private const string SpecField = "Spec";
        private const string KeyField = "Key";
        private const string SignatureField = "Signature";

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
                return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("McpBuildModelFromSpec");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(SpecField, typeof(string));
            builder.AddSimpleField(KeyField, typeof(string));
            builder.AddSimpleField(SignatureField, typeof(string));
            return builder.Finish();
        }

        /// <summary>Writes the stamp. Needs an open transaction.</summary>
        public static void Write(Element element, SpecTag tag)
        {
            Schema schema = GetSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(SpecField), tag.Spec ?? string.Empty);
            entity.Set(schema.GetField(KeyField), tag.Key ?? string.Empty);
            entity.Set(schema.GetField(SignatureField), tag.Signature ?? string.Empty);
            element.SetEntity(entity);
        }

        /// <summary>Reads the stamp, or null when the element was not built from a spec.</summary>
        public static SpecTag Read(Element element)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
                return null;

            Entity entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid())
                return null;

            return new SpecTag
            {
                Spec = entity.Get<string>(schema.GetField(SpecField)),
                Key = entity.Get<string>(schema.GetField(KeyField)),
                Signature = entity.Get<string>(schema.GetField(SignatureField))
            };
        }

        /// <summary>
        /// Every element stamped by the given spec, by key. When a key appears twice (a copy
        /// made by hand in Revit carries the stamp along) the first one wins and the rest are
        /// returned as duplicates so the caller can report them.
        /// </summary>
        public static Dictionary<string, Element> Index(Document doc, string spec, List<Element> duplicates)
        {
            var index = new Dictionary<string, Element>(StringComparer.Ordinal);
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
                return index;

            var collectors = new[]
            {
                new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid)).WhereElementIsNotElementType(),
                new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid)).WhereElementIsElementType()
            };

            foreach (var collector in collectors)
            {
                foreach (Element element in collector)
                {
                    SpecTag tag = Read(element);
                    if (tag == null || !string.Equals(tag.Spec, spec, StringComparison.Ordinal))
                        continue;

                    if (index.ContainsKey(tag.Key))
                        duplicates?.Add(element);
                    else
                        index[tag.Key] = element;
                }
            }

            return index;
        }
    }
}
