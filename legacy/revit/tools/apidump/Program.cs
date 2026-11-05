using System.Reflection;

var revitDir = @"C:\Program Files\Autodesk\Revit 2025";
var paths = Directory.GetFiles(revitDir, "*.dll").ToList();
// add runtime refs from the running host so core types resolve
var rtDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
paths.AddRange(Directory.GetFiles(rtDir, "*.dll"));

var resolver = new PathAssemblyResolver(paths.GroupBy(Path.GetFileName).Select(g => g.First()));
using var mlc = new MetadataLoadContext(resolver);
var api = mlc.LoadFromAssemblyPath(Path.Combine(revitDir, "RevitAPI.dll"));
var apiui = mlc.LoadFromAssemblyPath(Path.Combine(revitDir, "RevitAPIUI.dll"));

string N(Type t) => t.IsGenericType
    ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(N)) + ">"
    : (t.IsArray ? N(t.GetElementType()!) + "[]" : t.Name);

foreach (var name in args)
{
    var t = api.GetTypes().Concat(apiui.GetTypes()).FirstOrDefault(x => x.Name == name);
    if (t == null) { Console.WriteLine($"!! {name} not found"); continue; }
    Console.WriteLine($"\n===== {(t.IsInterface ? "interface" : t.IsEnum ? "enum" : "class")} {t.FullName} =====");
    if (t.IsEnum) { Console.WriteLine("  " + string.Join(", ", t.GetFields().Where(f => f.IsLiteral).Select(f => f.Name))); continue; }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  prop {N(p.PropertyType)} {p.Name}{(p.CanRead ? " get" : "")}{(p.CanWrite ? " set" : "")}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
        Console.WriteLine($"  {N(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})");
}
