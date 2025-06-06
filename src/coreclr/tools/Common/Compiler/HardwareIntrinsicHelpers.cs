// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

using Internal.JitInterface;
using Internal.TypeSystem;

namespace ILCompiler
{
    public static partial class HardwareIntrinsicHelpers
    {
        /// <summary>
        /// Gets a value indicating whether this is a hardware intrinsic on the platform that we're compiling for.
        /// </summary>
        public static bool IsHardwareIntrinsic(MethodDesc method)
        {
            // Matches logic in
            // https://github.com/dotnet/runtime/blob/5c40bb5636b939fb548492fdeb9d501b599ac5f5/src/coreclr/vm/methodtablebuilder.cpp#L1491-L1512
            TypeDesc owningType = method.OwningType;
            if (owningType.IsIntrinsic && !owningType.HasInstantiation)
            {
                var owningMdType = (MetadataType)owningType;
                DefType containingType = owningMdType.ContainingType;
                string ns = containingType?.ContainingType?.Namespace ??
                            containingType?.Namespace ??
                            owningMdType.Namespace;
                return method.Context.Target.Architecture switch
                {
                    TargetArchitecture.ARM64 => ns == "System.Runtime.Intrinsics.Arm",
                    TargetArchitecture.X64 or TargetArchitecture.X86 => ns == "System.Runtime.Intrinsics.X86",
                    _ => false,
                };
            }

            return false;
        }

        public static void AddRuntimeRequiredIsaFlagsToBuilder(InstructionSetSupportBuilder builder, int flags)
        {
            switch (builder.Architecture)
            {
                case TargetArchitecture.X86:
                case TargetArchitecture.X64:
                    XArchIntrinsicConstants.AddToBuilder(builder, flags);
                    break;
                case TargetArchitecture.ARM64:
                    Arm64IntrinsicConstants.AddToBuilder(builder, flags);
                    break;
                case TargetArchitecture.RiscV64:
                    RiscV64IntrinsicConstants.AddToBuilder(builder, flags);
                    break;
                default:
                    Debug.Fail("Probably unimplemented");
                    break;
            }
        }

        // Keep these enumerations in sync with cpufeatures.h in the minipal.
        private static class XArchIntrinsicConstants
        {
            // SSE and SSE2 are baseline ISAs - they're always available
            public const int Aes = 0x0001;
            public const int Pclmulqdq = 0x0002;
            public const int Sse3 = 0x0004;
            public const int Ssse3 = 0x0008;
            public const int Sse41 = 0x0010;
            public const int Sse42 = 0x0020;
            public const int Popcnt = 0x0040;
            public const int Avx = 0x0080;
            public const int Fma = 0x0100;
            public const int Avx2 = 0x0200;
            public const int Bmi1 = 0x0400;
            public const int Bmi2 = 0x0800;
            public const int Lzcnt = 0x1000;
            public const int AvxVnni = 0x2000;
            public const int Movbe = 0x4000;
            public const int Avx512 = 0x8000;
            public const int Avx512Vbmi = 0x10000;
            public const int Serialize = 0x20000;
            public const int Avx10v1 = 0x40000;
            public const int Evex = 0x80000;
            public const int Apx = 0x100000;
            public const int Vpclmulqdq = 0x200000;
            public const int Avx10v2 = 0x400000;
            public const int Gfni = 0x800000;

            public static void AddToBuilder(InstructionSetSupportBuilder builder, int flags)
            {
                if ((flags & Aes) != 0)
                    builder.AddSupportedInstructionSet("aes");
                if ((flags & Pclmulqdq) != 0)
                    builder.AddSupportedInstructionSet("pclmul");
                if ((flags & Sse3) != 0)
                    builder.AddSupportedInstructionSet("sse3");
                if ((flags & Ssse3) != 0)
                    builder.AddSupportedInstructionSet("ssse3");
                if ((flags & Sse41) != 0)
                    builder.AddSupportedInstructionSet("sse4.1");
                if ((flags & Sse42) != 0)
                    builder.AddSupportedInstructionSet("sse4.2");
                if ((flags & Popcnt) != 0)
                    builder.AddSupportedInstructionSet("popcnt");
                if ((flags & Avx) != 0)
                    builder.AddSupportedInstructionSet("avx");
                if ((flags & Fma) != 0)
                    builder.AddSupportedInstructionSet("fma");
                if ((flags & Avx2) != 0)
                    builder.AddSupportedInstructionSet("avx2");
                if ((flags & Bmi1) != 0)
                    builder.AddSupportedInstructionSet("bmi");
                if ((flags & Bmi2) != 0)
                    builder.AddSupportedInstructionSet("bmi2");
                if ((flags & Lzcnt) != 0)
                    builder.AddSupportedInstructionSet("lzcnt");
                if ((flags & AvxVnni) != 0)
                    builder.AddSupportedInstructionSet("avxvnni");
                if ((flags & Movbe) != 0)
                    builder.AddSupportedInstructionSet("movbe");
                if ((flags & Avx512) != 0)
                {
                    builder.AddSupportedInstructionSet("avx512f");
                    builder.AddSupportedInstructionSet("avx512f_vl");
                    builder.AddSupportedInstructionSet("avx512bw");
                    builder.AddSupportedInstructionSet("avx512bw_vl");
                    builder.AddSupportedInstructionSet("avx512cd");
                    builder.AddSupportedInstructionSet("avx512cd_vl");
                    builder.AddSupportedInstructionSet("avx512dq");
                    builder.AddSupportedInstructionSet("avx512dq_vl");
                }
                if ((flags & Avx512Vbmi) != 0)
                {
                    builder.AddSupportedInstructionSet("avx512vbmi");
                    builder.AddSupportedInstructionSet("avx512vbmi_vl");
                }
                if ((flags & Serialize) != 0)
                    builder.AddSupportedInstructionSet("serialize");
                if ((flags & Avx10v1) != 0)
                    builder.AddSupportedInstructionSet("avx10v1");
                if (((flags & Avx10v1) != 0) && ((flags & Avx512) != 0))
                    builder.AddSupportedInstructionSet("avx10v1_v512");
                if ((flags & Evex) != 0)
                    builder.AddSupportedInstructionSet("evex");
                if ((flags & Apx) != 0)
                    builder.AddSupportedInstructionSet("apx");
                if ((flags & Vpclmulqdq) != 0)
                {
                    builder.AddSupportedInstructionSet("vpclmul");
                    if ((flags & Avx512) != 0)
                        builder.AddSupportedInstructionSet("vpclmul_v512");
                }
                if ((flags & Avx10v2) != 0)
                    builder.AddSupportedInstructionSet("avx10v2");
                if (((flags & Avx10v2) != 0) && ((flags & Avx512) != 0))
                    builder.AddSupportedInstructionSet("avx10v2_v512");
                if ((flags & Gfni) != 0)
                {
                    builder.AddSupportedInstructionSet("gfni");
                    if ((flags & Avx) != 0)
                        builder.AddSupportedInstructionSet("gfni_v256");
                    if ((flags & Avx512) != 0)
                        builder.AddSupportedInstructionSet("gfni_v512");
                }
            }

            public static int FromInstructionSet(InstructionSet instructionSet)
            {
                Debug.Assert(InstructionSet.X64_AES == InstructionSet.X86_AES);
                Debug.Assert(InstructionSet.X64_SSE41 == InstructionSet.X86_SSE41);
                Debug.Assert(InstructionSet.X64_LZCNT == InstructionSet.X86_LZCNT);

                return instructionSet switch
                {
                    // Optional ISAs - only available via opt-in or opportunistic light-up
                    InstructionSet.X64_AES => Aes,
                    InstructionSet.X64_AES_X64 => Aes,
                    InstructionSet.X64_PCLMULQDQ => Pclmulqdq,
                    InstructionSet.X64_PCLMULQDQ_X64 => Pclmulqdq,
                    InstructionSet.X64_SSE3 => Sse3,
                    InstructionSet.X64_SSE3_X64 => Sse3,
                    InstructionSet.X64_SSSE3 => Ssse3,
                    InstructionSet.X64_SSSE3_X64 => Ssse3,
                    InstructionSet.X64_SSE41 => Sse41,
                    InstructionSet.X64_SSE41_X64 => Sse41,
                    InstructionSet.X64_SSE42 => Sse42,
                    InstructionSet.X64_SSE42_X64 => Sse42,
                    InstructionSet.X64_POPCNT => Popcnt,
                    InstructionSet.X64_POPCNT_X64 => Popcnt,
                    InstructionSet.X64_AVX => Avx,
                    InstructionSet.X64_AVX_X64 => Avx,
                    InstructionSet.X64_FMA => Fma,
                    InstructionSet.X64_FMA_X64 => Fma,
                    InstructionSet.X64_AVX2 => Avx2,
                    InstructionSet.X64_AVX2_X64 => Avx2,
                    InstructionSet.X64_BMI1 => Bmi1,
                    InstructionSet.X64_BMI1_X64 => Bmi1,
                    InstructionSet.X64_BMI2 => Bmi2,
                    InstructionSet.X64_BMI2_X64 => Bmi2,
                    InstructionSet.X64_LZCNT => Lzcnt,
                    InstructionSet.X64_LZCNT_X64 => Lzcnt,
                    InstructionSet.X64_AVXVNNI => AvxVnni,
                    InstructionSet.X64_AVXVNNI_X64 => AvxVnni,
                    InstructionSet.X64_MOVBE => Movbe,
                    InstructionSet.X64_AVX512F => Avx512,
                    InstructionSet.X64_AVX512F_X64 => Avx512,
                    InstructionSet.X64_AVX512F_VL => Avx512,
                    InstructionSet.X64_AVX512BW => Avx512,
                    InstructionSet.X64_AVX512BW_X64 => Avx512,
                    InstructionSet.X64_AVX512BW_VL => Avx512,
                    InstructionSet.X64_AVX512CD => Avx512,
                    InstructionSet.X64_AVX512CD_X64 => Avx512,
                    InstructionSet.X64_AVX512CD_VL => Avx512,
                    InstructionSet.X64_AVX512DQ => Avx512,
                    InstructionSet.X64_AVX512DQ_X64 => Avx512,
                    InstructionSet.X64_AVX512DQ_VL => Avx512,
                    InstructionSet.X64_AVX512VBMI => Avx512Vbmi,
                    InstructionSet.X64_AVX512VBMI_X64 => Avx512Vbmi,
                    InstructionSet.X64_AVX512VBMI_VL => Avx512Vbmi,
                    InstructionSet.X64_X86Serialize => Serialize,
                    InstructionSet.X64_X86Serialize_X64 => Serialize,
                    InstructionSet.X64_AVX10v1 => Avx10v1,
                    InstructionSet.X64_AVX10v1_X64 => Avx10v1,
                    InstructionSet.X64_AVX10v1_V512 => (Avx10v1 | Avx512),
                    InstructionSet.X64_AVX10v1_V512_X64 => (Avx10v1 | Avx512),
                    InstructionSet.X64_EVEX => Evex,
                    InstructionSet.X64_APX => Apx,
                    InstructionSet.X64_PCLMULQDQ_V256 => Vpclmulqdq,
                    InstructionSet.X64_PCLMULQDQ_V512 => (Vpclmulqdq | Avx512),
                    InstructionSet.X64_AVX10v2 => Avx10v2,
                    InstructionSet.X64_AVX10v2_X64 => Avx10v2,
                    InstructionSet.X64_AVX10v2_V512 => (Avx10v2 | Avx512),
                    InstructionSet.X64_AVX10v2_V512_X64 => (Avx10v2 | Avx512),
                    InstructionSet.X64_GFNI => Gfni,
                    InstructionSet.X64_GFNI_X64 => Gfni,
                    InstructionSet.X64_GFNI_V256 => (Gfni | Avx),
                    InstructionSet.X64_GFNI_V512 => (Gfni | Avx512),

                    // Baseline ISAs - they're always available
                    InstructionSet.X64_SSE => 0,
                    InstructionSet.X64_SSE_X64 => 0,
                    InstructionSet.X64_SSE2 => 0,
                    InstructionSet.X64_SSE2_X64 => 0,

                    InstructionSet.X64_X86Base => 0,
                    InstructionSet.X64_X86Base_X64 => 0,

                    // Vector<T> Sizes
                    InstructionSet.X64_VectorT128 => 0,
                    InstructionSet.X64_VectorT256 => Avx2,
                    InstructionSet.X64_VectorT512 => Avx512,

                    _ => throw new NotSupportedException(((InstructionSet_X64)instructionSet).ToString())
                };
            }
        }

        // Keep these enumerations in sync with cpufeatures.h in the minipal.
        private static class Arm64IntrinsicConstants
        {
            public const int AdvSimd = 0x0001;
            public const int Aes = 0x0002;
            public const int Crc32 = 0x0004;
            public const int Dp = 0x0008;
            public const int Rdm = 0x0010;
            public const int Sha1 = 0x0020;
            public const int Sha256 = 0x0040;
            public const int Atomics = 0x0080;
            public const int Rcpc = 0x0100;
            public const int Rcpc2 = 0x0200;
            public const int Sve = 0x0400;

            public static void AddToBuilder(InstructionSetSupportBuilder builder, int flags)
            {
                if ((flags & AdvSimd) != 0)
                    builder.AddSupportedInstructionSet("neon");
                if ((flags & Aes) != 0)
                    builder.AddSupportedInstructionSet("aes");
                if ((flags & Crc32) != 0)
                    builder.AddSupportedInstructionSet("crc");
                if ((flags & Dp) != 0)
                    builder.AddSupportedInstructionSet("dotprod");
                if ((flags & Rdm) != 0)
                    builder.AddSupportedInstructionSet("rdma");
                if ((flags & Sha1) != 0)
                    builder.AddSupportedInstructionSet("sha1");
                if ((flags & Sha256) != 0)
                    builder.AddSupportedInstructionSet("sha2");
                if ((flags & Atomics) != 0)
                    builder.AddSupportedInstructionSet("lse");
                if ((flags & Rcpc) != 0)
                    builder.AddSupportedInstructionSet("rcpc");
                if ((flags & Rcpc2) != 0)
                    builder.AddSupportedInstructionSet("rcpc2");
                if ((flags & Sve) != 0)
                    builder.AddSupportedInstructionSet("sve");
            }

            public static int FromInstructionSet(InstructionSet instructionSet)
            {
                return instructionSet switch
                {

                    // Baseline ISAs - they're always available
                    InstructionSet.ARM64_ArmBase => 0,
                    InstructionSet.ARM64_ArmBase_Arm64 => 0,
                    InstructionSet.ARM64_AdvSimd => AdvSimd,
                    InstructionSet.ARM64_AdvSimd_Arm64 => AdvSimd,

                    // Optional ISAs - only available via opt-in or opportunistic light-up
                    InstructionSet.ARM64_Aes => Aes,
                    InstructionSet.ARM64_Aes_Arm64 => Aes,
                    InstructionSet.ARM64_Crc32 => Crc32,
                    InstructionSet.ARM64_Crc32_Arm64 => Crc32,
                    InstructionSet.ARM64_Dp => Dp,
                    InstructionSet.ARM64_Dp_Arm64 => Dp,
                    InstructionSet.ARM64_Rdm => Rdm,
                    InstructionSet.ARM64_Rdm_Arm64 => Rdm,
                    InstructionSet.ARM64_Sha1 => Sha1,
                    InstructionSet.ARM64_Sha1_Arm64 => Sha1,
                    InstructionSet.ARM64_Sha256 => Sha256,
                    InstructionSet.ARM64_Sha256_Arm64 => Sha256,
                    InstructionSet.ARM64_Atomics => Atomics,
                    InstructionSet.ARM64_Rcpc => Rcpc,
                    InstructionSet.ARM64_Rcpc2 => Rcpc2,
                    InstructionSet.ARM64_Sve => Sve,
                    InstructionSet.ARM64_Sve_Arm64 => Sve,

                    // Vector<T> Sizes
                    InstructionSet.ARM64_VectorT128 => AdvSimd,

                    _ => throw new NotSupportedException(((InstructionSet_ARM64)instructionSet).ToString())
                };
            }
        }

        // Keep these enumerations in sync with cpufeatures.h in the minipal.
        private static class RiscV64IntrinsicConstants
        {
            public const int Zba = 0x0001;
            public const int Zbb = 0x0002;

            public static void AddToBuilder(InstructionSetSupportBuilder builder, int flags)
            {
                if ((flags & Zba) != 0)
                    builder.AddSupportedInstructionSet("zba");
                if ((flags & Zbb) != 0)
                    builder.AddSupportedInstructionSet("zbb");
            }

            public static int FromInstructionSet(InstructionSet instructionSet)
            {
                return instructionSet switch
                {
                    // Baseline ISAs - they're always available
                    InstructionSet.RiscV64_RiscV64Base => 0,

                    // Optional ISAs - only available via opt-in or opportunistic light-up
                    InstructionSet.RiscV64_Zba => Zba,
                    InstructionSet.RiscV64_Zbb => Zbb,

                    _ => throw new NotSupportedException(((InstructionSet_RiscV64)instructionSet).ToString())
                };
            }
        }
    }
}
