// Copyright (c) 2025 Niantic Spatial
// SPDX-License-Identifier: MIT

// SPZ format specification: https://github.com/nianticlabs/spz
// Decoding logic ported from nianticlabs/spz/src/cc/load-spz.cc

using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Gsplat
{
    internal struct SpzHeader
    {
        public uint Version;
        public uint NumPoints;
        public byte ShDegree;
        public byte FractionalBits;
        public byte Flags;
    }

    internal class SpzData
    {
        public SpzHeader Header;
        public byte[] Positions;  // v1: NumPoints*6 (float16 xyz), v2+: NumPoints*9 (24-bit fixed xyz)
        public byte[] Alphas;     // NumPoints * 1
        public byte[] Colors;     // NumPoints * 3 (quantized SH DC per channel)
        public byte[] Scales;     // NumPoints * 3 (quantized log-scale per axis)
        public byte[] Rotations;  // v1-2: NumPoints*3 (xyz as unsigned bytes, offset-encoded), v3+: NumPoints*4 (smallest-three)
        public byte[] SH;         // NumPoints * ShDim * 3 (per point: [R_0..R_N, G_0..G_N, B_0..B_N])
    }

    internal static class SpzLoader
    {
        const uint SpzMagic = 0x5053474e; // "NGSP" little-endian
        const uint MaxSupportedVersion = 3; // v4+ uses ZSTD, not supported

        const float ColorScale = 0.15f;
        const float Sqrt1_2 = 0.70710678118f;

        // Number of SH coefficients per color channel, excluding DC (degree 0).
        // Matches GsplatUtils.SHBandsToCoefficientCount.
        public static int ShDim(byte degree) => degree * (degree + 2);

        public static SpzData Load(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

            // SPZ v1–3 are gzip-compressed; v4 dropped the gzip wrapper for a different
            // container, so its raw bytes won't parse as gzip and the version field is
            // not at a fixed offset. Peek the first 2 bytes for the gzip magic and produce
            // a clear error before GZipStream throws an opaque format exception.
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            if (b0 != 0x1F || b1 != 0x8B)
                throw new NotSupportedException(
                    $"{Path.GetFileName(path)} is not a gzip-compressed SPZ file. " +
                    "This may be SPZ v4 (which uses a different container and is not supported), " +
                    "or the file is corrupt. Only SPZ v1–3 (gzip) are supported.");
            fs.Position = 0;

            using var gz = new GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);

            var headerBytes = new byte[16];
            if (gz.Read(headerBytes, 0, 16) != 16)
                throw new InvalidDataException("SPZ file too short to contain header");

            var header = ParseHeader(headerBytes);
            return ReadStreams(gz, header);
        }

        static SpzHeader ParseHeader(byte[] b)
        {
            uint magic = BitConverter.ToUInt32(b, 0);
            if (magic != SpzMagic)
                throw new InvalidDataException($"SPZ: bad magic 0x{magic:X8}, expected 0x{SpzMagic:X8}");

            uint version = BitConverter.ToUInt32(b, 4);
            if (version < 1 || version > MaxSupportedVersion)
            {
                var msg = version > MaxSupportedVersion
                    ? $"SPZ v{version} uses ZSTD compression which is not supported. Only v1-3 (gzip) are supported."
                    : $"SPZ version {version} is not supported";
                throw new NotSupportedException(msg);
            }

            return new SpzHeader
            {
                Version = version,
                NumPoints = BitConverter.ToUInt32(b, 8),
                ShDegree = b[12],
                FractionalBits = b[13],
                Flags = b[14],
            };
        }

        static SpzData ReadStreams(Stream stream, SpzHeader h)
        {
            int n = (int)h.NumPoints;
            bool float16Pos = h.Version == 1;
            bool smallestThree = h.Version >= 3;
            int shDim = ShDim(h.ShDegree);

            return new SpzData
            {
                Header = h,
                Positions = ReadExact(stream, float16Pos ? n * 6 : n * 9),
                Alphas = ReadExact(stream, n),
                Colors = ReadExact(stream, n * 3),
                Scales = ReadExact(stream, n * 3),
                Rotations = ReadExact(stream, smallestThree ? n * 4 : n * 3),
                SH = ReadExact(stream, n * shDim * 3),
            };
        }

        static byte[] ReadExact(Stream stream, int count)
        {
            if (count == 0) return Array.Empty<byte>();
            var buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("Unexpected end of SPZ payload");
                offset += read;
            }
            return buf;
        }

        // Decode 24-bit signed fixed-point XYZ position (v2+).
        public static Vector3 DecodePosition(byte[] positions, int i, byte fractionalBits)
        {
            float scale = 1.0f / (1 << fractionalBits);
            int b = i * 9;
            return new Vector3(
                Fixed24ToFloat(positions, b + 0) * scale,
                Fixed24ToFloat(positions, b + 3) * scale,
                Fixed24ToFloat(positions, b + 6) * scale);
        }

        // Decode float16 XYZ position (v1 only).
        public static Vector3 DecodePositionFloat16(byte[] positions, int i)
        {
            int b = i * 6;
            return new Vector3(
                Mathf.HalfToFloat(BitConverter.ToUInt16(positions, b + 0)),
                Mathf.HalfToFloat(BitConverter.ToUInt16(positions, b + 2)),
                Mathf.HalfToFloat(BitConverter.ToUInt16(positions, b + 4)));
        }

        static float Fixed24ToFloat(byte[] buf, int offset)
        {
            int v = buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }

        // Decode opacity as logit (pre-sigmoid), matching what PLY stores and PackSplat expects.
        public static float DecodeAlphaLogit(byte[] alphas, int i)
        {
            float a = Mathf.Clamp(alphas[i] / 255.0f, 1e-6f, 1f - 1e-6f);
            return Mathf.Log(a / (1f - a));
        }

        // Decode opacity as [0,1] post-sigmoid value, for GsplatAssetUncompressed.
        public static float DecodeAlphaLinear(byte[] alphas, int i) => alphas[i] / 255.0f;

        // Decode quantized color to raw SH DC coefficient (same form as PLY's f_dc_0/1/2).
        public static Vector3 DecodeColor(byte[] colors, int i)
        {
            int b = i * 3;
            return new Vector3(
                (colors[b + 0] / 255.0f - 0.5f) / ColorScale,
                (colors[b + 1] / 255.0f - 0.5f) / ColorScale,
                (colors[b + 2] / 255.0f - 0.5f) / ColorScale);
        }

        // Decode quantized log-scale to ln(scale), matching PLY's scale_0/1/2.
        public static Vector3 DecodeScaleLog(byte[] scales, int i)
        {
            int b = i * 3;
            return new Vector3(
                scales[b + 0] / 16.0f - 10.0f,
                scales[b + 1] / 16.0f - 10.0f,
                scales[b + 2] / 16.0f - 10.0f);
        }

        public static Quaternion DecodeRotation(byte[] rotations, int i, bool usesSmallestThree)
        {
            return usesSmallestThree
                ? DecodeSmallestThree(rotations, i * 4)
                : DecodeXyz3Bytes(rotations, i * 3);
        }

        // v1-2: xyz stored as unsigned bytes with offset encoding: x = byte/127.5 - 1.0.
        // byte 0 = -1.0, byte 127/128 ≈ 0.0, byte 255 = +1.0. w is derived (always non-negative).
        static Quaternion DecodeXyz3Bytes(byte[] r, int offset)
        {
            float x = r[offset + 0] / 127.5f - 1.0f;
            float y = r[offset + 1] / 127.5f - 1.0f;
            float z = r[offset + 2] / 127.5f - 1.0f;
            float w = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y - z * z));
            return new Quaternion(x, y, z, w);
        }

        // v3+: smallest-three quaternion. 32 bits: 2-bit index of largest component,
        // then three components each as (9-bit magnitude, 1-bit sign). Components are
        // packed high-index-first at LSB: index 3 gets bits[0:9], index 2 gets bits[10:19], etc.
        static Quaternion DecodeSmallestThree(byte[] r, int offset)
        {
            uint comp = r[offset]
                | ((uint)r[offset + 1] << 8)
                | ((uint)r[offset + 2] << 16)
                | ((uint)r[offset + 3] << 24);
            const uint cMask = (1u << 9) - 1u;
            int iLargest = (int)(comp >> 30);
            float[] q = new float[4];
            float sumSq = 0f;

            for (int i = 3; i >= 0; i--)
            {
                if (i == iLargest) continue;
                uint mag = comp & cMask;
                uint neg = (comp >> 9) & 1u;
                comp >>= 10;
                float val = Sqrt1_2 * mag / (float)cMask;
                if (neg == 1) val = -val;
                q[i] = val;
                sumSq += val * val;
            }
            q[iLargest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSq));
            return new Quaternion(q[0], q[1], q[2], q[3]);
        }

        // Dequantize a single SH byte: (x - 128) / 128.
        public static float UnquantizeSH(byte[] sh, int byteIndex)
            => (sh[byteIndex] - 128f) / 128f;
    }
}
