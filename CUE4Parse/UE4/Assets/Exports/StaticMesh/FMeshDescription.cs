using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.StaticMesh;

public class FMeshDescription
{
    public float[] Positions = [];
    public int[] VertexInstanceVertex = [];
    public float[] Normals = [];
    public float[] Tangents = [];
    public float[] BinormalSigns = [];
    public float[] Colors = [];
    public List<float[]> UVs = [];
    public int[] Triangles = [];
    public int[] TriangleGroups = [];
    public string[] GroupMaterialSlots = [];

    private const int MaxElements = 1 << 24;

    private enum EType : uint { Vector4f, Vector3f, Vector2f, Float, Int32, Bool, Name, Transform }

    private sealed class Attr
    {
        public EType Type;
        public int Extent = 1;
        public readonly List<Array> Channels = [];
    }

    private sealed class Container
    {
        public int Count;
        public bool[] Allocated = [];
        public readonly Dictionary<string, Attr> Attributes = new(StringComparer.OrdinalIgnoreCase);
    }

    private FMeshDescription() { }

    public static FMeshDescription FromRawMesh(FArchive Ar)
    {
        Ar.Read<int>();
        Ar.Read<int>();
        var faceMaterials = Ar.ReadArray<int>(Count(Ar));
        Skip(Ar, 4L);
        var positions = Ar.ReadArray<float>(3 * Count(Ar));
        var wedges = Ar.ReadArray<int>(Count(Ar));
        Skip(Ar, 12L);
        Skip(Ar, 12L);
        var normals = Ar.ReadArray<float>(3 * Count(Ar));
        var uvs = new List<float[]>();
        for (var i = 0; i < 8; i++)
        {
            var uv = Ar.ReadArray<float>(2 * Count(Ar));
            if (uv.Length == 2 * wedges.Length) uvs.Add(uv);
        }

        var faces = Math.Min(faceMaterials.Length, wedges.Length / 3);
        if (faceMaterials.Any(m => m < 0 || m >= 4096))
            throw new ParserException(Ar, "Mesh description: bad face material index");
        var groups = faceMaterials.Length == 0 ? 0 : faceMaterials.Max() + 1;
        return new FMeshDescription
        {
            Positions = positions,
            VertexInstanceVertex = wedges,
            Normals = normals.Length == 3 * wedges.Length ? normals : [],
            UVs = uvs,
            Triangles = Enumerable.Range(0, 3 * faces).ToArray(),
            TriangleGroups = faceMaterials[..faces],
            GroupMaterialSlots = Enumerable.Repeat("", groups).ToArray(),
        };
    }

    public FMeshDescription(FArchive Ar)
    {
        var release = Ar.Ver<FReleaseObjectVersion.Type>(FReleaseObjectVersion.GUID);
        var ue5 = Ar.Ver<FUE5MainStreamObjectVersion.Type>(FUE5MainStreamObjectVersion.GUID);
        bool newFormat = release == FReleaseObjectVersion.Type.MeshDescriptionNewFormat ||
                         ue5 >= FUE5MainStreamObjectVersion.Type.MeshDescriptionNewFormat;
        if (newFormat)
            ReadNew(Ar);
        else
            ReadLegacy(Ar);
    }

    private static int Count(FArchive Ar, int minSize = 1)
    {
        var n = Ar.Read<int>();
        if (n < 0 || n > MaxElements || (long) n * minSize > Ar.Length - Ar.Position)
            throw new ParserException(Ar, $"Mesh description: bad count {n}");
        return n;
    }

    private static bool[] BitArray(FArchive Ar)
    {
        var bits = Ar.Read<int>();
        if (bits < 0 || bits > MaxElements || (bits + 31L) / 32 * 4 > Ar.Length - Ar.Position)
            throw new ParserException(Ar, $"Mesh description: bad bit count {bits}");
        var words = Ar.ReadArray<uint>((bits + 31) / 32);
        var result = new bool[bits];
        for (var i = 0; i < bits; i++) result[i] = (words[i >> 5] & (1u << (i & 31))) != 0;
        return result;
    }

    private static int Size(EType t) => t switch
    {
        EType.Vector4f => 16, EType.Vector3f => 12, EType.Vector2f => 8, EType.Float or EType.Int32 => 4, EType.Bool => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static void SkipValue(FArchive Ar, EType t)
    {
        switch (t)
        {
            case EType.Bool: Ar.Position += 4; break;
            case EType.Name: Ar.ReadFString(); break;
            case EType.Transform: Ar.Position += 80; break;
            default: Ar.Position += Size(t); break;
        }
    }

    private static Array Values(FArchive Ar, EType t, int n, bool bulk)
    {
        if (t == EType.Name)
        {
            var names = new string[n];
            for (var i = 0; i < n; i++) names[i] = Ar.ReadFString();
            return names;
        }

        if (t == EType.Transform)
        {
            Ar.Position += 80L * n;
            return Array.Empty<float>();
        }

        if (t == EType.Bool)
        {
            var b = new int[n];
            for (var i = 0; i < n; i++) b[i] = bulk ? Ar.Read<byte>() : Ar.Read<int>();
            return b;
        }

        var components = Size(t) / 4;
        return t == EType.Int32 ? Ar.ReadArray<int>(n) : Ar.ReadArray<float>(n * components);
    }

    private static Array AttributeArray(FArchive Ar, EType t, bool hasExtent, ref int extent)
    {
        if (hasExtent) extent = Ar.Read<int>();
        var release = Ar.Ver<FReleaseObjectVersion.Type>(FReleaseObjectVersion.GUID);
        if (t == EType.Name && Ar.Ver<FFortniteMainBranchObjectVersion.Type>(FFortniteMainBranchObjectVersion.GUID) >= FFortniteMainBranchObjectVersion.Type.DynamicMeshAttributesTriangleLabels)
            return CompactNames(Ar);
        if (t is EType.Name)
            return Values(Ar, t, Count(Ar), false);
        if (t is EType.Transform)
        {
            if (Ar.Ver<FFortniteMainBranchObjectVersion.Type>(FFortniteMainBranchObjectVersion.GUID) < FFortniteMainBranchObjectVersion.Type.MeshDescriptionForSkeletalMesh)
            {
                Ar.Read<int>();
                if (Count(Ar) != 0) throw new ParserException(Ar, "Mesh description: bulk FTransform attribute with data");
                return Array.Empty<float>();
            }

            return Values(Ar, t, Count(Ar), false);
        }

        if (release < FReleaseObjectVersion.Type.MeshDescriptionNewSerialization)
            return Values(Ar, t, Count(Ar), false);

        var elementSize = Ar.Read<int>();
        if (elementSize != Size(t)) throw new ParserException(Ar, $"Mesh description: element size {elementSize} for {t}");
        return Values(Ar, t, Count(Ar), true);
    }

    private static string[] CompactNames(FArchive Ar)
    {
        var num = Count(Ar, 0);
        if (num == 0) return [];
        var unique = new string[Count(Ar, 4)];
        for (var i = 0; i < unique.Length; i++) unique[i] = Ar.ReadFString();
        if (unique.Length == 1) return Enumerable.Repeat(unique[0], num).ToArray();
        if (unique.Length == num) return unique;
        var size = Ar.Read<byte>();
        var names = new string[num];
        for (var i = 0; i < num; i++)
        {
            var index = size switch { 0 => Ar.Read<byte>(), 1 => Ar.Read<ushort>(), _ => (int) Ar.Read<uint>() };
            names[i] = index < unique.Length ? unique[index] : "None";
        }

        return names;
    }

    private static Attr AttributeSet(FArchive Ar, EType t, int extent, bool hasExtent)
    {
        var attr = new Attr { Type = t, Extent = Math.Max(1, extent) };
        Ar.Read<int>();
        var channels = Count(Ar);
        for (var c = 0; c < channels; c++)
        {
            if (hasExtent && extent == 0)
            {
                SkipUnbounded(Ar, t);
                continue;
            }

            var e = attr.Extent;
            attr.Channels.Add(AttributeArray(Ar, t, hasExtent, ref e));
            attr.Extent = Math.Max(1, e);
        }

        if (hasExtent && extent == 0)
        {
            SkipValue(Ar, t);
            Ar.Read<uint>();
            return attr;
        }

        SkipValue(Ar, t);
        Ar.Read<uint>();
        return attr;
    }

    private static void SkipUnbounded(FArchive Ar, EType t)
    {
        var chunks = Count(Ar);
        for (var i = 0; i < chunks; i++)
        {
            Values(Ar, t, Count(Ar), false);
            var n = Count(Ar);
            Ar.Position += 3L * 4 * n;
        }

        Ar.Read<int>();
        SkipValue(Ar, t);
    }

    private static void AttributesSet(FArchive Ar, Dictionary<string, Attr> into, bool newFormat)
    {
        if (Ar.Ver<FEditorObjectVersion.Type>(FEditorObjectVersion.GUID) < FEditorObjectVersion.Type.MeshDescriptionNewAttributeFormat)
        {
            var types = Ar.Read<int>();
            if (types != 7) throw new ParserException(Ar, $"Mesh description: {types} legacy attribute types");
            for (var t = 0; t < 7; t++)
            {
                Ar.Read<int>();
                var n = Count(Ar);
                for (var i = 0; i < n; i++)
                {
                    var name = Ar.ReadFString().Trim();
                    into[name] = AttributeSet(Ar, (EType) t, 1, false);
                }
            }

            return;
        }

        Ar.Read<int>();
        var entries = Count(Ar);
        for (var i = 0; i < entries; i++)
        {
            var name = Ar.ReadFString().Trim();
            var type = Ar.Read<uint>();
            if (type > (uint) EType.Transform) throw new ParserException(Ar, $"Mesh description: attribute type {type}");
            var extent = newFormat ? Ar.Read<int>() : 1;
            into[name] = AttributeSet(Ar, (EType) type, extent, newFormat);
        }
    }

    private void ReadNew(FArchive Ar)
    {
        var elements = new Dictionary<string, Container>(StringComparer.OrdinalIgnoreCase);
        var n = Count(Ar);
        for (var i = 0; i < n; i++)
        {
            var name = Ar.ReadFString();
            var channels = Count(Ar);
            for (var c = 0; c < channels; c++)
            {
                var container = new Container { Allocated = BitArray(Ar) };
                container.Count = container.Allocated.Length;
                Ar.Read<int>();
                AttributesSet(Ar, container.Attributes, true);
                if (c == 0) elements[name] = container;
            }
        }

        var vertices = elements.GetValueOrDefault("Vertices") ?? throw new ParserException(Ar, "Mesh description: no vertices");
        var instances = elements.GetValueOrDefault("VertexInstances") ?? throw new ParserException(Ar, "Mesh description: no vertex instances");
        var triangles = elements.GetValueOrDefault("Triangles") ?? throw new ParserException(Ar, "Mesh description: no triangles");
        var groups = elements.GetValueOrDefault("PolygonGroups") ?? new Container();
        var polygons = elements.GetValueOrDefault("Polygons") ?? new Container();

        Positions = Floats(vertices, "Position", vertices.Count * 3);
        VertexInstanceVertex = Ints(instances, "VertexIndex");
        TakeInstanceAttributes(instances);
        var triVI = Ints(triangles, "VertexInstanceIndex");
        var triGroup = Ints(triangles, "PolygonGroupIndex");
        var triPoly = Ints(triangles, "PolygonIndex");
        var polyGroup = Ints(polygons, "PolygonGroupIndex");
        var tris = new List<int>();
        var tg = new List<int>();
        for (var t = 0; t < triangles.Count; t++)
        {
            if (!triangles.Allocated[t] || triVI.Length < 3 * t + 3) continue;
            tris.AddRange(triVI.AsSpan(3 * t, 3).ToArray());
            tg.Add(t < triGroup.Length ? triGroup[t] : t < triPoly.Length && triPoly[t] < polyGroup.Length ? polyGroup[triPoly[t]] : 0);
        }

        Triangles = tris.ToArray();
        TriangleGroups = tg.ToArray();
        GroupMaterialSlots = Names(groups, "ImportedMaterialSlotName", groups.Count);
    }

    private void ReadLegacy(FArchive Ar)
    {
        var release = Ar.Ver<FReleaseObjectVersion.Type>(FReleaseObjectVersion.GUID);
        var editor = Ar.Ver<FEditorObjectVersion.Type>(FEditorObjectVersion.GUID);
        bool oldSerialization = release < FReleaseObjectVersion.Type.MeshDescriptionNewSerialization;

        var vertexAlloc = BitArray(Ar);
        foreach (var _ in vertexAlloc.Where(a => a))
            if (oldSerialization) { SkipIntArray(Ar); SkipIntArray(Ar); }

        var instanceAlloc = BitArray(Ar);
        var instanceVertex = new int[instanceAlloc.Length];
        for (var i = 0; i < instanceAlloc.Length; i++)
        {
            if (!instanceAlloc[i]) continue;
            instanceVertex[i] = Ar.Read<int>();
            if (oldSerialization) SkipIntArray(Ar);
        }

        var edgeAlloc = BitArray(Ar);
        foreach (var _ in edgeAlloc.Where(a => a))
        {
            Ar.Position += 8;
            if (oldSerialization) SkipIntArray(Ar);
        }

        var polyAlloc = BitArray(Ar);
        var polyInstances = new int[polyAlloc.Length][];
        var polyGroup = new int[polyAlloc.Length];
        for (var p = 0; p < polyAlloc.Length; p++)
        {
            if (!polyAlloc[p]) continue;
            polyInstances[p] = Ar.ReadArray<int>(Count(Ar));
            if (editor < FEditorObjectVersion.Type.MeshDescriptionRemovedHoles)
            {
                var holes = Count(Ar);
                for (var h = 0; h < holes; h++) SkipIntArray(Ar);
            }

            if (oldSerialization)
            {
                var discarded = Count(Ar);
                Ar.Position += (long) discarded * (editor >= FEditorObjectVersion.Type.MeshDescriptionTriangles ? 16 : 12);
            }

            polyGroup[p] = Ar.Read<int>();
        }

        var groupAlloc = BitArray(Ar);
        foreach (var _ in groupAlloc.Where(a => a))
            if (oldSerialization) SkipIntArray(Ar);

        var vertexAttrs = new Container { Count = vertexAlloc.Length, Allocated = vertexAlloc };
        var instanceAttrs = new Container { Count = instanceAlloc.Length, Allocated = instanceAlloc };
        var groupAttrs = new Container { Count = groupAlloc.Length, Allocated = groupAlloc };
        AttributesSet(Ar, vertexAttrs.Attributes, false);
        AttributesSet(Ar, instanceAttrs.Attributes, false);
        AttributesSet(Ar, new Dictionary<string, Attr>(), false);
        AttributesSet(Ar, new Dictionary<string, Attr>(), false);
        AttributesSet(Ar, groupAttrs.Attributes, false);

        var tris = new List<int>();
        var tg = new List<int>();
        if (editor >= FEditorObjectVersion.Type.MeshDescriptionTriangles)
        {
            var triAlloc = BitArray(Ar);
            for (var t = 0; t < triAlloc.Length; t++)
            {
                if (!triAlloc[t]) continue;
                var a = Ar.Read<int>(); var b = Ar.Read<int>(); var c = Ar.Read<int>();
                var poly = Ar.Read<int>();
                tris.AddRange([a, b, c]);
                tg.Add(poly >= 0 && poly < polyGroup.Length ? polyGroup[poly] : 0);
            }

            AttributesSet(Ar, new Dictionary<string, Attr>(), false);
        }
        else
        {
            for (var p = 0; p < polyAlloc.Length; p++)
            {
                if (!polyAlloc[p] || polyInstances[p].Length < 3) continue;
                for (var k = 1; k + 1 < polyInstances[p].Length; k++)
                {
                    tris.AddRange([polyInstances[p][0], polyInstances[p][k], polyInstances[p][k + 1]]);
                    tg.Add(polyGroup[p]);
                }
            }
        }

        Positions = Floats(vertexAttrs, "Position", vertexAlloc.Length * 3);
        VertexInstanceVertex = instanceVertex;
        TakeInstanceAttributes(instanceAttrs);
        Triangles = tris.ToArray();
        TriangleGroups = tg.ToArray();
        GroupMaterialSlots = Names(groupAttrs, "ImportedMaterialSlotName", groupAlloc.Length);
    }

    private static void SkipIntArray(FArchive Ar) => Skip(Ar, 4L);

    private static void Skip(FArchive Ar, long size)
    {
        var n = Count(Ar);
        Ar.Position += size * n;
    }

    private void TakeInstanceAttributes(Container instances)
    {
        Normals = Floats(instances, "Normal", 0);
        Tangents = Floats(instances, "Tangent", 0);
        BinormalSigns = Floats(instances, "BinormalSign", 0);
        Colors = Floats(instances, "Color", 0);
        UVs = instances.Attributes.TryGetValue("TextureCoordinate", out var uv)
            ? uv.Channels.OfType<float[]>().ToList()
            : [];
    }

    private static float[] Floats(Container c, string name, int fallback) =>
        c.Attributes.TryGetValue(name, out var a) && a.Channels.FirstOrDefault() is float[] f ? f : new float[fallback];

    private static int[] Ints(Container c, string name) =>
        c.Attributes.TryGetValue(name, out var a) && a.Channels.FirstOrDefault() is int[] i ? i : [];

    private static string[] Names(Container c, string name, int count) =>
        c.Attributes.TryGetValue(name, out var a) && a.Channels.FirstOrDefault() is string[] s ? s : Enumerable.Repeat("None", count).ToArray();
}
