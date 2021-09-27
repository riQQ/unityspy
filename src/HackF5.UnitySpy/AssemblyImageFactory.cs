namespace HackF5.UnitySpy
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using HackF5.UnitySpy.Detail;
    using HackF5.UnitySpy.Util;
    using JetBrains.Annotations;

    /// <summary>
    /// A factory that creates <see cref="IAssemblyImage"/> instances that provides access into a Unity application's
    /// managed memory.
    /// SEE: https://github.com/Unity-Technologies/mono.
    /// </summary>
    [PublicAPI]
    public static class AssemblyImageFactory
    {
        /// <summary>
        /// Creates an <see cref="IAssemblyImage"/> that provides access into a Unity application's managed memory.
        /// </summary>
        /// <param name="processId">
        /// The id of the Unity process to be inspected.
        /// </param>
        /// <param name="assemblyName">
        /// The name of the assembly to be inspected. The default setting of 'Assembly-CSharp' is probably what you want.
        /// </param>
        /// <returns>
        /// An <see cref="IAssemblyImage"/> that provides access into a Unity application's managed memory.
        /// </returns>
        public static IAssemblyImage Create(int processId, string assemblyName = "Assembly-CSharp")
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new InvalidOperationException(
                    "This library reads data directly from a process's memory, so is platform specific "
                    + "and only runs under Windows. It might be possible to get it running under macOS, but...");
            }

            var process = new ProcessFacade(processId);
            var monoModule = AssemblyImageFactory.GetMonoModule(process);
            if (monoModule == null)
            {
                throw new Exception("Couldn't find mono module");
            }
            var moduleDump = process.ReadModule(monoModule);
            var rootDomainFunctionAddress = AssemblyImageFactory.GetRootDomainFunctionAddress(moduleDump, monoModule);

            // get_root_domain_address function has one assembly instruction:
            // mov rax,[rel $0046AD40] (can be found via snowman f.e.)
            // the relative offset $0046AD40 is located at rootDomainFunctionAddress + 3
            // the instruction with operand is 7 byte in total
            var domainAddressOffset = process.ReadInt32(rootDomainFunctionAddress + 3);
            var domainAddress = rootDomainFunctionAddress + 7 + domainAddressOffset;
            // pointer to struct of type _MonoDomain
            var domain = process.ReadPtr(domainAddress);

            return AssemblyImageFactory.GetAssemblyImage(process, assemblyName, domain);
        }

        private static AssemblyImage GetAssemblyImage(ProcessFacade process, string name, long domain)
        {
            long assemblyArrayPointer = GetAssemblyListAddress(process, domain);
            var bla = domain + 108;
            byte[] bytes = process.ReadByteArray(domain, (int)MonoLibraryOffsets.ReferencedAssemblies + 30);
            // pointer to array of structs of type _MonoAssembly
            var assemblyArrayAddress = process.ReadPtr(assemblyArrayPointer);

            if (assemblyArrayAddress == 0)
            {
                throw new Exception("Wrong starting point for referenced assemblies search");
            }

            for (var assemblyAddress = assemblyArrayAddress;
                assemblyAddress != Constants.NullPtr;
                assemblyAddress = process.ReadPtr(assemblyAddress + Constants.SizeOfPtr))
            {
                // _MonoAssembly
                var assembly = process.ReadPtr(assemblyAddress);
                // members
                // int ref_count (4 byte + padding)
                // char *basedir (8 byte)
                // MonoAssemblyName aname
                var assemblyNameAddress = process.ReadPtr(assembly + 2 * Constants.SizeOfPtr);
                var assemblyName = process.ReadAsciiString(assemblyNameAddress);
                if (assemblyName == name)
                {
                    return new AssemblyImage(process, process.ReadPtr(assembly + MonoLibraryOffsets.AssemblyImage), assembly);
                }
            }

            throw new InvalidOperationException($"Unable to find assembly '{name}'");
        }

        private static long GetAssemblyListAddress(ProcessFacade process, long domain)
        {
            const string unityRootDomain = "Unity Root Domain";
            int unityRootDomainStringLength = unityRootDomain.Length + 1;
            // 18 pointers + 3 * 32 bit integer incl. padding (0 byte padding when x86, 4 byte padding when x64) + 16 offset
            // from address list (domain_assemblies) to friendly_name
            int offset = 18 * Constants.SizeOfPtr + (3 * 4 + 4) + 2*8;
            var domainNameStartAddress = domain + offset;

            for (var domainNameAddress = domainNameStartAddress; domainNameAddress < domainNameStartAddress + 200; domainNameAddress += Constants.SizeOfPtr)
            {
                var assembly = process.ReadPtr(domainNameAddress);
                if (assembly == 0)
                {
                    continue;
                }

                var assemblyName = process.ReadAsciiString(assembly, unityRootDomainStringLength);
                if (assemblyName == "Unity Root Domain")
                {
                    // the assembly list pointer is 8 byte before the domain name pointer
                    return domainNameAddress - 2 * Constants.SizeOfPtr;
                }
            }

            throw new Exception("Couldn't determine assembly list");
        }

        // https://stackoverflow.com/questions/36431220/getting-a-list-of-dlls-currently-loaded-in-a-process-c-sharp
        private static ModuleInfo GetMonoModule(ProcessFacade process)
        {
            var modulePointers = Native.GetProcessModulePointers(process);

            // Collect modules from the process
            var modules = new List<ModuleInfo>();
            foreach (var modulePointer in modulePointers)
            {
                var moduleFilePath = new StringBuilder(1024);
                var errorCode = Native.GetModuleFileNameEx(
                    process.Process.Handle,
                    modulePointer,
                    moduleFilePath,
                    (uint)moduleFilePath.Capacity);

                if (errorCode == 0)
                {
                    throw new COMException("Failed to get module file name.", Marshal.GetLastWin32Error());
                }

                var moduleName = Path.GetFileName(moduleFilePath.ToString());
                bool success = Native.GetModuleInformation(
                    process.Process.Handle,
                    modulePointer,
                    out var moduleInformation,
                    (uint)(IntPtr.Size * modulePointers.Length));

                // Convert to a normalized module and add it to our list
                var module = new ModuleInfo(moduleName, moduleInformation.BaseOfDll, moduleInformation.SizeInBytes);
                modules.Add(module);
            }

            return modules.FirstOrDefault(module => Regex.IsMatch(module.ModuleName, @"mono.*\.dll"));
        }

        private static long GetRootDomainFunctionAddress(byte[] moduleDump, ModuleInfo monoModuleInfo)
        {
            // offsets taken from https://docs.microsoft.com/en-us/windows/desktop/Debug/pe-format

            // Offset to PE / lfanew (long file address for the New Executable header)
            var peStartIndex = moduleDump.ToInt32(0x3c);
            var bytes = Encoding.ASCII.GetBytes("PE");
            var peHeader = moduleDump.ToInt32(peStartIndex);
            int coffStart = peStartIndex + 4;
            ushort machineType = moduleDump.ToUInt16(coffStart);
            bool isX64 = machineType == 0x8664;

            ushort coffSizeOfOptionalHeader = moduleDump.ToUInt16(coffStart + 16);

            ushort coffFieldMagic = moduleDump.ToUInt16(coffStart + 20);
            bool pe32Plus = coffFieldMagic == 0x020b;
            // Export Table (RVA)
            var exportDirectoryIndex = pe32Plus ? peStartIndex + 0x78 + 16 : peStartIndex + 0x78;
            var exportDirectory = moduleDump.ToInt32(exportDirectoryIndex);

            var numberOfFunctions = moduleDump.ToInt32(exportDirectory + 0x14);
            var functionAddressArrayIndex = moduleDump.ToInt32(exportDirectory + 0x1c);
            var functionNameArrayIndex = moduleDump.ToInt32(exportDirectory + 0x20);

            IntPtr rootDomainFunctionAddress = IntPtr.Zero;
            for (int functionIndex = (int)Constants.NullPtr;
                functionIndex < (numberOfFunctions * IntPtr.Size);
                functionIndex += IntPtr.Size)
            {
                var functionNameIndex = moduleDump.ToInt32(functionNameArrayIndex + functionIndex);
                var functionName = moduleDump.ToAsciiString(functionNameIndex);
                if (functionName == "mono_get_root_domain")
                {
                    rootDomainFunctionAddress = IntPtr.Add(monoModuleInfo.BaseAddress, moduleDump.ToInt32(functionAddressArrayIndex + functionIndex));

                    break;
                }
            }

            if (rootDomainFunctionAddress == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to find mono_get_root_domain function.");
            }

            return rootDomainFunctionAddress.ToInt64();
        }
    }
}