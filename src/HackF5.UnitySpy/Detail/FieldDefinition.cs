﻿namespace HackF5.UnitySpy.Detail
{
    using System;
    using System.Diagnostics;
    using JetBrains.Annotations;

    /// <summary>
    /// Represents an unmanaged _MonoClassField instance in a Mono process. This object describes a field in a
    /// managed class or struct. The .NET equivalent is <see cref="System.Reflection.FieldInfo"/>.
    /// See: _MonoImage in https://github.com/Unity-Technologies/mono/blob/unity-master/mono/metadata/class-internals.h.
    /// </summary>
    [PublicAPI]
    [DebuggerDisplay(
        "Field: {" + nameof(FieldDefinition.Offset) + "} - {" + nameof(FieldDefinition.Name) + "}")]
    public class FieldDefinition : MemoryObject, IFieldDefinition
    {
        public FieldDefinition([NotNull] TypeDefinition declaringType, uint address)
            : base((declaringType ?? throw new ArgumentNullException(nameof(declaringType))).Image, address)
        {
            this.DeclaringType = declaringType;
            this.TypeInfo = new TypeInfo(declaringType.Image, this.ReadPtr(0x0));
            this.Name = this.ReadString(0x4);
            this.Offset = this.ReadInt32(0xc);
        }

        ITypeDefinition IFieldDefinition.DeclaringType => this.DeclaringType;

        public string Name { get; }

        ITypeInfo IFieldDefinition.TypeInfo => this.TypeInfo;

        public TypeDefinition DeclaringType { get; }

        public int Offset { get; set; }

        public TypeInfo TypeInfo { get; }

        public TValue GetValue<TValue>(uint address)
        {
            var offset = this.Offset - (this.DeclaringType.IsValueType ? 8 : 0);
            return (TValue)this.TypeInfo.GetValue((uint)(address + offset));
        }
    }
}