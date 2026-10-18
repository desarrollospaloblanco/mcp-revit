using System.Reflection;

namespace RevitMCPCommandSet.Services.ApiLookup
{
    /// <summary>
    /// Describes Revit API types and members by reflecting the RevitAPI assemblies loaded in the
    /// running Revit, so signatures always match the installed version. Pure reflection: no Revit
    /// object is touched, so it is safe off Revit's main thread.
    /// </summary>
    public static class RevitApiReflector
    {
        private static List<Type> _types;
        private static readonly object _lock = new object();

        private static List<Type> Types
        {
            get
            {
                lock (_lock)
                {
                    if (_types != null) return _types;
                    _types = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => !a.IsDynamic && a.GetName().Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(SafeTypes)
                        .Where(t => t.IsPublic || t.IsNestedPublic)
                        .ToList();
                    return _types;
                }
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        /// <summary>
        /// name: "Wall", "Autodesk.Revit.DB.Wall", "Wall.Create", "BuiltInParameter.WALL_BASE_OFFSET"
        /// or a bare member or enum value such as "WALL_BASE_OFFSET".
        /// </summary>
        public static object Lookup(string name, string memberFilter, int maxMembers)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                throw new ArgumentException("name is required");

            // "Type.Member": try the longest prefix that resolves to a type.
            Type type = FindType(name);
            string member = memberFilter;
            if (type == null && name.Contains('.'))
            {
                int dot = name.LastIndexOf('.');
                type = FindType(name.Substring(0, dot));
                if (type != null) member = name.Substring(dot + 1);
            }

            if (type == null)
            {
                var enumHits = FindEnumValues(name);
                return new
                {
                    found = false,
                    query = name,
                    closestTypes = Closest(Types.Select(t => t.Name).Distinct(), name, 10),
                    enumValuesNamed = enumHits
                };
            }

            return Describe(type, member, maxMembers);
        }

        private static Type FindType(string name)
        {
            return Types.FirstOrDefault(t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase))
                   ?? Types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                           // Prefer the DB namespace when a name exists in several (for example Document).
                           .OrderBy(t => t.Namespace == "Autodesk.Revit.DB" ? 0 : 1)
                           .FirstOrDefault();
        }

        private static object Describe(Type type, string member, int maxMembers)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            bool Match(string n) => string.IsNullOrEmpty(member) || n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0;

            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var hits = names.Where(Match).ToList();
                return new
                {
                    found = string.IsNullOrEmpty(member) || hits.Count > 0,
                    type = type.FullName,
                    @namespace = type.Namespace,
                    import = "using " + type.Namespace + ";",
                    kind = "enum",
                    valueCount = names.Length,
                    values = hits.Take(maxMembers),
                    closestValues = hits.Count == 0 ? Closest(names, member, 10) : null
                };
            }

            var constructors = type.GetConstructors().Select(c => type.Name + "(" + Params(c) + ")").ToList();
            var methods = type.GetMethods(flags).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && Match(m.Name))
                .Select(m => (m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " + m.Name + "(" + Params(m) + ")" +
                             (m.DeclaringType != type ? "  [de " + m.DeclaringType.Name + "]" : ""))
                .Distinct().OrderBy(s => s).ToList();
            var properties = type.GetProperties(flags).Where(p => Match(p.Name))
                .Select(p => Short(p.PropertyType) + " " + p.Name + " { " + (p.CanRead ? "get; " : "") +
                             (p.CanWrite && p.GetSetMethod() != null ? "set; " : "") + "}" +
                             (p.DeclaringType != type ? "  [de " + p.DeclaringType.Name + "]" : ""))
                .Distinct().OrderBy(s => s).ToList();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => Match(f.Name))
                .Select(f => "static " + Short(f.FieldType) + " " + f.Name).ToList();

            bool memberFound = string.IsNullOrEmpty(member) || methods.Count + properties.Count + fields.Count > 0;
            var allNames = type.GetMembers(flags).Select(m => m.Name).Distinct();

            return new
            {
                found = memberFound,
                type = type.FullName,
                @namespace = type.Namespace,
                import = "using " + type.Namespace + ";",
                kind = type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                baseType = type.BaseType?.FullName,
                memberFilter = member,
                constructors = string.IsNullOrEmpty(member) ? constructors : null,
                methods = methods.Take(maxMembers),
                properties = properties.Take(maxMembers),
                staticFields = fields.Take(maxMembers),
                truncated = methods.Count > maxMembers || properties.Count > maxMembers,
                creation = Creation(type),
                closestMembers = memberFound ? null : Closest(allNames, member, 10)
            };
        }

        /// <summary>Static factories on the type and doc.Create.New… methods that return it.</summary>
        private static List<string> Creation(Type type)
        {
            var result = new List<string>();
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => Returns(m.ReturnType, type)))
                result.Add(type.Name + "." + m.Name + "(" + Params(m) + ")");

            foreach (var factory in Types.Where(t => t.Namespace == "Autodesk.Revit.Creation"))
            {
                string prefix = factory.Name == "Document" || factory.Name == "ItemFactoryBase" ? "doc.Create."
                              : factory.Name == "FamilyItemFactory" ? "familyDoc.FamilyCreate."
                              : factory.Name == "Application" ? "app.Create." : factory.Name + ".";
                foreach (var m in factory.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => m.Name.StartsWith("New") && Returns(m.ReturnType, type)))
                    result.Add(prefix + m.Name + "(" + Params(m) + ")");
            }
            return result.Distinct().Take(25).ToList();
        }

        private static bool Returns(Type returnType, Type type)
        {
            if (returnType == type) return true;
            if (returnType.IsGenericType && returnType.GetGenericArguments().Any(a => a == type)) return true;
            return false;
        }

        private static List<string> FindEnumValues(string valueName)
        {
            string bare = valueName.Contains('.') ? valueName.Substring(valueName.LastIndexOf('.') + 1) : valueName;
            return Types.Where(t => t.IsEnum)
                .SelectMany(t => Enum.GetNames(t)
                    .Where(n => n.IndexOf(bare, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(n => t.Name + "." + n))
                .Take(20).ToList();
        }

        private static string Params(MethodBase m) =>
            string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name));

        private static string Short(Type t)
        {
            if (t.IsGenericType)
            {
                string name = t.Name.Substring(0, t.Name.IndexOf('`'));
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Short)) + ">";
            }
            if (t.IsByRef) return "ref " + Short(t.GetElementType());
            if (t.IsArray) return Short(t.GetElementType()) + "[]";
            return t.Name;
        }

        /// <summary>Names containing the query first, then the smallest edit distance.</summary>
        private static List<string> Closest(IEnumerable<string> names, string query, int count)
        {
            if (string.IsNullOrEmpty(query)) return new List<string>();
            string q = query.ToLowerInvariant();
            return names
                .Select(n => new { n, contains = n.ToLowerInvariant().Contains(q), d = Distance(n.ToLowerInvariant(), q) })
                .OrderByDescending(x => x.contains).ThenBy(x => x.d)
                .Take(count).Select(x => x.n).ToList();
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
