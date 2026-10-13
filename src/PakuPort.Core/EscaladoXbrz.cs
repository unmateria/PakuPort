using System;

namespace PakuPort.Core;

/// <summary>Escalador xBRZ ("scale by rules", de Zenju): amplía arte pixelado x2..x6 prolongando
/// los bordes con gradientes, en vez de repetir bloques o emborronar interpolando.
///
/// Es una traducción FIEL a C# del camino RGB (ColorFormat::RGB) de xBRZ tal y como viene en
/// DOSBox-X: <c>src/libs/xBRZ/xbrz.cpp</c> + <c>xbrz.h</c> + <c>xbrz_tools.h</c> +
/// <c>xbrz_config.h</c> (copyright Zenju, GPL-3.0 — este proyecto ya es GPL-3.0 y documenta sus
/// derivaciones con el fichero de origen). El algoritmo se copió tal cual: preprocesado de
/// esquinas de 4 por 4, mezcla por píxel con las cuatro rotaciones, distancia de color YCbCr (BT.2020) y
/// los escaladores Scaler2x..Scaler6x con sus pesos exactos. La única desviación deliberada es que
/// la distancia se calcula con la fórmula en vez de con la tabla de 64 MB del original
/// (distYCbCrBuffered); se replica su cuantización (diferencias a pasos de 2) y su redondeo a
/// float para que el resultado numérico sea el MISMO que el de la tabla.
///
/// Layout de píxel de <see cref="Escala"/>: uint 0xAARRGGBB (el byte alto se ignora para el color,
/// pero participa en las comparaciones de igualdad exacta, igual que en el original — conviene
/// pasarlo constante en toda la imagen). <see cref="EscalaRgba"/> acepta y devuelve bytes R,G,B,A
/// en ese orden en memoria y deja el alfa de salida a 255.</summary>
public static class EscaladoXbrz
{
    // xbrz::ScalerCfg defaults (xbrz_config.h)
    const double LuminanceWeight = 1;
    const double EqualColorTolerance = 30;
    const double DominantDirectionThreshold = 3.6;
    const double SteepDirectionThreshold = 2.2;

    /// <summary>Escala <paramref name="src"/> (w*h píxeles 0xAARRGGBB) a
    /// <paramref name="dst"/> ((w*factor)*(h*factor)). <paramref name="factor"/> válido: 2..6.</summary>
    public static void Escala(uint[] src, uint[] dst, int w, int h, int factor)
    {
        if (src.Length < w * h) throw new ArgumentException("src demasiado corto", nameof(src));
        if (dst.Length < w * factor * h * factor) throw new ArgumentException("dst demasiado corto", nameof(dst));
        switch (factor)
        {
            case 2: ScaleImage<Scaler2x>(src, dst, w, h); break;
            case 3: ScaleImage<Scaler3x>(src, dst, w, h); break;
            case 4: ScaleImage<Scaler4x>(src, dst, w, h); break;
            case 5: ScaleImage<Scaler5x>(src, dst, w, h); break;
            case 6: ScaleImage<Scaler6x>(src, dst, w, h); break;
            default: throw new ArgumentOutOfRangeException(nameof(factor), "factor válido: 2..6");
        }
    }

    static uint[]? _srcPix, _dstPix;

    /// <summary>Conveniencia: entrada y salida como bytes R,G,B,A. El alfa de entrada se ignora y
    /// el de salida sale a 255. Reutiliza buffers internos: NO es reentrante entre hilos.</summary>
    public static byte[] EscalaRgba(byte[] rgbaSrc, int w, int h, int factor)
    {
        if (rgbaSrc.Length < w * h * 4) throw new ArgumentException("rgbaSrc demasiado corto", nameof(rgbaSrc));
        int wOut = w * factor, hOut = h * factor;
        if (_srcPix == null || _srcPix.Length < w * h) _srcPix = new uint[w * h];
        if (_dstPix == null || _dstPix.Length < wOut * hOut) _dstPix = new uint[wOut * hOut];

        for (int i = 0, p = 0; i < w * h; i++, p += 4)
            _srcPix[i] = ((uint)rgbaSrc[p] << 16) | ((uint)rgbaSrc[p + 1] << 8) | rgbaSrc[p + 2];

        Escala(_srcPix, _dstPix, w, h, factor);

        var outBytes = new byte[wOut * hOut * 4];
        for (int i = 0, p = 0; i < wOut * hOut; i++, p += 4)
        {
            uint pix = _dstPix[i];
            outBytes[p] = (byte)(pix >> 16);
            outBytes[p + 1] = (byte)(pix >> 8);
            outBytes[p + 2] = (byte)pix;
            outBytes[p + 3] = 255;
        }
        return outBytes;
    }

    // ---- color distance: ColorDistanceRGB / distYCbCrBuffered (xbrz.cpp) ------------------
    // The original looks the value up in a 64 MB float table indexed by the channel differences
    // quantized to steps of 2 ((diff + 0xFF) / 2). Computing the same formula on the quantized
    // differences and rounding through float reproduces the table's values exactly.
    static double Dist(uint pix1, uint pix2)
    {
        int rDiff = (int)((pix1 >> 16) & 0xFF) - (int)((pix2 >> 16) & 0xFF);
        int gDiff = (int)((pix1 >> 8) & 0xFF) - (int)((pix2 >> 8) & 0xFF);
        int bDiff = (int)(pix1 & 0xFF) - (int)(pix2 & 0xFF);

        // quantization used by the buffered table: idx = (diff + 0xFF) / 2; diff' = idx * 2 - 0xFF
        int r = ((rDiff + 0xFF) / 2) * 2 - 0xFF;
        int g = ((gDiff + 0xFF) / 2) * 2 - 0xFF;
        int b = ((bDiff + 0xFF) / 2) * 2 - 0xFF;

        const double kB = 0.0593; // ITU-R BT.2020 conversion
        const double kR = 0.2627;
        const double kG = 1 - kB - kR;
        const double scaleB = 0.5 / (1 - kB);
        const double scaleR = 0.5 / (1 - kR);

        double y = kR * r + kG * g + kB * b;
        double cB = scaleB * (b - y);
        double cR = scaleR * (r - y);

        return (float)Math.Sqrt(y * y + cB * cB + cR * cR); // table stores float
    }

    static bool Eq(uint pix1, uint pix2) => Dist(pix1, pix2) < EqualColorTolerance;

    // ---- gradientRGB<M, N>: blend front color with opacity M/N over opaque background ------
    static uint GradientRgb(uint m, uint n, uint pixFront, uint pixBack)
    {
        uint Calc(uint front, uint back) => (front * m + back * (n - m)) / n;
        return (Calc((pixFront >> 16) & 0xFF, (pixBack >> 16) & 0xFF) << 16) |
               (Calc((pixFront >> 8) & 0xFF, (pixBack >> 8) & 0xFF) << 8) |
               Calc(pixFront & 0xFF, pixBack & 0xFF);
    }

    // ---- blend info: four 2-bit BlendType values packed into one byte ----------------------
    const byte BlendNone = 0, BlendNormal = 1, BlendDominant = 2;

    static byte GetTopR(byte b) => (byte)(0x3 & (b >> 2));
    static byte GetBottomR(byte b) => (byte)(0x3 & (b >> 4));
    static byte GetBottomL(byte b) => (byte)(0x3 & (b >> 6));

    static void SetTopL(ref byte b, byte bt) => b |= bt;
    static void SetTopR(ref byte b, byte bt) => b |= (byte)(bt << 2);
    static void SetBottomR(ref byte b, byte bt) => b |= (byte)(bt << 4);
    static void SetBottomL(ref byte b, byte bt) => b |= (byte)(bt << 6);

    static byte RotateBlendInfo(byte b, int rotDeg) => rotDeg switch
    {
        1 => (byte)(((b << 2) | (b >> 6)) & 0xFF),
        2 => (byte)(((b << 4) | (b >> 4)) & 0xFF),
        3 => (byte)(((b << 6) | (b >> 2)) & 0xFF),
        _ => b,
    };

    struct BlendResult // preProcessCorners output: corners F, G, J, K
    {
        public byte F, G, J, K;
    }

    /*
    input kernel area naming convention (4 by 4, input pixel at F):
    | A | B | C | D |
    | E | F | G | H |   evaluate the four corners between F, G, J, K
    | I | J | K | L |
    | M | N | O | P |
    */
    static BlendResult PreProcessCorners(
        uint b, uint c, uint e, uint f, uint g, uint h,
        uint i, uint j, uint k, uint l, uint n, uint o)
    {
        BlendResult result = default;

        if ((f == g && j == k) || (f == j && g == k))
            return result;

        const int weight = 4;
        double jg = Dist(i, f) + Dist(f, c) + Dist(n, k) + Dist(k, h) + weight * Dist(j, g);
        double fk = Dist(e, j) + Dist(j, o) + Dist(b, g) + Dist(g, l) + weight * Dist(f, k);

        if (jg < fk)
        {
            bool dominantGradient = DominantDirectionThreshold * jg < fk;
            if (f != g && f != j)
                result.F = dominantGradient ? BlendDominant : BlendNormal;
            if (k != j && k != g)
                result.K = dominantGradient ? BlendDominant : BlendNormal;
        }
        else if (fk < jg)
        {
            bool dominantGradient = DominantDirectionThreshold * fk < jg;
            if (j != f && j != k)
                result.J = dominantGradient ? BlendDominant : BlendNormal;
            if (g != f && g != k)
                result.G = dominantGradient ? BlendDominant : BlendNormal;
        }
        return result;
    }

    // ---- OutputMatrix: writes into the scale x scale target block, rotated -----------------
    readonly struct OutMatrix
    {
        readonly uint[] _buf;
        readonly int _base;      // index of block's top-left in _buf
        readonly int _width;     // target image width in pixels
        readonly int _rotDeg;    // 0..3, clockwise quarter turns
        readonly int _n;         // matrix size (= scale)

        public OutMatrix(uint[] buf, int baseIdx, int width, int rotDeg, int n)
        {
            _buf = buf; _base = baseIdx; _width = width; _rotDeg = rotDeg; _n = n;
        }

        // MatrixRotation<rotDeg, I, J, N> resolved at runtime:
        // ROT_0:(i,j)  ROT_90:(n-1-j,i)  ROT_180:(n-1-i,n-1-j)  ROT_270:(j,n-1-i)
        public ref uint Ref(int i, int j)
        {
            int iOld, jOld;
            switch (_rotDeg)
            {
                case 1: iOld = _n - 1 - j; jOld = i; break;
                case 2: iOld = _n - 1 - i; jOld = _n - 1 - j; break;
                case 3: iOld = j; jOld = _n - 1 - i; break;
                default: iOld = i; jOld = j; break;
            }
            return ref _buf[_base + jOld + iOld * _width];
        }

        public void AlphaGrad(uint m, uint n, int i, int j, uint col)
        {
            ref uint pixBack = ref Ref(i, j);
            pixBack = GradientRgb(m, n, col, pixBack);
        }

        public void Set(int i, int j, uint col) => Ref(i, j) = col;
    }

    // ---- scaler policies (Scaler2x..Scaler6x, weights copied verbatim) ---------------------
    interface IScaler
    {
        static abstract int Scale { get; }
        static abstract void BlendLineShallow(uint col, in OutMatrix o);
        static abstract void BlendLineSteep(uint col, in OutMatrix o);
        static abstract void BlendLineSteepAndShallow(uint col, in OutMatrix o);
        static abstract void BlendLineDiagonal(uint col, in OutMatrix o);
        static abstract void BlendCorner(uint col, in OutMatrix o);
    }

    struct Scaler2x : IScaler
    {
        public static int Scale => 2;

        public static void BlendLineShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 1, 0, col);
            o.AlphaGrad(3, 4, 1, 1, col);
        }

        public static void BlendLineSteep(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 1, col);
            o.AlphaGrad(3, 4, 1, 1, col);
        }

        public static void BlendLineSteepAndShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 1, 0, col);
            o.AlphaGrad(1, 4, 0, 1, col);
            o.AlphaGrad(5, 6, 1, 1, col); // [!] fixes 7/8 used in xBR
        }

        public static void BlendLineDiagonal(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 2, 1, 1, col);
        }

        public static void BlendCorner(uint col, in OutMatrix o)
        {
            o.AlphaGrad(21, 100, 1, 1, col); // exact: 1 - pi/4 = 0.2146018366
        }
    }

    struct Scaler3x : IScaler
    {
        public static int Scale => 3;

        public static void BlendLineShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 2, 0, col);
            o.AlphaGrad(1, 4, 1, 2, col);

            o.AlphaGrad(3, 4, 2, 1, col);
            o.Set(2, 2, col);
        }

        public static void BlendLineSteep(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 2, col);
            o.AlphaGrad(1, 4, 2, 1, col);

            o.AlphaGrad(3, 4, 1, 2, col);
            o.Set(2, 2, col);
        }

        public static void BlendLineSteepAndShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 2, 0, col);
            o.AlphaGrad(1, 4, 0, 2, col);
            o.AlphaGrad(3, 4, 2, 1, col);
            o.AlphaGrad(3, 4, 1, 2, col);
            o.Set(2, 2, col);
        }

        public static void BlendLineDiagonal(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 8, 1, 2, col); // conflict with other rotations for this odd scale
            o.AlphaGrad(1, 8, 2, 1, col);
            o.AlphaGrad(7, 8, 2, 2, col);
        }

        public static void BlendCorner(uint col, in OutMatrix o)
        {
            o.AlphaGrad(45, 100, 2, 2, col); // exact: 0.4545939598
        }
    }

    struct Scaler4x : IScaler
    {
        public static int Scale => 4;

        public static void BlendLineShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 3, 0, col);
            o.AlphaGrad(1, 4, 2, 2, col);

            o.AlphaGrad(3, 4, 3, 1, col);
            o.AlphaGrad(3, 4, 2, 3, col);

            o.Set(3, 2, col);
            o.Set(3, 3, col);
        }

        public static void BlendLineSteep(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 3, col);
            o.AlphaGrad(1, 4, 2, 2, col);

            o.AlphaGrad(3, 4, 1, 3, col);
            o.AlphaGrad(3, 4, 3, 2, col);

            o.Set(2, 3, col);
            o.Set(3, 3, col);
        }

        public static void BlendLineSteepAndShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(3, 4, 3, 1, col);
            o.AlphaGrad(3, 4, 1, 3, col);
            o.AlphaGrad(1, 4, 3, 0, col);
            o.AlphaGrad(1, 4, 0, 3, col);

            o.AlphaGrad(1, 3, 2, 2, col); // [!] fixes 1/4 used in xBR

            o.Set(3, 3, col);
            o.Set(3, 2, col);
            o.Set(2, 3, col);
        }

        public static void BlendLineDiagonal(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 2, 3, 2, col); // ref<scale - 1, scale / 2>
            o.AlphaGrad(1, 2, 2, 3, col); // ref<scale - 2, scale / 2 + 1>
            o.Set(3, 3, col);
        }

        public static void BlendCorner(uint col, in OutMatrix o)
        {
            o.AlphaGrad(68, 100, 3, 3, col); // exact: 0.6848532563
            o.AlphaGrad(9, 100, 3, 2, col);  // 0.08677704501
            o.AlphaGrad(9, 100, 2, 3, col);  // 0.08677704501
        }
    }

    struct Scaler5x : IScaler
    {
        public static int Scale => 5;

        public static void BlendLineShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 4, 0, col);
            o.AlphaGrad(1, 4, 3, 2, col);
            o.AlphaGrad(1, 4, 2, 4, col);

            o.AlphaGrad(3, 4, 4, 1, col);
            o.AlphaGrad(3, 4, 3, 3, col);

            o.Set(4, 2, col);
            o.Set(4, 3, col);
            o.Set(4, 4, col);
            o.Set(3, 4, col);
        }

        public static void BlendLineSteep(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 4, col);
            o.AlphaGrad(1, 4, 2, 3, col);
            o.AlphaGrad(1, 4, 4, 2, col);

            o.AlphaGrad(3, 4, 1, 4, col);
            o.AlphaGrad(3, 4, 3, 3, col);

            o.Set(2, 4, col);
            o.Set(3, 4, col);
            o.Set(4, 4, col);
            o.Set(4, 3, col);
        }

        public static void BlendLineSteepAndShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 4, col);
            o.AlphaGrad(1, 4, 2, 3, col);
            o.AlphaGrad(3, 4, 1, 4, col);

            o.AlphaGrad(1, 4, 4, 0, col);
            o.AlphaGrad(1, 4, 3, 2, col);
            o.AlphaGrad(3, 4, 4, 1, col);

            o.AlphaGrad(2, 3, 3, 3, col);

            o.Set(2, 4, col);
            o.Set(3, 4, col);
            o.Set(4, 4, col);

            o.Set(4, 2, col);
            o.Set(4, 3, col);
        }

        public static void BlendLineDiagonal(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 8, 4, 2, col); // ref<scale - 1, scale / 2>; conflict with other rotations for this odd scale
            o.AlphaGrad(1, 8, 3, 3, col); // ref<scale - 2, scale / 2 + 1>
            o.AlphaGrad(1, 8, 2, 4, col); // ref<scale - 3, scale / 2 + 2>

            o.AlphaGrad(7, 8, 4, 3, col);
            o.AlphaGrad(7, 8, 3, 4, col);

            o.Set(4, 4, col);
        }

        public static void BlendCorner(uint col, in OutMatrix o)
        {
            o.AlphaGrad(86, 100, 4, 4, col); // exact: 0.8631434088
            o.AlphaGrad(23, 100, 4, 3, col); // 0.2306749731
            o.AlphaGrad(23, 100, 3, 4, col); // 0.2306749731
        }
    }

    struct Scaler6x : IScaler
    {
        public static int Scale => 6;

        public static void BlendLineShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 5, 0, col);
            o.AlphaGrad(1, 4, 4, 2, col);
            o.AlphaGrad(1, 4, 3, 4, col);

            o.AlphaGrad(3, 4, 5, 1, col);
            o.AlphaGrad(3, 4, 4, 3, col);
            o.AlphaGrad(3, 4, 3, 5, col);

            o.Set(5, 2, col);
            o.Set(5, 3, col);
            o.Set(5, 4, col);
            o.Set(5, 5, col);

            o.Set(4, 4, col);
            o.Set(4, 5, col);
        }

        public static void BlendLineSteep(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 5, col);
            o.AlphaGrad(1, 4, 2, 4, col);
            o.AlphaGrad(1, 4, 4, 3, col);

            o.AlphaGrad(3, 4, 1, 5, col);
            o.AlphaGrad(3, 4, 3, 4, col);
            o.AlphaGrad(3, 4, 5, 3, col);

            o.Set(2, 5, col);
            o.Set(3, 5, col);
            o.Set(4, 5, col);
            o.Set(5, 5, col);

            o.Set(4, 4, col);
            o.Set(5, 4, col);
        }

        public static void BlendLineSteepAndShallow(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 4, 0, 5, col);
            o.AlphaGrad(1, 4, 2, 4, col);
            o.AlphaGrad(3, 4, 1, 5, col);
            o.AlphaGrad(3, 4, 3, 4, col);

            o.AlphaGrad(1, 4, 5, 0, col);
            o.AlphaGrad(1, 4, 4, 2, col);
            o.AlphaGrad(3, 4, 5, 1, col);
            o.AlphaGrad(3, 4, 4, 3, col);

            o.Set(2, 5, col);
            o.Set(3, 5, col);
            o.Set(4, 5, col);
            o.Set(5, 5, col);

            o.Set(4, 4, col);
            o.Set(5, 4, col);

            o.Set(5, 2, col);
            o.Set(5, 3, col);
        }

        public static void BlendLineDiagonal(uint col, in OutMatrix o)
        {
            o.AlphaGrad(1, 2, 5, 3, col); // ref<scale - 1, scale / 2>
            o.AlphaGrad(1, 2, 4, 4, col); // ref<scale - 2, scale / 2 + 1>
            o.AlphaGrad(1, 2, 3, 5, col); // ref<scale - 3, scale / 2 + 2>

            o.Set(4, 5, col);
            o.Set(5, 5, col);
            o.Set(5, 4, col);
        }

        public static void BlendCorner(uint col, in OutMatrix o)
        {
            o.AlphaGrad(97, 100, 5, 5, col); // exact: 0.9711013910
            o.AlphaGrad(42, 100, 4, 5, col); // 0.4236372243
            o.AlphaGrad(42, 100, 5, 4, col); // 0.4236372243
            o.AlphaGrad(6, 100, 5, 3, col);  // 0.05652034508
            o.AlphaGrad(6, 100, 3, 5, col);  // 0.05652034508
        }
    }

    /*
    3x3 input kernel area naming convention (input pixel at E):
    | A | B | C |
    | D | E | F |
    | G | H | I |
    */
    static void BlendPixel<TScaler>(
        uint kA, uint kB, uint kC, uint kD, uint kE, uint kF, uint kG, uint kH, uint kI,
        uint[] trg, int outBase, int trgWidth, byte blendInfo, int rotDeg)
        where TScaler : struct, IScaler
    {
        // rotated getters (get_a<rotDeg>..get_i<rotDeg> in the original)
        uint a, b, c, d, e, f, g, h, i;
        switch (rotDeg)
        {
            case 1: // ROT_90
                a = kG; b = kD; c = kA; d = kH; e = kE; f = kB; g = kI; h = kF; i = kC;
                break;
            case 2: // ROT_180
                a = kI; b = kH; c = kG; d = kF; e = kE; f = kD; g = kC; h = kB; i = kA;
                break;
            case 3: // ROT_270
                a = kC; b = kF; c = kI; d = kB; e = kE; f = kH; g = kA; h = kD; i = kG;
                break;
            default:
                a = kA; b = kB; c = kC; d = kD; e = kE; f = kF; g = kG; h = kH; i = kI;
                break;
        }
        _ = a; // unused, kept to mirror the original kernel

        byte blend = RotateBlendInfo(blendInfo, rotDeg);

        if (GetBottomR(blend) >= BlendNormal)
        {
            bool doLineBlend;
            if (GetBottomR(blend) >= BlendDominant)
                doLineBlend = true;
            // make sure there is no second blending in an adjacent rotation for this pixel: handles insular pixels, mario eyes
            else if (GetTopR(blend) != BlendNone && !Eq(e, g)) // but support double-blending for 90 degree corners
                doLineBlend = false;
            else if (GetBottomL(blend) != BlendNone && !Eq(e, c))
                doLineBlend = false;
            // no full blending for L-shapes; blend corner only (handles "mario mushroom eyes")
            else if (!Eq(e, i) && Eq(g, h) && Eq(h, i) && Eq(i, f) && Eq(f, c))
                doLineBlend = false;
            else
                doLineBlend = true;

            uint px = Dist(e, f) <= Dist(e, h) ? f : h; // choose most similar color

            var o = new OutMatrix(trg, outBase, trgWidth, rotDeg, TScaler.Scale);

            if (doLineBlend)
            {
                double fg = Dist(f, g);
                double hc = Dist(h, c);

                bool haveShallowLine = SteepDirectionThreshold * fg <= hc && e != g && d != g;
                bool haveSteepLine = SteepDirectionThreshold * hc <= fg && e != c && b != c;

                if (haveShallowLine)
                {
                    if (haveSteepLine)
                        TScaler.BlendLineSteepAndShallow(px, o);
                    else
                        TScaler.BlendLineShallow(px, o);
                }
                else
                {
                    if (haveSteepLine)
                        TScaler.BlendLineSteep(px, o);
                    else
                        TScaler.BlendLineDiagonal(px, o);
                }
            }
            else
                TScaler.BlendCorner(px, o);
        }
    }

    static void ScaleImage<TScaler>(uint[] src, uint[] trg, int srcWidth, int srcHeight)
        where TScaler : struct, IScaler
    {
        if (srcWidth <= 0 || srcHeight <= 0)
            return;

        int scale = TScaler.Scale;
        int trgWidth = srcWidth * scale;

        // The original reuses the tail of the target image as this byte buffer; a separate
        // array is equivalent (and it is zero-initialized, as std::fill does there).
        // The yFirst > 0 pre-pass of the original is only for slice processing and is
        // unreachable here (we always scale the full image).
        var preProcBuffer = new byte[srcWidth];

        for (int y = 0; y < srcHeight; ++y)
        {
            int outBase = scale * y * trgWidth;

            int sM1 = srcWidth * Math.Max(y - 1, 0);
            int s0 = srcWidth * y; // center line
            int sP1 = srcWidth * Math.Min(y + 1, srcHeight - 1);
            int sP2 = srcWidth * Math.Min(y + 2, srcHeight - 1);

            byte blendXy1 = 0; // corner blending for current (x, y + 1) position

            for (int x = 0; x < srcWidth; ++x, outBase += scale)
            {
                int xM1 = Math.Max(x - 1, 0);
                int xP1 = Math.Min(x + 1, srcWidth - 1);
                int xP2 = Math.Min(x + 2, srcWidth - 1);

                // Kernel 4 by 4
                uint kA = src[sM1 + xM1], kB = src[sM1 + x], kC = src[sM1 + xP1], kD = src[sM1 + xP2];
                uint kE = src[s0 + xM1], kF = src[s0 + x], kG = src[s0 + xP1], kH = src[s0 + xP2];
                uint kI = src[sP1 + xM1], kJ = src[sP1 + x], kK = src[sP1 + xP1], kL = src[sP1 + xP2];
                uint kN = src[sP2 + x], kO = src[sP2 + xP1];

                // evaluate the four corners on bottom-right of current pixel
                byte blendXy;
                {
                    BlendResult res = PreProcessCorners(kB, kC, kE, kF, kG, kH, kI, kJ, kK, kL, kN, kO);

                    blendXy = preProcBuffer[x];
                    SetBottomR(ref blendXy, res.F); // all four corners of (x, y) have been determined at this point

                    SetTopR(ref blendXy1, res.J);   // set 2nd known corner for (x, y + 1)
                    preProcBuffer[x] = blendXy1;    // store on current buffer position for use on next row

                    blendXy1 = 0;
                    SetTopL(ref blendXy1, res.K);   // set 1st known corner for (x + 1, y + 1)

                    if (x + 1 < srcWidth)           // set 3rd known corner for (x + 1, y)
                        SetBottomL(ref preProcBuffer[x + 1], res.G);
                }

                // fill block of size scale * scale with the given color
                for (int dy = 0; dy < scale; ++dy)
                {
                    int row = outBase + dy * trgWidth;
                    for (int dx = 0; dx < scale; ++dx)
                        trg[row + dx] = kF;
                }

                // blend four corners of current pixel
                if (blendXy != 0)
                {
                    BlendPixel<TScaler>(kA, kB, kC, kE, kF, kG, kI, kJ, kK, trg, outBase, trgWidth, blendXy, 0);
                    BlendPixel<TScaler>(kA, kB, kC, kE, kF, kG, kI, kJ, kK, trg, outBase, trgWidth, blendXy, 1);
                    BlendPixel<TScaler>(kA, kB, kC, kE, kF, kG, kI, kJ, kK, trg, outBase, trgWidth, blendXy, 2);
                    BlendPixel<TScaler>(kA, kB, kC, kE, kF, kG, kI, kJ, kK, trg, outBase, trgWidth, blendXy, 3);
                }
            }
        }
    }
}
