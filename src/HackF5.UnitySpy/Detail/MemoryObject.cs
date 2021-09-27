namespace HackF5.UnitySpy.Detail
{
    /// <summary>
    /// The base type for all objects accessed in a process' memory. Every object has an address in memory
    /// and all information about that object is accessed via an offset from that address.
    /// </summary>
    public abstract class MemoryObject : IMemoryObject
    {
        protected MemoryObject(AssemblyImage image, long address)
        {
            this.Image = image;
            this.Address = address;
        }

        IAssemblyImage IMemoryObject.Image => this.Image;

        public virtual AssemblyImage Image { get; }

        public virtual ProcessFacade Process => this.Image.Process;

        internal long Address { get; }

        protected int ReadInt32(long offset) => this.Process.ReadInt32(this.Address + offset);

        protected long ReadPtr(long offset) => this.Process.ReadPtr(this.Address + offset);

        protected string ReadString(long offset) => this.Process.ReadAsciiStringPtr(this.Address + offset);

        protected uint ReadUInt32(long offset) => this.Process.ReadUInt32(this.Address + offset);

        protected byte ReadByte(long offset) => this.Process.ReadByte(this.Address + offset);

        protected byte[] ReadByteArray(long offset, int size) => this.Process.ReadByteArray(this.Address + offset, size);
    }
}