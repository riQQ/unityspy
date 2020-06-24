namespace HackF5.UnitySpy.Detail
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using HackF5.UnitySpy.Util;
    using JetBrains.Annotations;

    /// <summary>
    /// Represents an unmanaged _MonoImage instance in a Mono process. This object describes a managed assembly.
    /// The .NET equivalent is <see cref="System.Reflection.Assembly"/>.
    /// See: _MonoImage in https://github.com/Unity-Technologies/mono/blob/unity-master/mono/metadata/metadata-internals.h.
    /// </summary>
    [PublicAPI]
    public class AssemblyImage : MemoryObject, IAssemblyImage
    {
        private readonly Dictionary<string, TypeDefinition> typeDefinitionsByFullName =
            new Dictionary<string, TypeDefinition>();

        private readonly ConcurrentDictionary<uint, TypeDefinition> typeDefinitionsByAddress;

        public AssemblyImage(ProcessFacade process, uint address, uint assemblyAddress)
            : base(null, address)
        {
            this.Process = process;
            byte[] bytes = process.ReadByteArray(address, 3000);

            int assemblyOffset = 0;
            for (int i = 0; i < 3000 - 4; i++)
            {
                uint pointerA = BitConverter.ToUInt32(bytes, i);
                if (pointerA == assemblyAddress)
                {
                    assemblyOffset = i;
                    break;
                }
            }

            int offsetTest = 8;
            int raw_data = 8;
            int raw_data_len = 12;
            int nameOffset = 20;
            int fileNameOffset = 24;
            int moduleNameOffset = 28;
            int versionOffset = 32;
            int guidOffset = 40;
            int rawMetaData = 52;
            int headerStrings = 56; // 8 groß

            uint addr = address + (uint)headerStrings;
            uint pointer = BitConverter.ToUInt32(bytes, 52);
            this.typeDefinitionsByAddress = this.CreateTypeDefinitions((uint)assemblyOffset + 8);

            foreach (var definition in this.TypeDefinitions)
            {
                definition.Init();
            }

            var typDefs = this.TypeDefinitions.Select(x => x.FullName).ToArray();

            foreach (var definition in this.TypeDefinitions)
            {
                if (definition.FullName.Contains("`") || definition.FullName.Contains(">"))
                {
                    // ignore generic classes as they have name clashes. in order to make them unique these it would be
                    // necessary to examine the information held in TypeInfo.Data. see
                    // ProcessFacade.ReadManagedGenericObject for moral support.
                    continue;
                }

                this.typeDefinitionsByFullName.Add(definition.FullName, definition);
            }
        }

        IEnumerable<ITypeDefinition> IAssemblyImage.TypeDefinitions => this.TypeDefinitions;

        public IEnumerable<TypeDefinition> TypeDefinitions =>
            this.typeDefinitionsByAddress.ToArray().Select(k => k.Value);

        public override AssemblyImage Image => this;

        public override ProcessFacade Process { get; }

        public dynamic this[string fullTypeName] => this.GetTypeDefinition(fullTypeName);

        ITypeDefinition IAssemblyImage.GetTypeDefinition(string fullTypeName) => this.GetTypeDefinition(fullTypeName);

        public TypeDefinition GetTypeDefinition(string fullTypeName) =>
            this.typeDefinitionsByFullName.TryGetValue(fullTypeName, out var d) ? d : default;

        public TypeDefinition GetTypeDefinition(uint address)
        {
            if (address == Constants.NullPtr)
            {
                return default;
            }

            return this.typeDefinitionsByAddress.GetOrAdd(
                address,
                key => new TypeDefinition(this, key));
        }

        private ConcurrentDictionary<uint, TypeDefinition> CreateTypeDefinitions(uint classCacheOffset)
        {
            var definitions = new ConcurrentDictionary<uint, TypeDefinition>();
            const uint classCache = 0x2a0u;
            // struct _MonoInternalHashTable / mono\utils\mono-internal-hash.h
            var classCacheSize = this.ReadUInt32(classCacheOffset + 0xc);
            var entries = this.ReadUInt32(classCacheOffset + 0x10);
            var classCacheTableArray = this.ReadPtr(classCacheOffset + 0x14);

            for (var tableItem = 0u;
                tableItem < (classCacheSize * Constants.SizeOfPtr);
                tableItem += Constants.SizeOfPtr)
            {
                TypeDefinition lastTypeDef = null;
                for (var definition = this.Process.ReadPtr(classCacheTableArray + tableItem);
                    definition != Constants.NullPtr;
                    definition = this.Process.ReadPtr(definition + MonoLibraryOffsets.TypeDefinitionNextClassCache + (lastTypeDef?.ClassKind == MonoTypeKind.MONO_CLASS_GTD ? 16u : 0u)))
                {
                    try
                    {
                        lastTypeDef = new TypeDefinition(this, definition);
                        definitions.GetOrAdd(definition, lastTypeDef);
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }

            return definitions;
        }

        private void GetAddress()
        {

        }
    }
}