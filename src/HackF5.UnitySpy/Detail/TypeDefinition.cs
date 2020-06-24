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

        private readonly int fieldSize;

        private readonly Lazy<IReadOnlyList<FieldDefinition>> lazyFields;

        private readonly Lazy<string> lazyFullName;

        private readonly Lazy<TypeDefinition> lazyNestedIn;

        private readonly Lazy<TypeDefinition> lazyParent;

        public TypeDefinition([NotNull] AssemblyImage image, uint address)
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
            //for (int i = 0; i < bytes.Length - 4; i++)
            //{
            //    uint pointer = BitConverter.ToUInt32(bytes, i);
            //    if (pointer == image.Address)
            //    {
            //        imageOffset = i;
            //        break;
            //    }
            //}
            imageOffset = 40;
            uint thisArgOffset = 0;
            //for (int i = imageOffset; i < bytes.Length - 4; i++)
            //{
            //    uint pointer = BitConverter.ToUInt32(bytes, i);
            //    if (pointer == this.Address)
            //    {
            //        thisArgOffset = (uint)i;
            //        break;
            //    }
            //}
            thisArgOffset = 104;
            this.TypeInfo = new TypeInfo(image, this.Address + thisArgOffset + 12);
            var castClass = this.ReadUInt32(4);
            var superTypes = this.ReadUInt32(8);
            var idepth = this.ReadUInt32(12);
            this.InstanceSize = this.ReadUInt32(16);

            var flageBytes29 = this.ReadByte(29);
            var isDelegate = flageBytes29 & 0b00000001;
            var flageBytes = this.ReadUInt32(29);
            // var classKind = (MonoTypeKind)((flageBytes & 0b00000000_00000000_00000001_11000000) >> 6);
            this.ClassKind = (MonoTypeKind)((flageBytes & 0b00000000_00000000_00000111_00000000) >> 8);
            var imageAddress = this.ReadUInt32(40);
            bool isClass = imageAddress == image.Address;

            this.bitFields = this.ReadUInt32(MonoLibraryOffsets.TypeDefinitionBitFields);
            this.fieldSize = this.ReadInt32(MonoLibraryOffsets.TypeDefinitionFieldCount);
            this.lazyParent = new Lazy<TypeDefinition>(() => this.GetClassDefinition(MonoLibraryOffsets.TypeDefinitionParent));
            this.lazyNestedIn = new Lazy<TypeDefinition>(() => this.GetClassDefinition(MonoLibraryOffsets.TypeDefinitionNestedIn));

            byte flags2 = this.ReadByte((uint)imageOffset - 10);
            byte flags = this.ReadByte((uint)imageOffset - 9);
            // 44
            try
            {
                this.Name = this.ReadString((uint)imageOffset + 4);
            }
            catch (Exception ex)
            {
                return;
            }

            if (string.IsNullOrEmpty(this.Name))
            {
                throw new Exception("Empty type name");
            }

            this.NamespaceName = this.ReadString((uint)imageOffset + 8);
            // typedef token
            uint typeToken = this.ReadUInt32((uint)imageOffset + 12);
            uint vtableSize = this.ReadUInt32((uint)imageOffset + 16);

            this.Size = this.ReadInt32(MonoLibraryOffsets.TypeDefinitionSize);
            uint runtimeInfo = thisArgOffset + 12 + 12 + 4;
            var vtablePtr = this.ReadPtr(runtimeInfo);
            this.VTable = vtablePtr == Constants.NullPtr ? Constants.NullPtr : image.Process.ReadPtr(vtablePtr + MonoLibraryOffsets.TypeDefinitionRuntimeInfoDomainVtables);

        }

        IReadOnlyList<IFieldDefinition> ITypeDefinition.Fields => this.Fields;

        public string FullName => this.lazyFullName.Value;

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

        public uint VTable { get; }

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
            for (var fieldIndex = 0u; (fieldDefinition?.Offset ?? 0) < this.fieldSize; fieldIndex++)
            {
                var field = firstField + (fieldIndex * 0x10);
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

                // TODO Hack, correct way is to determine field size
                // for size see mono function "mono_type_size"
                // value type size = instanceSize - 8 ( sizeof(MonoObject) = 2 * PointerSize)
                if (oldOffset >= fieldDefinition.Offset)
                {
                    break;
                }
                fields.Add(fieldDefinition);
            }

            fields.AddRange(this.Parent?.Fields ?? Array.Empty<FieldDefinition>());

            return new ReadOnlyCollection<FieldDefinition>(fields.OrderBy(f => f.Name).ToArray());
        }

        private string GetFullName()
        {
            var builder = new StringBuilder();

            var hierarchy = this.NestedHierarchy().Reverse().ToArray();
            if (!string.IsNullOrWhiteSpace(this.NamespaceName))
            {
                builder.Append($"{hierarchy[0].NamespaceName}.");
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