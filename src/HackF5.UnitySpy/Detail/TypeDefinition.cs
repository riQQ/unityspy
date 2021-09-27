namespace HackF5.UnitySpy.Detail
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using HackF5.UnitySpy.Util;
    using JetBrains.Annotations;

    /// <summary>
    /// Represents an unmanaged _MonoClass instance in a Mono process. This object describes the type of a class or
    /// struct. The .NET equivalent is <see cref="System.Type"/>.
    /// See: _MonoClass in https://github.com/Unity-Technologies/mono/blob/unity-master/mono/metadata/class-internals.h.
    /// </summary>
    [PublicAPI]
    [DebuggerDisplay("Class: {" + nameof(TypeDefinition.Name) + "}")]
    public class TypeDefinition : MemoryObject, ITypeDefinition
    {
        private readonly uint bitFields;

        private readonly ConcurrentDictionary<(string @class, string name), FieldDefinition> fieldCache =
            new ConcurrentDictionary<(string @class, string name), FieldDefinition>();

        private readonly int fieldCount;

        private readonly Lazy<IReadOnlyList<FieldDefinition>> lazyFields;

        private readonly Lazy<string> lazyFullName;

        private readonly Lazy<TypeDefinition> lazyNestedIn;

        private readonly Lazy<TypeDefinition> lazyParent;

        public TypeDefinition([NotNull] AssemblyImage image, long address)
            : base(image, address)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            this.lazyFullName = new Lazy<string>(this.GetFullName);
            this.lazyFields = new Lazy<IReadOnlyList<FieldDefinition>>(this.GetFields);

            //byte[] bytes = this.ReadByteArray(0, 200);

            int imageOffset = 0;
            //for (int i = 0; i < bytes.Length - Constants.SizeOfPtr; i += Constants.SizeOfPtr)
            //{
            //    uint pointer = BitConverter.ToUInt32(bytes, i);
            //    if (pointer == image.Address)
            //    {
            //        imageOffset = i;
            //        break;
            //    }
            //}
            imageOffset = 0x40;
            uint thisArgOffset = 0;
            //for (int i = imageOffset; i < bytes.Length - Constants.SizeOfPtr; i += Constants.SizeOfPtr)
            //{
            //    var pointer = BitConverter.ToInt64(bytes, i);
            //    if (pointer == this.Address)
            //    {
            //        thisArgOffset = (uint)i;
            //        break;
            //    }
            //}
            thisArgOffset = 168;
            // thisArgOffset + sizeof(_MonoType)
            this.TypeInfo = new TypeInfo(image, this.Address + thisArgOffset + 3 * Constants.SizeOfPtr);
            var castClass = this.ReadPtr(Constants.SizeOfPtr);
            var superTypes = this.ReadPtr(2 * Constants.SizeOfPtr);
            var idepth = this.ReadInt32(3 * Constants.SizeOfPtr);
            var rank = this.ReadByte(3 * Constants.SizeOfPtr + 2);
            this.InstanceSize = this.ReadUInt32(3 * Constants.SizeOfPtr + 4);
            var flagsInited = this.ReadUInt32(3 * Constants.SizeOfPtr + 2 * 4);
            bool isSizeInited = Convert.ToBoolean(flagsInited & 0b00000001);
            bool isValueType = Convert.ToBoolean(flagsInited & 0b00000010);
            var minAlign = this.ReadUInt32(3 * Constants.SizeOfPtr + 3 * 4);
            var flagsPackingsize = this.ReadUInt32(3 * Constants.SizeOfPtr + 4 * 4);
            var isDelegate = flagsPackingsize & 0b00000001;
            var flageBytes = this.ReadUInt32(29);
            // var classKind = (MonoTypeKind)((flageBytes & 0b00000000_00000000_00000001_11000000) >> 6);
            this.ClassKind = (MonoTypeKind)((flagsPackingsize & 0b00000000_00000111_00000000_00000000) >> 16);
            var imageAddress = this.ReadUInt32(imageOffset);
            bool isClass = imageAddress == image.Address;

            this.bitFields = this.ReadUInt32(3 * Constants.SizeOfPtr + 2 * 4);
            this.fieldCount = this.ReadInt32(MonoLibraryOffsets.TypeDefinitionFieldCount);
            this.lazyParent = new Lazy<TypeDefinition>(() => this.GetClassDefinition(3 * Constants.SizeOfPtr + 4 * 4 + (4 + 4 /*Padding*/)));
            this.lazyNestedIn = new Lazy<TypeDefinition>(() => this.GetClassDefinition(4 * Constants.SizeOfPtr + 4 * 4 + (4 + 4 /*Padding*/)));

            byte flags2 = this.ReadByte((uint)imageOffset - 10);
            byte flags = this.ReadByte((uint)imageOffset - 9);
            // 44
            try
            {
                this.Name = this.ReadString((uint)imageOffset + Constants.SizeOfPtr);
            }
            catch (Exception ex)
            {
                return;
            }

            if (string.IsNullOrEmpty(this.Name))
            {
                throw new Exception("Empty type name");
            }

            this.NamespaceName = this.ReadString((uint)imageOffset + 2 * Constants.SizeOfPtr);
            // typedef token
            uint typeToken = this.ReadUInt32((uint)imageOffset + 3 * Constants.SizeOfPtr);
            uint vtableSize = this.ReadUInt32((uint)imageOffset + 3 * Constants.SizeOfPtr + 4);

            //this.Size = this.ReadInt32(MonoLibraryOffsets.TypeDefinitionSize);
            this.Size = this.ReadInt32(imageOffset + 3 * Constants.SizeOfPtr + 6 * 4 + 4 * Constants.SizeOfPtr);
            uint runtimeInfo = thisArgOffset + 2 * (3 * Constants.SizeOfPtr) + Constants.SizeOfPtr;
            var vtablePtr = this.ReadPtr(runtimeInfo);
            this.VTable = vtablePtr == Constants.NullPtr ? Constants.NullPtr : image.Process.ReadPtr(vtablePtr + MonoLibraryOffsets.TypeDefinitionRuntimeInfoDomainVtables);

        }

        IReadOnlyList<IFieldDefinition> ITypeDefinition.Fields => this.Fields;

        public string FullName => this.lazyFullName.Value;

        public bool IsInited => (this.bitFields & 0x1) == 0x1;

        public bool IsSizeInited => (this.bitFields & 0x2) == 0x2;

        public bool IsEnum => (this.bitFields & 0x8) == 0x8;

        public bool IsValueType => (this.bitFields & 0x4) == 0x4;

        public string Name { get; }

        public string NamespaceName { get; }

        ITypeInfo ITypeDefinition.TypeInfo => this.TypeInfo;

        public IReadOnlyList<FieldDefinition> Fields => this.lazyFields.Value;

        public TypeDefinition NestedIn => this.lazyNestedIn.Value;

        public TypeDefinition Parent => this.lazyParent.Value;

        public int Size { get; }

        public TypeInfo TypeInfo { get; }

        public long VTable { get; }

        public MonoTypeKind ClassKind { get; set; }

        public uint InstanceSize { get; }

        public dynamic this[string fieldName] => this.GetStaticValue<dynamic>(fieldName);

        IFieldDefinition ITypeDefinition.GetField(string fieldName, string typeFullName) =>
            this.GetField(fieldName, typeFullName);

        public TValue GetStaticValue<TValue>(string fieldName)
        {
            var field = this.GetField(fieldName, this.FullName)
                ?? throw new ArgumentException(
                    $"Field '{fieldName}' does not exist in class '{this.FullName}'.",
                    nameof(fieldName));

            if (!field.TypeInfo.IsStatic)
            {
                throw new InvalidOperationException($"Field '{fieldName}' is not static in class '{this.FullName}'.");
            }

            if (field.TypeInfo.IsConstant)
            {
                throw new InvalidOperationException($"Field '{fieldName}' is constant in class '{this.FullName}'.");
            }

            return field.GetValue<TValue>(this.Process.ReadPtr(this.VTable + 0xc));
        }

        public FieldDefinition GetField(string fieldName, string typeFullName = default) =>
            this.fieldCache.GetOrAdd(
                (typeFullName, fieldName),
                k => this.Fields
                    .FirstOrDefault(
                        f => (f.Name == k.name) && ((k.@class == default) || (k.@class == f.DeclaringType.FullName))));

        public void Init()
        {
            this.NestedIn?.Init();
            this.Parent?.Init();
        }

        private TypeDefinition GetClassDefinition(uint address) =>
            this.Image.GetTypeDefinition(this.ReadPtr(address));

        private IReadOnlyList<FieldDefinition> GetFields()
        {
            var firstField = this.ReadPtr(MonoLibraryOffsets.TypeDefinitionFields);
            if (firstField == Constants.NullPtr)
            {
                return this.Parent?.Fields ?? Array.Empty<FieldDefinition>();
            }

            var fields = new List<FieldDefinition>();
            FieldDefinition fieldDefinition = null;
            for (var fieldIndex = 0u; fieldIndex < this.fieldCount; fieldIndex++)
            {
                var field = firstField + (fieldIndex * (Constants.SizeOfPtr * 4));
                if (this.Process.ReadPtr(field) == Constants.NullPtr)
                {
                    break;
                }

                var oldOffset = fieldDefinition?.Offset ?? -1;
                try
                {
                    fieldDefinition = new FieldDefinition(this, field);
                }
                catch (Exception exception)
                {
                    break;
                }

                var fieldClass = fieldDefinition.TypeInfo.GetClass();

                // var isValueType = fieldDefinition.TypeInfo.TypeCode == TypeCode.VALUETYPE;
                // var instSize = fieldClass.InstanceSize;

                fields.Add(fieldDefinition);
            }

            fields.AddRange(this.Parent?.Fields ?? Array.Empty<FieldDefinition>());

            return new ReadOnlyCollection<FieldDefinition>(fields.OrderBy(f => f.Name).ToArray());
        }

        private string GetFullName()
        {
            var builder = new StringBuilder();

            var hierarchy = this.NestedHierarchy().Reverse().ToArray();
            string topLevelNamespace = hierarchy[0].NamespaceName;
            if (!string.IsNullOrWhiteSpace(topLevelNamespace))
            {
                builder.Append($"{topLevelNamespace}.");
            }

            foreach (var definition in hierarchy)
            {
                builder.Append($"{definition.Name}+");
            }

            return builder.ToString().TrimEnd('+');
        }

        private IEnumerable<TypeDefinition> NestedHierarchy()
        {
            yield return this;

            var nested = this.NestedIn;
            while (nested != default)
            {
                yield return nested;

                nested = nested.NestedIn;
            }
        }
    }

    public enum MonoTypeKind
    {
        MONO_CLASS_DEF = 1, /* non-generic type */
        MONO_CLASS_GTD, /* generic type definition */
        MONO_CLASS_GINST, /* generic instantiation */
        MONO_CLASS_GPARAM, /* generic parameter */
        MONO_CLASS_ARRAY, /* vector or array, bounded or not */
        MONO_CLASS_POINTER, /* pointer of function pointer*/
    }
}