using System.Linq;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.Animation;

public class FRawAnimSequenceTrack : IUStruct
{
    public readonly FVector[] PosKeys;
    public readonly FQuat[] RotKeys;
    public readonly FVector[] ScaleKeys;
    public readonly float[] KeyTimes;

    public FRawAnimSequenceTrack(FStructFallback fallback)
    {
        PosKeys = Vectors(fallback, nameof(PosKeys));
        RotKeys = Keys<FQuat>(fallback, nameof(RotKeys));
        ScaleKeys = Vectors(fallback, nameof(ScaleKeys));
        KeyTimes = [];
    }

    public static bool IsTagged(FArchive Ar)
    {
        if (Ar is not FAssetArchive { HasUnversionedProperties: false })
            return false;
        var position = Ar.Position;
        try
        {
            return Ar.ReadFName().Text == nameof(PosKeys);
        }
        catch
        {
            return false;
        }
        finally
        {
            Ar.Position = position;
        }
    }

    private static FVector[] Vectors(FStructFallback fallback, string name) =>
        fallback.TryGetValue(out UScriptArray array, name)
            ? array.Properties.Select(p => p.GenericValue switch
            {
                FScriptStruct { StructType: FVector v } => v,
                FScriptStruct { StructType: TIntVector3<float> f } => new FVector(f.X, f.Y, f.Z),
                _ => default,
            }).ToArray()
            : [];

    private static T[] Keys<T>(FStructFallback fallback, string name) where T : struct =>
        fallback.TryGetValue(out UScriptArray array, name)
            ? array.Properties.Select(p => p.GenericValue is FScriptStruct { StructType: T value } ? value : default).ToArray()
            : [];

    public FRawAnimSequenceTrack(FArchive Ar)
    {
        PosKeys = Ar.ReadBulkArray<FVector>();
        RotKeys = Ar.ReadBulkArray<FQuat>();
        ScaleKeys = Ar.Ver >= EUnrealEngineObjectUE4Version.ANIM_SUPPORT_NONUNIFORM_SCALE_ANIMATION ? Ar.ReadBulkArray<FVector>() : [];
        KeyTimes = Ar.Ver < EUnrealEngineObjectUE3Version.RAW_ANIMDATA_REDUX ? Ar.ReadBulkArray<float>() : [];
    }
}
