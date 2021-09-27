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

        private readonly ConcurrentDictionary<long, TypeDefinition> typeDefinitionsByAddress;

        public AssemblyImage(ProcessFacade process, long address, long assemblyAddress)
            : base(null, address)
        {
            this.Process = process;
            byte[] bytes = process.ReadByteArray(address, 3000);

            int assemblyOffset = 0;
            for (int i = 0; i < 3000 - 4; i += Constants.SizeOfPtr)
            {
                long pointerA = BitConverter.ToInt64(bytes, i);
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

            long addr = address + headerStrings;
            long pointer = BitConverter.ToInt64(bytes, 52);

            // Members
            // ...
            // MonoAssembly *assembly;
            // GHashTable* method_cache;
            // MonoInternalHashTable class_cache;
            // ...
            this.typeDefinitionsByAddress = this.CreateTypeDefinitions(assemblyOffset + 2 * Constants.SizeOfPtr);

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

        public TypeDefinition GetTypeDefinition(long address)
        {
            if (address == Constants.NullPtr)
            {
                return default;
            }

            return this.typeDefinitionsByAddress.GetOrAdd(
                address,
                key => new TypeDefinition(this, key));
        }

        private ConcurrentDictionary<long, TypeDefinition> CreateTypeDefinitions(long classCacheOffset)
        {
            var definitions = new ConcurrentDictionary<long, TypeDefinition>();
            const uint classCache = 0x2a0u;
            // struct _MonoInternalHashTable / mono\utils\mono-internal-hash.h
            var classCacheAddress = this.Address + classCacheOffset;
            var classCacheSize = this.ReadInt32(classCacheOffset + 3 * Constants.SizeOfPtr);
            var entries = this.ReadUInt32(classCacheOffset + 3 * Constants.SizeOfPtr + 4);
            var classCacheTableArray = this.ReadPtr(classCacheOffset + 3 * Constants.SizeOfPtr + 2 * 4);

            for (var tableItem = 0u;
                tableItem < (classCacheSize * Constants.SizeOfPtr);
                tableItem += Constants.SizeOfPtr)
            {
                TypeDefinition lastTypeDef = null;
                for (var definition = this.Process.ReadPtr(classCacheTableArray + tableItem);
                    definition != Constants.NullPtr;
                    definition = this.Process.ReadPtr(definition + MonoLibraryOffsets.TypeDefinitionNextClassCache /*+ (lastTypeDef?.ClassKind == MonoTypeKind.MONO_CLASS_GTD ? 4 * Constants.SizeOfPtr : 0u)*/))
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