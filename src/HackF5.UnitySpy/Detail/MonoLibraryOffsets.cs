// ReSharper disable IdentifierTypo
namespace HackF5.UnitySpy.Detail
{
    internal static class MonoLibraryOffsets
    {
        public const uint AssemblyImage = 0x60;

        public const uint TypeDefinitionBitFields = 0x13;

        public const uint TypeDefinitionParent = 0x20;

        public const uint TypeDefinitionNestedIn = 0x24;

        public const uint TypeDefinitionImage = 0x30;

        public const uint TypeDefinitionName = 0x34;

        public const uint TypeDefinitionNamespace = 0x38;

        public const uint TypeDefinitionFields = 0x98;

        public const uint TypeDefinitionByValArg = 0x74;

        // from MonoClassDef
        public const uint TypeDefinitionFieldCount = 0x100;

        // from MonoClassDef
        public const uint TypeDefinitionNextClassCache = 0x108;

        public const uint TypeDefinitionRuntimeInfo = 0xa8;

        public const uint TypeDefinitionSize = 0x5c;

        public const uint TypeDefinitionRuntimeInfoDomainVtables = 0x4;

        // as
        public const uint ReferencedAssemblies = 0x70;
   }
}