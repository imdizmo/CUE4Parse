using System;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.StaticMesh;

public class FMeshDescriptionBulkData
{
    public FByteBulkData? LegacyData;
    public FEditorBulkData? EditorData;
    public FGuid Guid;
    public bool bGuidIsHash;
    public readonly bool bIsRawMesh;
    private readonly VersionContainer _versions;

    private static readonly FGuid EnterpriseGuid = new(0x9DFFBCD6, 0x494F0158, 0xE2211282, 0x3C92A888);
    private const int MeshDescriptionBulkDataGuidIsHash = 8;

    public FMeshDescriptionBulkData(FAssetArchive Ar, bool rawMesh = false)
    {
        _versions = (VersionContainer) Ar.Versions.Clone();
        _versions.CustomVersions ??= Ar.Owner?.Summary?.CustomVersionContainer;
        bIsRawMesh = rawMesh;
        if (rawMesh)
        {
            LegacyData = new FByteBulkData(Ar);
            Guid = Ar.Read<FGuid>();
            bGuidIsHash = Ar.ReadBoolean();
            return;
        }

        var ue5 = Ar.Ver<FUE5MainStreamObjectVersion.Type>(FUE5MainStreamObjectVersion.GUID);
        if (ue5 < FUE5MainStreamObjectVersion.Type.VirtualizedBulkDataHaveUniqueGuids &&
            ue5 < FUE5MainStreamObjectVersion.Type.MeshDescriptionVirtualization)
            LegacyData = new FByteBulkData(Ar);
        else
            EditorData = new FEditorBulkData(Ar);

        if (Ar.Ver<FEditorObjectVersion.Type>(FEditorObjectVersion.GUID) >= FEditorObjectVersion.Type.MeshDescriptionBulkDataGuid)
            Guid = Ar.Read<FGuid>();
        if (Ar.CustomVer(EnterpriseGuid) >= MeshDescriptionBulkDataGuidIsHash)
            bGuidIsHash = Ar.ReadBoolean();
    }

    public byte[] GetPayload() => EditorData?.Payload?.Decompress() ?? LegacyData?.Data ?? [];

    public FMeshDescription? ReadMeshDescription()
    {
        var payload = GetPayload();
        if (payload.Length == 0) return null;
        using var Ar = new FByteArchive("MeshDescription", payload, _versions);
        return bIsRawMesh ? FMeshDescription.FromRawMesh(Ar) : new FMeshDescription(Ar);
    }
}

public class UMeshDescriptionBaseBulkData : UObject
{
    public FMeshDescriptionBulkData? BulkData;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);
        if (!Ar.IsFilterEditorOnly && Ar.Position < validPos)
            BulkData = new FMeshDescriptionBulkData(Ar);
    }
}

public class UStaticMeshDescriptionBulkData : UMeshDescriptionBaseBulkData;
