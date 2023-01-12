// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;
using Microsoft.Win32.SafeHandles;

namespace System.Reflection.Metadata
{
    public sealed partial class MetadataReader
    {
        internal AssemblyName GetAssemblyName(StringHandle nameHandle, Version version, StringHandle cultureHandle, BlobHandle publicKeyOrTokenHandle, AssemblyHashAlgorithm assemblyHashAlgorithm, AssemblyFlags flags)
        {
            string name = GetString(nameHandle);
            // compat: normalize Nil culture name to "" to match original behavior of AssemblyName.GetAssemblyName()
            string cultureName = (!cultureHandle.IsNil) ? GetString(cultureHandle) : "";
            var hashAlgorithm = (Configuration.Assemblies.AssemblyHashAlgorithm)assemblyHashAlgorithm;
            // compat: original native implementation used to guarantee that publicKeyOrToken is never null in this scenario.
            byte[]? publicKeyOrToken = !publicKeyOrTokenHandle.IsNil ? GetBlobBytes(publicKeyOrTokenHandle) : Array.Empty<byte>();

            var assemblyName = new AssemblyName()
            {
                Name = name,
                Version = version,
                CultureName = cultureName,
#pragma warning disable SYSLIB0037 // AssemblyName.HashAlgorithm is obsolete
                HashAlgorithm = hashAlgorithm,
#pragma warning restore
                Flags = GetAssemblyNameFlags(flags),
                ContentType = GetContentTypeFromAssemblyFlags(flags)
            };

            bool hasPublicKey = (flags & AssemblyFlags.PublicKey) != 0;
            if (hasPublicKey)
            {
                assemblyName.SetPublicKey(publicKeyOrToken);
            }
            else
            {
                assemblyName.SetPublicKeyToken(publicKeyOrToken);
            }

            return assemblyName;
        }

        /// <summary>
        /// Gets the <see cref="AssemblyName"/> for a given file.
        /// </summary>
        /// <param name="assemblyFile">The path for the assembly which <see cref="AssemblyName"/> is to be returned.</param>
        /// <returns>An <see cref="AssemblyName"/> that represents the given <paramref name="assemblyFile"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="assemblyFile"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="assemblyFile"/> is invalid.</exception>
        /// <exception cref="FileNotFoundException">If <paramref name="assemblyFile"/> is not found.</exception>
        /// <exception cref="BadImageFormatException">If <paramref name="assemblyFile"/> is not a valid assembly.</exception>
        public static unsafe AssemblyName GetAssemblyName(string assemblyFile)
        {
            if (assemblyFile is null)
            {
                Throw.ArgumentNull(nameof(assemblyFile));
            }

            FileStream? fileStream = null;
            MemoryMappedFile? mappedFile = null;
            MemoryMappedViewAccessor? accessor = null;
            PEReader? peReader = null;

            try
            {
                try
                {
                    // Create stream because CreateFromFile(string, ...) uses FileShare.None which is too strict
                    fileStream = new FileStream(assemblyFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: false);
                    if (fileStream.Length == 0)
                    {
                        throw new BadImageFormatException(SR.PEImageDoesNotHaveMetadata, assemblyFile);
                    }

                    mappedFile = MemoryMappedFile.CreateFromFile(
                        fileStream, null, fileStream.Length, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                    accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                    SafeMemoryMappedViewHandle? safeBuffer = accessor.SafeMemoryMappedViewHandle;
                    peReader = new PEReader((byte*)safeBuffer.DangerousGetHandle(), (int)safeBuffer.ByteLength);
                    MetadataReader mdReader = peReader.GetMetadataReader(MetadataReaderOptions.None);
                    AssemblyName assemblyName = mdReader.GetAssemblyDefinition().GetAssemblyName();

                    GetPEKind(peReader, out PortableExecutableKinds peKind, out ImageFileMachine machine);
                    AssemblyFlags aFlags = mdReader.AssemblyTable.GetFlags();
#pragma warning disable SYSLIB0037 // AssemblyName.ProcessorArchitecture is obsolete
                    assemblyName.ProcessorArchitecture = CalculateProcArchIndex(peKind, machine, aFlags);
#pragma warning restore SYSLIB0037

                    return assemblyName;
                }
                finally
                {
                    peReader?.Dispose();
                    accessor?.Dispose();
                    mappedFile?.Dispose();
                    fileStream?.Dispose();
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new BadImageFormatException(ex.Message);
            }
        }

        private static void GetPEKind(PEReader peReader, out PortableExecutableKinds peKind, out ImageFileMachine machine)
        {
            PEHeaders peHeaders = peReader.PEHeaders;
            PEMagic peMagic = peHeaders.PEHeader!.Magic;
            Machine coffMachine = peHeaders.CoffHeader.Machine;
            CorFlags corFlags = peHeaders.CorHeader!.Flags;

            peKind = default;
            if ((corFlags & CorFlags.ILOnly) != 0)
                peKind |= PortableExecutableKinds.ILOnly;

            if ((corFlags & CorFlags.Prefers32Bit) != 0)
                peKind |= PortableExecutableKinds.Preferred32Bit;
            else if ((corFlags & CorFlags.Requires32Bit) != 0)
                peKind |= PortableExecutableKinds.Required32Bit;

            if (peMagic == PEMagic.PE32Plus)
                peKind |= PortableExecutableKinds.PE32Plus;

            machine = (ImageFileMachine)coffMachine;
        }

        private static ProcessorArchitecture CalculateProcArchIndex(PortableExecutableKinds pek, ImageFileMachine ifm, AssemblyFlags flags)
        {
            if (((uint)flags & 0xF0) == 0x70)
                return ProcessorArchitecture.None;

            if ((pek & PortableExecutableKinds.PE32Plus) == PortableExecutableKinds.PE32Plus)
            {
                switch (ifm)
                {
                    case ImageFileMachine.IA64:
                        return ProcessorArchitecture.IA64;
                    case ImageFileMachine.AMD64:
                        return ProcessorArchitecture.Amd64;
                    case ImageFileMachine.I386:
                        if ((pek & PortableExecutableKinds.ILOnly) == PortableExecutableKinds.ILOnly)
                            return ProcessorArchitecture.MSIL;
                        break;
                }
            }
            else
            {
                if (ifm == ImageFileMachine.I386)
                {
                    if ((pek & PortableExecutableKinds.Required32Bit) == PortableExecutableKinds.Required32Bit)
                        return ProcessorArchitecture.X86;

                    if ((pek & PortableExecutableKinds.ILOnly) == PortableExecutableKinds.ILOnly)
                        return ProcessorArchitecture.MSIL;

                    return ProcessorArchitecture.X86;
                }
                if (ifm == ImageFileMachine.ARM)
                {
                    return ProcessorArchitecture.Arm;
                }
            }
            return ProcessorArchitecture.None;
        }

        private static AssemblyNameFlags GetAssemblyNameFlags(AssemblyFlags flags)
        {
            AssemblyNameFlags assemblyNameFlags = AssemblyNameFlags.None;

            if ((flags & AssemblyFlags.PublicKey) != 0)
                assemblyNameFlags |= AssemblyNameFlags.PublicKey;

            if ((flags & AssemblyFlags.Retargetable) != 0)
                assemblyNameFlags |= AssemblyNameFlags.Retargetable;

            if ((flags & AssemblyFlags.EnableJitCompileTracking) != 0)
                assemblyNameFlags |= AssemblyNameFlags.EnableJITcompileTracking;

            if ((flags & AssemblyFlags.DisableJitCompileOptimizer) != 0)
                assemblyNameFlags |= AssemblyNameFlags.EnableJITcompileOptimizer;

            return assemblyNameFlags;
        }

        private static AssemblyContentType GetContentTypeFromAssemblyFlags(AssemblyFlags flags)
        {
            return (AssemblyContentType)(((int)flags & (int)AssemblyFlags.ContentTypeMask) >> 9);
        }
    }
}
